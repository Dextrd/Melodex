// The audio element behind the player bar. Player.cs drives it and hears back about time, ends and errors. A second
// one waits beside it with the next song loaded, while that runs on from this one with no gap (#1); as this one ends,
// that one starts, and they swap places. On an iPhone, the queue is one stream in one element instead (#109): see below.
let audio, spare;
let player;
// A song shown as what's playing while it's still being fetched: see hold.
let holding = false;
let second = -1;
// The song loaded in the spare, and what's said of it once it plays: { token, meta }.
let prepared = null;
// Watching the last moments of a song, to start the next exactly as it ends; and a start in progress.
let watching = 0, handing = null;
// Bumped by every load and stop, so a start of the next song that's overtaken by one of them is called off when it comes.
let epoch = 0;
// How far ahead of the end the next song is started, to make up for the moment it takes to begin.
const LEAD = 0.04;
// How long each element's song is, as the server measured its file. Safari misjudges files that don't say up front, such
// as Ogg Opus, a second or two in (#93): its own idea is only taken when it agrees, being exact to the hundredth.
const lengths = new WeakMap();

function lengthOf(el) {
    const measured = lengths.get(el), own = el.duration;
    if (Number.isFinite(own) && (!(measured > 0) || Math.abs(own - measured) < 1))
        return own;
    return measured > 0 ? measured : own;
}

function element() {
    const el = new Audio();
    el.preload = "auto";
    el.volume = wanted;
    // Only the one playing is heard from: the spare loading or the one just finished say nothing.
    const on = (name, handler) => el.addEventListener(name, e => { if (el === audio) handler(e); });
    for (const name of ["loadedmetadata", "canplay", "playing", "waiting", "stalled", "pause", "seeking", "ended", "error"])
        on(name, () => {
            if (!stream)
                return;
            note(`${name} at ${el.currentTime.toFixed(2)}, ready ${el.readyState}${el.error ? `, error ${el.error.code} ${el.error.message}` : ""}`);
            if (name === "playing")
                stream.started = true;
            // Wherever it's gone, the stream's filled from there.
            if (name === "seeking")
                pump(stream);
            if (name === "error")
                report("The stream hit an error on this device.");
        });
    on("timeupdate", () => {
        // While seeking, the time can still be the old one, which would put the seek bar back.
        if (audio.seeking)
            return;
        // Once a second is plenty for a seek bar, and keeps calls into .NET down.
        if (stream) {
            reach();
            pump(stream);
        }
        const now = Math.floor(here());
        if (now !== second) {
            second = now;
            player.invokeMethodAsync("OnTime", here(), songLength());
            position();
        }
        if (prepared && !watching && Number.isFinite(lengthOf(audio)) && lengthOf(audio) - audio.currentTime < 1.5)
            watch();
    });
    // Where a seek landed, at once, which may not be quite where it was asked for; the lyrics jump there too.
    on("seeked", () => {
        second = Math.floor(here());
        player.invokeMethodAsync("OnTime", here(), songLength());
        position();
        if (lyrics?.isConnected)
            light(true);
    });
    on("ended", () => {
        unwatch();
        // A start of the next one still going decides: on its way, it's played; refused, it's as if there were none.
        if (handing)
            handing.then(started => { if (started === false) player.invokeMethodAsync("OnEnded"); });
        else
            player.invokeMethodAsync("OnEnded");
    });
    on("playing", () => { player.invokeMethodAsync("OnPlaying", true); state("playing"); position(); });
    on("pause", () => {
        if (audio.ended)
            return;
        player.invokeMethodAsync("OnPlaying", false);
        state("paused");
        position();
    });
    on("durationchange", position);
    // A stream run dry: what was in it may have been let go of to save memory, and it's given it again.
    on("waiting", () => { if (stream) pump(stream); });
    on("error", () => {
        if (audio.getAttribute("src"))
            player.invokeMethodAsync("OnError", audio.error?.message || "The file couldn't be played.");
    });
    return el;
}

export function init(dotnet) {
    player = dotnet;
    if (audio)
        return;
    document.addEventListener("keydown", shortcut);
    audio = element();
    spare = element();
}

// Media keys, and the lock screen and Control Centre on a phone: taken as each song is loaded, and given back when the
// music stops, so a phone no longer shows Melodex with nothing to play (#98).
const actions = {
    // Nothing to play while the song's being fetched.
    play: () => { if (!holding) audio.play(); },
    pause: () => audio.pause(),
    previoustrack: () => player.invokeMethodAsync("OnRemote", "previous"),
    // The next song is started at once if it's loaded already, as it is to run on without a gap: a locked phone runs the
    // page slowly, and asking for it first took a while (#103).
    nexttrack: () => {
        if (stream?.next && ready(stream.next))
            skip();
        else if (stream)
            player.invokeMethodAsync("OnRemote", "next");
        else if (prepared && !handing && !audio.paused)
            handOff();
        else if (!handing)
            player.invokeMethodAsync("OnRemote", "next");
    },
    // Dragging along the phone's bar: once Melodex says which buttons it has, an iPhone offers only those.
    seekto: details => {
        if (holding)
            return;
        const to = (stream ? stream.now.start : 0) + details.seekTime;
        if (details.fastSeek && audio.fastSeek)
            audio.fastSeek(to);
        else
            audio.currentTime = to;
    },
    // Said outright to be none: an iPhone otherwise offers buttons that jump 10 seconds, rather than previous and next
    // song (#99).
    seekbackward: null,
    seekforward: null,
};

function controls(on) {
    if (!("mediaSession" in navigator))
        return;
    for (const [action, handler] of Object.entries(actions)) {
        try {
            navigator.mediaSession.setActionHandler(action, on ? handler : null);
        } catch {
            // An action this browser doesn't know.
        }
    }
    if (!on) {
        navigator.mediaSession.playbackState = "none";
        try {
            navigator.mediaSession.setPositionState();
        } catch {
        }
    }
}

// Whether it's playing, told to the phone's controls: left to themselves, they showed a song skipped to as paused while
// it played (#103). (They'd once shown the music paused as it started, but that was the next song waiting, muted since.)
function state(value) {
    if ("mediaSession" in navigator)
        navigator.mediaSession.playbackState = value;
}

// How long the song is and where it's got to, told to the phone's controls: they'd go by the browser's own idea of the
// length otherwise, which Safari gets wrong for some files (#103).
function start(length) {
    if (!("mediaSession" in navigator) || !navigator.mediaSession.setPositionState)
        return;
    try {
        if (length > 0)
            navigator.mediaSession.setPositionState({ duration: length, playbackRate: 1, position: 0 });
        else
            navigator.mediaSession.setPositionState();
    } catch {
    }
}

function position() {
    const length = songLength();
    if (!("mediaSession" in navigator) || !navigator.mediaSession.setPositionState || !(length > 0) || !Number.isFinite(length))
        return;
    try {
        navigator.mediaSession.setPositionState({ duration: length, playbackRate: audio.playbackRate || 1, position: Math.min(Math.max(here(), 0), length) });
    } catch {
        // A length or time it won't take, for a moment while a song loads.
    }
}

// Covers as the phone's controls are given them: where they really are, as an iPhone didn't always follow the cover URL
// on to its image, and how big they are, by URL.
const artwork = new Map();

function setMetadata(meta) {
    if (!("mediaSession" in navigator && "MediaMetadata" in window))
        return;
    const show = art => navigator.mediaSession.metadata = meta ? new MediaMetadata({
        title: meta.title, artist: meta.artist, album: meta.album ?? "", artwork: art,
    }) : null;
    if (!meta?.cover) {
        show([]);
        return;
    }
    const url = new URL(meta.cover, document.baseURI).href;
    const known = artwork.get(url);
    show(known ?? [{ src: url }]);
    if (known)
        return;
    // Shown again once it's found, if it's still this song.
    const image = new Image();
    image.onload = () => {
        const art = [{ src: image.currentSrc || image.src, sizes: `${image.naturalWidth}x${image.naturalHeight}` }];
        artwork.set(url, art);
        if (navigator.mediaSession.metadata?.title === meta.title && navigator.mediaSession.metadata?.artwork?.[0]?.src === url)
            show(art);
    };
    fetch(url).then(response => {
        if (response.ok) {
            image.src = response.url;
            artwork.set(url, [{ src: response.url }]);
        }
    }).catch(() => { });
}

// iPhones and iPads, in any browser: all are WebKit. After a song started in the other element, iOS sometimes kept its
// lock screen controls on the one before, showing the music paused and not following seeks (#107). Nothing a page does
// decides which it follows, so songs play one after another in one element there, without gapless.
const ios = /iP(hone|od|ad)/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

export function gaplessWorks() {
    return !ios || streams;
}

// On iPhones and iPads the volume is the phone's own: a page can't set it, so songs can't be evened out there (#23).
export function volumeWorks() {
    return !ios;
}

/** Loads the next song into the spare, to start as this one ends. */
export function prepare(url, meta, token, length, level = 1) {
    levels.set(spare, level);
    if (stream) {
        follow(url, meta, token, length);
        return;
    }
    if (ios) {
        // Its first byte only, so the server has it ready (a WebM copy made, for one) and it starts quickly.
        fetch(source(url), { headers: { Range: "bytes=0-0" } }).catch(() => { });
        return;
    }
    prepared = { token, meta };
    // Muted until it plays: a phone offers its controls to any player with sound loaded, and an iPhone chose this one,
    // waiting, showing the music paused while the other played (#94).
    spare.muted = true;
    lengths.set(spare, length);
    url = source(url);
    if (spare.getAttribute("src") !== url) {
        spare.src = url;
        spare.load();
    }
}

/** Forgets the song loaded ahead: the queue has changed, or it's no longer to run on from this one. */
export function unprepare() {
    if (stream)
        dropNext(stream);
    prepared = null;
    unwatch();
    if (spare.getAttribute("src")) {
        spare.removeAttribute("src");
        spare.load();
    }
}

// Close to the end, the time is checked often enough to start the next song within a few hundredths of a second.
function watch() {
    watching = setInterval(() => {
        if (!prepared || audio.paused || audio.seeking)
            return;
        const left = lengthOf(audio) - audio.currentTime;
        // Sought back from the end: watched again when it gets there.
        if (left > 2)
            unwatch();
        else if (left <= LEAD)
            handOff();
    }, 10);
}

function unwatch() {
    clearInterval(watching);
    watching = 0;
}

function handOff() {
    unwatch();
    const from = audio, to = spare, next = prepared, at = epoch;
    prepared = null;
    turn(to);
    to.muted = false;
    handing = to.play().then(() => {
        // Another song was chosen, or the music stopped, while this one was starting (a phone can take a while): it's
        // stopped again, or both would play.
        if (at !== epoch) {
            to.pause();
            to.removeAttribute("src");
            to.load();
            return null;
        }
        audio = to;
        spare = from;
        second = -1;
        // What's left of the last one is a moment, and the next has it covered.
        from.pause();
        from.removeAttribute("src");
        from.load();
        setMetadata(next.meta);
        state("playing");
        position();
        player.invokeMethodAsync("OnPlaying", true);
        player.invokeMethodAsync("OnAdvanced", next.token);
        return true;
    }, () => {
        if (at !== epoch)
            return null;
        // Not allowed to start it without a tap, as some phones won't: the song ends, and the next loads as it always has.
        to.removeAttribute("src");
        to.load();
        return false;
    }).finally(() => { handing = null; });
}

// Keyboard shortcuts, anywhere but a text box. The player's need something in the queue; "/" opens Search.
const keys = {
    " ": "toggle", k: "toggle", K: "toggle",
    ArrowRight: "forward", ArrowLeft: "back",
    n: "next", N: "next", p: "previous", P: "previous",
    s: "shuffle", S: "shuffle", r: "repeat", R: "repeat",
};

function shortcut(e) {
    if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || e.isComposing)
        return;
    const target = e.target instanceof Element ? e.target : document.body;
    if (target.closest("input, textarea, select, [contenteditable]:not([contenteditable=false])"))
        return;
    // Nothing behind an open dialog.
    if (document.querySelector(".dialog-backdrop, dialog[open]"))
        return;
    if (e.key === "/") {
        e.preventDefault();
        Blazor.navigateTo("search");
        return;
    }
    let action = keys[e.key];
    if (!action || !document.querySelector(".player-bar"))
        return;
    // Shift with the arrows goes from song to song rather than through one.
    if (e.shiftKey && (e.key === "ArrowRight" || e.key === "ArrowLeft"))
        action = e.key === "ArrowRight" ? "next" : "previous";
    // Space on a focused button or link presses it, as it always does.
    if (e.key === " " && target.closest("button, a, [role=slider]"))
        return;
    e.preventDefault();
    player.invokeMethodAsync("OnKey", action);
}

/** Whether the browser expects to play this type; an empty type is given the benefit of the doubt. */
export function canPlay(type) {
    // Anything goes in the stream: the server makes AAC of what the browser can't take.
    if (streams)
        return true;
    if (safari && /^audio\/ogg;\s*codecs="?opus/.test(type ?? ""))
        type = "audio/webm; codecs=opus";
    return !type || (audio ?? new Audio()).canPlayType(type) !== "";
}

// Safari, on any device, and every browser on an iPhone. It gets the time of Opus in an Ogg file wrong, stopping short of
// the end while the music plays on (#93), so it's sent such songs in WebM instead; the server repacks them.
const safari = navigator.vendor?.startsWith("Apple") ?? false;

function source(url) {
    return safari && url.includes("/stream?") ? url + "&as=webm" : url;
}

// ---- The queue as one stream, on iPhones (#109) ----

// On an iPhone, a song started in another element took the lock screen's controls with it or didn't, as iOS chose
// (#107), so songs there played one at a time, each loaded as the last ended, with a gap between. Now the queue is one
// stream in the one element, through Media Source Extensions (ManagedMediaSource, from iOS 17.1): each song is fetched
// in fragmented MP4, in pieces of two seconds or so, and the stream is kept full a minute or so on from where it's
// playing, running on from one song's last piece into the next's first with no gap. An iPhone holds only a couple of
// minutes of it, and lets go of what it likes, so the pieces are kept here and given it again as they're needed: after a
// seek, or once it's let go of them. Where there's no ManagedMediaSource, songs play one at a time as before.
// "melodex.stream" set to "on" in a browser's storage plays it this way in any browser with Media Source Extensions.
const Source = window.ManagedMediaSource ?? (forced() ? window.MediaSource : undefined);
const streams = !!Source && (ios || forced()) && Source.isTypeSupported('audio/mp4; codecs="mp4a.40.2"');
// The sound this browser takes in MP4 as it is; the server makes AAC of anything else.
const keeps = streams
    ? Object.entries({ flac: "flac", opus: "opus", alac: "alac", mp3: "mp4a.6B" })
        .filter(([, codec]) => Source.isTypeSupported(`audio/mp4; codecs="${codec}"`)).map(([name]) => name)
    : [];
// Songs that couldn't be fetched for the stream, by path: they're played on their own instead.
const failed = new Set();
// Given up on for this visit, once a stream didn't start: songs play one at a time instead.
let unstreamable = false;
// How many seconds of the stream are kept ahead of where it's playing, and behind. Less is kept ahead once it's full.
const AHEAD = 60, BEHIND = 10;
// Songs fetched for the stream, by path, the latest last: the playing one, the next and the one before, so going back or
// on to them needs nothing fetched again. Each is { type, init, pieces: [{ t, bytes }], complete, failed }, the pieces
// with when in the song they start.
const fetched = new Map();
// The stream: { source, buffer, type, open, now, next, run, ahead, from, work, object }, from being where it's to start
// once it can. Its songs, the one playing and the one after, are each { url, meta, token, length, data, start, end,
// added }, starting and ending at times along it; run is the song and piece to add next to carry on from the last.
// The buffer's changed one change at a time (work).
let stream = null;
// The stream's last few steps, put in the server's log if a song doesn't start in it: there's no seeing a phone's
// console without a computer beside it.
const trail = [];

function note(step) {
    trail.push(`${(performance.now() / 1000).toFixed(2)}s ${step}`);
    if (trail.length > 150)
        trail.shift();
}

function report(message) {
    fetch("api/v1/client-errors", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        // The latest steps, as the server keeps only the first 4,000 characters.
        body: JSON.stringify({ message, details: `${navigator.userAgent}\nKept as they are: ${keeps.join(", ") || "none"}\n${trail.join("\n").slice(-3600)}`, page: location.pathname }),
    }).catch(() => { });
}

function forced() {
    try {
        return localStorage.getItem("melodex.stream") === "on";
    } catch {
        return false;
    }
}

function path(url) {
    return url.split("?")[0];
}

// Where the song playing has got to, and how long it is; in the stream, from where it starts along it.
function here() {
    return stream ? stream.from ?? Math.max(audio.currentTime - stream.now.start, 0) : audio.currentTime;
}

function songLength() {
    if (!stream) {
        const length = lengthOf(audio);
        return Number.isFinite(length) ? length : 0;
    }
    const song = stream.now;
    return song.length > 0 ? song.length : song.end != null ? song.end - song.start : 0;
}

function song(url, meta, token, length) {
    return { url, meta, token, length, data: get(url), start: null, end: null, added: false };
}

// ---- Fetching songs, a piece at a time ----

/** The song's pieces, fetched already or as they come. */
function get(url) {
    const key = path(url);
    let data = fetched.get(key);
    fetched.delete(key);
    if (!data || data.failed) {
        data = { type: null, init: null, pieces: [], complete: false, failed: null, controller: new AbortController() };
        download(url, data);
    }
    fetched.set(key, data);
    for (const [old, kept] of fetched) {
        if (fetched.size <= 3)
            break;
        if (kept === data || stream && (stream.now.data === kept || stream.next?.data === kept))
            continue;
        kept.controller.abort();
        fetched.delete(old);
    }
    return data;
}

async function download(url, data) {
    try {
        const response = await fetch(url + "&as=mp4" + (keeps.length ? "&keep=" + keeps.join(",") : ""), {
            signal: data.controller.signal,
            // Checked with the server each time, in case the song came as it is before its copy could be made.
            cache: "no-cache",
        });
        const type = response.headers.get("Content-Type") ?? "";
        note(`fetching ${path(url)}: ${response.status} ${type}`);
        if (!response.ok || !type.startsWith("audio/mp4") || !Source.isTypeSupported(type))
            throw new Error(`Not a song for the stream: ${response.status} ${type}`);
        data.type = type;
        const reader = response.body.getReader();
        let held = new Uint8Array(0);
        for (; ;) {
            const { done, value } = await reader.read();
            if (value) {
                held = split(data, held.length ? join([held, value], held.length + value.length) : value);
                heard(data);
            }
            if (done)
                break;
        }
        data.complete = true;
        note(`fetched ${path(url)}: ${data.pieces.length} pieces`);
    } catch (e) {
        data.failed = e;
        if (e?.name !== "AbortError") {
            failed.add(path(url));
            note(`couldn't fetch ${path(url)}: ${e?.name} ${e?.message}`);
            report("A song couldn't be fetched for the stream.");
        }
    }
    heard(data);
}

// MP4's boxes: a 4-byte size (or 1, and an 8-byte one after the name) and a 4-letter name.
function boxes(bytes, from, to, each) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let at = from;
    while (to - at >= 8) {
        let size = view.getUint32(at), header = 8;
        if (size === 1) {
            if (to - at < 16)
                break;
            size = Number(view.getBigUint64(at + 8));
            header = 16;
        }
        if (size < header || to - at < size)
            break;
        if (each(String.fromCharCode(...bytes.subarray(at + 4, at + 8)), at, at + header, at + size, view) === false)
            break;
        at += size;
    }
    return at;
}

/** The box at this path of names within these bytes: [start of its contents, its end], or null. */
function find(bytes, names, from = 0, to = bytes.length) {
    let found = null;
    boxes(bytes, from, to, (name, start, body, end) => {
        if (name !== names[0])
            return true;
        found = names.length === 1 ? [body, end] : find(bytes, names.slice(1), body, end);
        return false;
    });
    return found;
}

// Takes the whole boxes from the bytes fetched so far: the first part (ftyp and moov) and the pieces (moof and mdat);
// what's left is kept for when the rest of it comes.
function split(data, bytes) {
    const used = boxes(bytes, 0, bytes.length, (name, start, body, end, view) => {
        const box = bytes.slice(start, end);
        if (name === "ftyp")
            data.head = box;
        else if (name === "moov") {
            data.init = data.head ? join([data.head, box], data.head.length + box.length) : box;
            // mdhd: its version, then times as long as it says, then the timescale.
            const mdhd = find(box, ["moov", "trak", "mdia", "mdhd"]);
            data.timescale = mdhd ? view.getUint32(start + mdhd[0] + (box[mdhd[0]] === 1 ? 20 : 12)) : 1000;
        } else if (name === "moof")
            data.moof = box;
        else if (name === "mdat" && data.moof) {
            // tfdt: when the piece starts, in the timescale.
            const tfdt = find(data.moof, ["moof", "traf", "tfdt"]);
            const moof = new DataView(data.moof.buffer);
            const time = !tfdt ? 0 : data.moof[tfdt[0]] === 1 ? Number(moof.getBigUint64(tfdt[0] + 4)) : moof.getUint32(tfdt[0] + 4);
            data.pieces.push({ t: time / (data.timescale || 1000), bytes: join([data.moof, box], data.moof.length + box.length) });
            data.moof = null;
        }
    });
    return bytes.slice(used);
}

// Only the songs in the stream are fetched: a phone has few connections to the server, and quick skips each left one
// fetching (often waiting on the server making its copy), so what the player asked of the server waited behind them.
// Those fetched already are kept.
function prune() {
    for (const [key, data] of fetched)
        if (!data.complete && !data.failed && stream?.now.data !== data && stream?.next?.data !== data) {
            data.controller.abort();
            fetched.delete(key);
        }
}

// More of a song came, or it failed: the stream it's in is kept full with it.
function heard(data) {
    const s = stream;
    if (!s)
        return;
    if (data.failed && data.failed.name !== "AbortError") {
        if (s.now.data === data && !s.now.added)
            alone(s.now.url);
        else if (s.next?.data === data)
            dropNext(s);
        return;
    }
    if (s.now.data === data || s.next?.data === data)
        pump(s);
}

// ---- The stream ----

/** Starts a stream with this song. */
function begin(url, meta, length) {
    close();
    const source = new Source();
    const s = stream = {
        source, buffer: null, type: null, open: false, now: song(url, meta, null, length), next: null, run: null, ahead: AHEAD,
        from: null, stuck: 0, leave: new Set(), work: Promise.resolve(), object: URL.createObjectURL(source),
    };
    s.now.start = 0;
    prune();
    // A ManagedMediaSource won't open without it: AirPlay would need the music from somewhere else.
    audio.disableRemotePlayback = true;
    note(`begin ${path(url)}`);
    for (const name of ["sourceopen", "sourceended", "sourceclose", "startstreaming", "endstreaming"])
        source.addEventListener(name, () => note(`${name}${stream === s ? "" : " (old stream)"}`));
    source.addEventListener("sourceopen", () => {
        s.open = true;
        pump(s);
    }, { once: true });
    audio.src = s.object;
    // Looked at every second too, whatever it's heard: a stream left short by quick seeks, or let go of while nothing
    // was said, is filled again.
    s.beat = setInterval(() => {
        if (audio.paused && !audio.seeking) {
            s.still = 0;
            return;
        }
        pump(s);
        if (audio.currentTime !== s.last) {
            s.last = audio.currentTime;
            s.still = 0;
            return;
        }
        // Not moving though it should be. With what it needs there, WebKit can sit after a seek until it's asked again:
        // it's asked again. Without, it's said in the server's log, once.
        s.still++;
        if (s.still % 3 === 0 && ahead(s) > 0.5) {
            note(`sat at ${audio.currentTime.toFixed(2)}, seeking ${audio.seeking}, ready ${audio.readyState}, with ${ranges(s.buffer?.buffered)}: asked again`);
            audio.currentTime = audio.currentTime;
        } else if (s.still === 8 && !s.told) {
            s.told = true;
            note(`sat at ${audio.currentTime.toFixed(2)}, seeking ${audio.seeking}, ready ${audio.readyState}, with ${ranges(s.buffer?.buffered)}`);
            report("The stream stopped moving on this device.");
        }
    }, 1000);
    // Not started a while after its song came, though asked to play: given up on, and played the old way. The server
    // may take a while to make the song's copy the first time.
    const watch = () => s.watchdog = setTimeout(() => {
        if (stream !== s || s.started || audio.paused)
            return;
        if (!s.now.added)
            return watch();
        note("given up");
        report("A song didn't start in the stream on this device; it's played on its own instead.");
        unstreamable = true;
        const from = here();
        alone(s.now.url);
        seek(from);
    }, 15000);
    watch();
}

/** Ends the stream, as another song plays on its own or the music stops. */
function close() {
    const s = stream;
    if (!s)
        return;
    stream = null;
    clearTimeout(s.watchdog);
    clearTimeout(s.retry);
    clearInterval(s.beat);
    URL.revokeObjectURL(s.object);
    audio.disableRemotePlayback = false;
}

// The song played on its own, the old way.
function alone(url) {
    close();
    audio.src = source(url);
    play();
}

/** The song to run on into after this one; one there already that isn't it is taken out first. */
function follow(url, meta, token, length) {
    const s = stream, next = s.next;
    if (next && path(next.url) === path(url)) {
        Object.assign(next, { meta, token, length });
        return;
    }
    if (next)
        dropNext(s);
    // Played on its own when it comes.
    if (failed.has(path(url))) {
        finish(s);
        return;
    }
    s.next = song(url, meta, token, length);
    s.next.start = s.now.end;
    prune();
    pump(s);
}

/** Takes the song after this one out of the stream. */
function dropNext(s) {
    const next = s.next;
    if (!next)
        return;
    s.next = null;
    if (s.run?.song === next)
        s.run = null;
    if (next.start != null && next.added)
        change(s, b => {
            b.remove(next.start, Infinity);
            return true;
        }).catch(() => { });
    finish(s);
}

/**
 * Keeps the stream full from where it's playing: what's there runs on, a piece at a time, as far ahead as it holds;
 * what's well behind is let go of. Once at a time; asked again while it's at it, it goes round again.
 */
async function pump(s) {
    if (s.pumping) {
        s.again = true;
        return;
    }
    s.pumping = true;
    try {
        do {
            s.again = false;
            await fill(s);
        } while (s.again && stream === s);
    } catch (e) {
        if (stream === s) {
            note(`couldn't add to the stream: ${e?.name} ${e?.message}`);
            report("The stream couldn't take a song on this device.");
        }
    } finally {
        s.pumping = false;
    }
}

async function fill(s) {
    for (; ;) {
        if (stream !== s || !s.open)
            return;
        const at = s.from ?? audio.currentTime;
        const buffered = s.buffer?.buffered;
        const end = edge(s, at);
        // Well behind, or cut off ahead of what runs on from here (left from before a seek): let go of. What Safari
        // won't let go of (a scrap of a piece, after quick seeks) is left, or it'd be asked for ever and nothing added.
        const behind = buffered?.length && buffered.start(0) < at - BEHIND - 5 && !s.leave.has(buffered.start(0).toFixed(2))
            ? [buffered.start(0), at - BEHIND] : null;
        const after = !behind && buffered?.length
            ? [...Array(buffered.length).keys()].map(i => buffered.start(i)).find(start => start > end + 0.15 && !s.leave.has(start.toFixed(2)))
            : null;
        if (behind || after != null) {
            const [from, to] = behind ?? [after, Infinity], was = ranges(buffered);
            await change(s, b => {
                b.remove(behind ? 0 : from, to);
                return true;
            });
            if (ranges(s.buffer.buffered) === was) {
                s.leave.add(from.toFixed(2));
                note(`couldn't let go of ${from.toFixed(2)}–${to.toFixed(2)} of ${was}`);
            }
            continue;
        }
        // As long as its songs, so a seek can go past what's there yet.
        const length = Math.max(...[s.now, s.next].map(song => song?.start != null && song.length > 0 ? song.start + song.length : 0));
        if (s.source.readyState === "open" && length > 0 && !(s.source.duration >= length) && !s.buffer?.updating) {
            await change(s, () => {
                s.source.duration = length;
                return false;
            });
            continue;
        }
        if (end - at >= s.ahead)
            return;
        const place = locate(s, end);
        if (!place) {
            finish(s);
            return;
        }
        const { song, index } = place, data = song.data, piece = data.pieces[index];
        // Not fetched yet: here again when it is.
        if (!piece || !data.init)
            return;
        try {
            // A run from somewhere new starts with the song's first part, its time moved along to where it starts.
            if (s.run?.song !== song || s.run.index !== index)
                await change(s, b => {
                    if (!b) {
                        b = s.buffer = s.source.addSourceBuffer(data.type);
                        b.mode = "segments";
                        b.addEventListener("bufferedchange", e => {
                            if (e.removedRanges?.length)
                                note(`let go of ${ranges(e.removedRanges)}, now ${ranges(b.buffered)}`);
                        });
                    } else if (s.type !== data.type)
                        b.changeType(data.type);
                    s.type = data.type;
                    b.timestampOffset = song.start;
                    b.appendBuffer(data.init);
                    return true;
                });
            await change(s, b => {
                b.appendBuffer(piece.bytes);
                return true;
            });
        } catch (e) {
            if (e?.name !== "QuotaExceededError")
                throw e;
            // Full: less is kept ahead from now on, and it's tried again in a moment.
            s.ahead = Math.max(10, Math.min(s.ahead, end - at) - 5);
            s.run = null;
            note(`full at ${end.toFixed(2)}: ${s.ahead.toFixed(0)}s kept ahead`);
            clearTimeout(s.retry);
            s.retry = setTimeout(() => pump(s), 1000);
            return;
        }
        s.run = { song, index: index + 1 };
        // A piece that didn't go where it was meant to would be added for ever: left a moment, and tried again.
        if (edge(s, at) > end + 0.01)
            s.stuck = 0;
        else if (++s.stuck > 3) {
            note(`stuck at ${end.toFixed(2)}`);
            s.stuck = 0;
            s.run = null;
            return;
        }
        if (!song.added) {
            song.added = true;
            note(`added ${path(song.url)} from ${(song.start + piece.t).toFixed(2)}`);
        }
        if (s.from != null && song === s.now && audio.readyState > 0) {
            audio.currentTime = song.start + s.from;
            s.from = null;
        }
        // Its last piece: it ends where that does, and the next starts there.
        if (index + 1 === data.pieces.length && data.complete && song.end == null) {
            song.end = edge(s, song.start + piece.t);
            if (song === s.now && s.next)
                s.next.start = song.end;
            note(`${path(song.url)} ends at ${song.end.toFixed(2)}`);
        }
    }
}

/** The song and piece to add to carry on from this time along the stream; null if there's nothing more. */
function locate(s, end) {
    const run = s.run;
    if (run && (run.song === s.now || run.song === s.next)) {
        const { song, index } = run, pieces = song.data.pieces;
        if (index < pieces.length && Math.abs(song.start + pieces[index].t - end) < 0.5)
            return run;
        // Waiting for more of it.
        if (index >= pieces.length && !song.data.complete && pieces.length && end > song.start + pieces[pieces.length - 1].t)
            return run;
    }
    for (const song of [s.now, s.next]) {
        if (!song || song.start == null || end < song.start - 0.1 || song.end != null && end >= song.end - 0.1)
            continue;
        const rel = end - song.start, pieces = song.data.pieces;
        let index = 0;
        while (index + 1 < pieces.length && pieces[index + 1].t <= rel + 0.05)
            index++;
        // Past the last piece there is of it, there's more to come.
        if (pieces.length && index === pieces.length - 1 && !song.data.complete && pieces[index].t + 2.5 < rel)
            index = pieces.length;
        return { song, index };
    }
    return null;
}

/** Where what's there from this time on runs to: the time itself if there's nothing. Gaps of a moment don't count. */
function edge(s, at) {
    let end = at;
    try {
        const buffered = s.buffer.buffered;
        for (let i = 0; i < buffered.length; i++)
            if (buffered.start(i) <= end + 0.15 && buffered.end(i) > end)
                end = buffered.end(i);
    } catch {
    }
    return end;
}

// How much of the stream there is from where it's playing.
function ahead(s) {
    return edge(s, audio.currentTime) - audio.currentTime;
}

/**
 * Changes the buffer once the change before is done: act changes it, saying whether it began something that takes a
 * while (adding or taking out), to be waited for.
 */
function change(s, act) {
    const work = s.work.then(() => new Promise((resolve, reject) => {
        if (stream !== s)
            return reject(new DOMException("The stream has ended.", "AbortError"));
        let began;
        try {
            began = act(s.buffer);
        } catch (e) {
            return reject(e);
        }
        if (!began)
            return resolve();
        const b = s.buffer;
        const settle = ok => () => {
            clearTimeout(late);
            b.removeEventListener("updateend", end);
            b.removeEventListener("error", error);
            ok ? resolve() : reject(new Error("The stream couldn't take it."));
        };
        const end = settle(true), error = settle(false);
        b.addEventListener("updateend", end);
        b.addEventListener("error", error);
        // Not heard back from: not waited for for ever, or nothing more would be added.
        const late = setTimeout(() => {
            note("a change wasn't heard back from");
            settle(false)();
        }, 5000);
    }));
    s.work = work.catch(() => { });
    return work;
}

function ranges(r) {
    const out = [];
    for (let i = 0; r && i < r.length; i++)
        out.push(`${r.start(i).toFixed(2)}–${r.end(i).toFixed(2)}`);
    return out.join(", ") || "nothing";
}

function join(parts, size) {
    const bytes = new Uint8Array(size);
    let at = 0;
    for (const part of parts) {
        bytes.set(part, at);
        at += part.length;
    }
    return bytes;
}

// The stream's said to end with its last song, so the element ends there; more added after starts it again.
function finish(s) {
    s.work.then(() => {
        const last = s.next ?? s.now;
        if (stream === s && s.open && last.end != null && s.source.readyState === "open" && !s.buffer?.updating)
            try {
                s.source.endOfStream();
            } catch {
                // Changed in the meantime: said again when it's done.
            }
    });
}

/** Whether the next song's start is in the stream, to jump to. */
function ready(song) {
    return song.start != null && !!stream && edge(stream, song.start) > song.start + 0.5;
}

// The stream's run on into the next song: it's the one playing now.
function reach() {
    const next = stream.next;
    if (next?.start != null && audio.currentTime >= next.start)
        advance(false);
}

function advance(quiet) {
    const s = stream, to = s.next;
    s.now = to;
    s.next = null;
    second = -1;
    setMetadata(to.meta);
    position();
    if (!quiet)
        player.invokeMethodAsync("OnAdvanced", to.token);
}

/** Jumps to the next song, there in the stream already; quietly when Player.cs chose it. */
function skip(quiet) {
    audio.currentTime = stream.next.start;
    advance(quiet);
}

export function load(url, meta, length, level = 1) {
    epoch++;
    levels.set(audio, level);
    turn(audio);
    holding = false;
    note(`load ${meta?.title}`);
    lengths.set(audio, length);
    second = -1;
    unwatch();
    if (streams && !unstreamable && url.includes("/stream?") && !failed.has(path(url))) {
        // The next song, there already: played on in the stream. Otherwise a new stream, starting with this one.
        if (stream?.next && path(stream.next.url) === path(url) && ready(stream.next)) {
            stream.next.meta = meta;
            skip(true);
        } else
            begin(url, meta, length);
    } else {
        close();
        audio.src = source(url);
    }
    setMetadata(meta);
    controls(true);
    // From the start of the new song at once, rather than where the last one was until this one has loaded.
    start(length);
    play();
}

export function play() {
    if (holding)
        return;
    audio.play().catch(e => {
        if (stream)
            note(`play refused: ${e.name} ${e.message}`);
        // Loading another track first isn't a failure; the browser refusing to start without a click is shown as paused.
        if (e.name !== "AbortError")
            player.invokeMethodAsync("OnPlaying", false);
    });
}

export function pause() {
    audio.pause();
}

export function seek(seconds) {
    // Before a stream has any of its song, where it's to start is kept until it does.
    if (stream && (!stream.open || audio.readyState === 0))
        stream.from = seconds;
    else
        audio.currentTime = (stream ? stream.now.start : 0) + seconds;
}

/** A step of the player's, in the stream's trail. */
export function trace(step) {
    note(step);
}

/**
 * A song still being fetched to listen to: shown as what's playing, paused at its start, until it can play. The phone's
 * controls stay Melodex's, rather than going back to the page's own.
 */
export function hold(meta, length) {
    epoch++;
    holding = true;
    note(`hold ${meta?.title}`);
    unprepare();
    close();
    audio.pause();
    setMetadata(meta);
    controls(true);
    start(length);
    state("paused");
}

export function stop() {
    epoch++;
    holding = false;
    unprepare();
    close();
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    setMetadata(null);
    controls(false);
}

// ---- Volume, kept per browser ----

// Each song's own level from its ReplayGain (#23), the volume chosen times it: an iPhone's volume is the phone's own,
// and stays as it is.
const levels = new WeakMap();
let wanted = storedVolume();

function turn(el) {
    el.volume = Math.min(1, wanted * (levels.get(el) ?? 1));
}

function storedVolume() {
    try {
        const v = Number(localStorage.getItem("melodex.volume"));
        return localStorage.getItem("melodex.volume") !== null && v >= 0 && v <= 1 ? v : 1;
    } catch {
        return 1;
    }
}

export function getVolume() {
    return wanted;
}

export function setVolume(value) {
    const v = Math.min(1, Math.max(0, value));
    wanted = v;
    if (audio) {
        turn(audio);
        turn(spare);
    }
    try {
        localStorage.setItem("melodex.volume", String(v));
    } catch {
    }
}

// ---- The visualiser: bars that move with the music, drawn under the cover in the full-screen player ----

// The bars are drawn from each song's spectrum, worked out by the server (#89, #102): how loud each band of pitch is,
// many times a second, from bass up. It's fetched once for each song and played back with it. Nothing else plays: the
// music sent through Web Audio would go quiet when a phone put the browser in the background, and a second, silent copy
// playing beside it took an iPhone's controls with it, showing the music stopped when Safari was left while it carried
// on (#65).
let frame = 0, canvas, bars = null, barsSrc = null, loading = null;
// Each bar's height now, 0 to 255, falling away while paused.
let levels = new Float32Array(64);

/**
 * The bars for each frame of a spectrum (see the client guide for its format): each band's level smoothed over time,
 * as a Web Audio analyser smooths, from -86 to -16 decibels of full scale over 0 to 255. Null if it isn't a spectrum.
 */
function barsOf(bytes) {
    // MDXB, the version, frames a second, bands a frame, the header's length, ..., the quietest and loudest decibels,
    // a frame's length.
    if (bytes.length < 16 || String.fromCharCode(...bytes.subarray(0, 4)) !== "MDXB" || bytes[4] !== 2)
        return null;
    const fps = bytes[5], count = bytes[6], header = bytes[7];
    const view = new DataView(bytes.buffer, bytes.byteOffset);
    const floor = view.getInt8(12), ceiling = view.getInt8(13), size = view.getUint16(14, true);
    const frames = Math.floor((bytes.length - header) / size);
    const smoothed = new Float32Array(count), out = new Uint8Array(frames * count);
    for (let f = 0; f < frames; f++)
        for (let i = 0; i < count; i++) {
            const level = bytes[header + f * size + i];
            const amplitude = level === 0 ? 0 : Math.pow(10, (floor + level * (ceiling - floor) / 255) / 20);
            smoothed[i] = 0.64 * smoothed[i] + 0.36 * amplitude;
            const db = 20 * Math.log10(Math.max(smoothed[i], 1e-12));
            out[f * count + i] = Math.min(255, Math.max(0, 255 * (db + 86) / 70));
        }
    return { fps, count, frames: out };
}

/** The song at src's bars, from the server: { fps, count, frames }; null until they're here, or if there are none. */
function loadBars(src) {
    if (barsSrc === src || loading?.src === src)
        return;
    bars = null;
    barsSrc = null;
    const controller = new AbortController();
    loading?.controller.abort();
    loading = { src, controller };
    fetch(src.replace("/stream?", "/spectrum?"), { signal: controller.signal })
        .then(response => response.ok ? response.arrayBuffer() : Promise.reject(response.status))
        .then(buffer => {
            if (loading?.src !== src)
                return;
            barsSrc = src;
            bars = barsOf(new Uint8Array(buffer));
            if (bars && levels.length !== bars.count)
                levels = new Float32Array(bars.count);
        })
        // Not asked for again for this song: the bars stay down.
        .catch(() => { if (loading?.src === src) barsSrc = src; })
        .finally(() => { if (loading?.src === src) loading = null; });
}

/** Each bar's height at this moment of the song, between the frames either side of it; with no moment, falling away. */
function spectrum(at) {
    if (at < 0 || !bars || bars.frames.length < bars.count) {
        for (let i = 0; i < levels.length; i++)
            levels[i] *= 0.85;
        return;
    }
    const { fps, count, frames } = bars;
    const last = frames.length / count - 1;
    const position = Math.min(Math.max(at * fps, 0), last);
    const a = Math.floor(position), b = Math.min(a + 1, last), t = position - a;
    for (let i = 0; i < count; i++)
        levels[i] = frames[a * count + i] * (1 - t) + frames[b * count + i] * t;
}

export function startVisualiser(target) {
    canvas = target;
    if (!audio || !canvas)
        return;
    cancelAnimationFrame(frame);
    const draw = () => {
        frame = requestAnimationFrame(draw);
        if (!canvas.isConnected) {
            stopVisualiser();
            return;
        }
        const src = stream ? stream.now.url : audio.getAttribute("src");
        if (src)
            loadBars(src);
        const ratio = window.devicePixelRatio || 1;
        const width = canvas.clientWidth, height = canvas.clientHeight;
        if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
            canvas.width = Math.round(width * ratio);
            canvas.height = Math.round(height * ratio);
        }
        const g = canvas.getContext("2d");
        g.setTransform(ratio, 0, 0, ratio, 0, 0);
        g.clearRect(0, 0, width, height);
        // Still while paused, falling away as they would.
        spectrum(bars && barsSrc === src && !audio.paused ? here() : -1);
        // Mirrored from the middle out: bass in the centre. Each bar drawn is the loudest of the bands it covers, about
        // 40 Hz to 16 kHz on a logarithmic scale, as we hear pitch.
        const half = Math.max(8, Math.floor(width / 14));
        const bar = width / (half * 2);
        g.fillStyle = getComputedStyle(canvas).color;
        for (let i = 0; i < half; i++) {
            const from = Math.floor(i * levels.length / half);
            const to = Math.max(from + 1, Math.floor((i + 1) * levels.length / half));
            let peak = 0;
            for (let b = from; b < to && b < levels.length; b++)
                peak = Math.max(peak, levels[b]);
            const h = Math.max(2, Math.pow(peak / 255, 1.6) * height);
            const r = Math.min(bar * 0.35, 3);
            for (const x of [width / 2 + i * bar, width / 2 - (i + 1) * bar]) {
                g.beginPath();
                g.roundRect(x + bar * 0.15, height - h, bar * 0.7, h, [r, r, 0, 0]);
                g.fill();
            }
        }
    };
    draw();
}

export function stopVisualiser() {
    cancelAnimationFrame(frame);
    frame = 0;
}

// ---- Lyrics that follow along: the line being sung is lit and kept in view ----

let lyrics, lyricsTimer = 0, userScrolledAt = 0, lastActive = -1;

export function followLyrics(container) {
    unfollowLyrics();
    lyrics = container;
    lastActive = -1;
    if (!lyrics)
        return;
    if (!lyrics.dataset.followed) {
        lyrics.dataset.followed = "1";
        // Scrolling them yourself stops them being moved for a few seconds.
        const held = () => userScrolledAt = Date.now();
        lyrics.addEventListener("wheel", held, { passive: true });
        lyrics.addEventListener("touchmove", held, { passive: true });
        lyrics.addEventListener("click", seekToLine);
    }
    const tick = () => {
        lyricsTimer = requestAnimationFrame(tick);
        if (!lyrics?.isConnected) {
            unfollowLyrics();
            return;
        }
        light(false);
    };
    light(true);
    tick();
}

// Clicking a line plays from it.
function seekToLine(e) {
    const line = e.target.closest("[data-time]");
    if (line && audio) {
        seek(Number(line.dataset.time));
        userScrolledAt = 0;
        lastActive = -2;
        if (audio.paused)
            play();
    }
}

function light(jump) {
    const lines = lyrics.querySelectorAll("[data-time]");
    if (lines.length === 0 || !audio)
        return;
    // A little early, as people expect to see a line just before it's sung.
    const now = here() + 0.2;
    let active = -1;
    for (let i = 0; i < lines.length && Number(lines[i].dataset.time) <= now; i++)
        active = i;
    if (active === lastActive && !jump)
        return;
    lastActive = active;
    lines.forEach((line, i) => {
        line.classList.toggle("active", i === active);
        line.classList.toggle("past", i < active);
    });
    if (Date.now() - userScrolledAt > 3000) {
        const target = lines[Math.max(active, 0)];
        const top = target.offsetTop - lyrics.clientHeight * 0.35;
        lyrics.scrollTo({ top, behavior: jump ? "auto" : "smooth" });
    }
}

export function unfollowLyrics() {
    cancelAnimationFrame(lyricsTimer);
    lyricsTimer = 0;
    lyrics = null;
}

// ---- The browser's own full screen, for the full-screen player ----

export async function toggleFullscreen(element) {
    try {
        if (document.fullscreenElement)
            await document.exitFullscreen();
        else
            await element.requestFullscreen();
    } catch {
    }
    return !!document.fullscreenElement;
}

export function isFullscreen() {
    return !!document.fullscreenElement;
}

export function exitFullscreen() {
    if (document.fullscreenElement)
        document.exitFullscreen().catch(() => { });
}
