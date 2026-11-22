// The audio element behind the player bar. Player.cs drives it and hears back about time, ends and errors. A second
// one waits beside it with the next song loaded, while that runs on from this one with no gap (#1); as this one ends,
// that one starts, and they swap places. On an iPhone, the queue is one stream in one element instead (#109): see below.
let audio, spare;
let player;
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
    el.volume = storedVolume();
    // Only the one playing is heard from: the spare loading or the one just finished say nothing.
    const on = (name, handler) => el.addEventListener(name, e => { if (el === audio) handler(e); });
    for (const name of ["loadedmetadata", "canplay", "playing", "waiting", "stalled", "pause", "seeking", "ended", "error"])
        on(name, () => {
            if (!stream)
                return;
            note(`${name} at ${el.currentTime.toFixed(2)}, ready ${el.readyState}${el.error ? `, error ${el.error.code} ${el.error.message}` : ""}`);
            if (name === "playing")
                stream.started = true;
            if (name === "error")
                report("The stream hit an error on this device.");
        });
    on("timeupdate", () => {
        // While seeking, the time can still be the old one, which would put the seek bar back.
        if (audio.seeking)
            return;
        // Once a second is plenty for a seek bar, and keeps calls into .NET down.
        if (stream)
            reach();
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
    // A stream run dry with more of it to come: what was in it may have been let go of to save memory.
    on("waiting", () => { if (stream) stalled(); });
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
    play: () => audio.play(),
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

function setMetadata(meta) {
    if ("mediaSession" in navigator && "MediaMetadata" in window)
        navigator.mediaSession.metadata = meta ? new MediaMetadata({
            title: meta.title, artist: meta.artist, album: meta.album ?? "",
            artwork: meta.cover ? [{ src: new URL(meta.cover, document.baseURI).href }] : [],
        }) : null;
}

// iPhones and iPads, in any browser: all are WebKit. After a song started in the other element, iOS sometimes kept its
// lock screen controls on the one before, showing the music paused and not following seeks (#107). Nothing a page does
// decides which it follows, so songs play one after another in one element there, without gapless.
const ios = /iP(hone|od|ad)/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

export function gaplessWorks() {
    return !ios || streams;
}

/** Loads the next song into the spare, to start as this one ends. */
export function prepare(url, meta, token, length) {
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
    if (stream?.next) {
        drop(stream, stream.next);
        stream.next = null;
    }
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
    to.volume = from.volume;
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
// stream in the one element, through Media Source Extensions (ManagedMediaSource, from iOS 17.1): each song is fetched in
// fragmented MP4 and added to the end, the next as soon as it's known, so it runs on with no gap and skipping to it is a
// jump along the stream, with nothing to load. Where there's no ManagedMediaSource, songs play one at a time as before.
// "melodex.stream" set to "on" in a browser's storage plays it this way in any browser with Media Source Extensions.
const Source = window.ManagedMediaSource ?? (forced() ? window.MediaSource : undefined);
const streams = !!Source && (ios || forced()) && Source.isTypeSupported('audio/mp4; codecs="mp4a.40.2"');
// The sound this browser takes in MP4 as it is; the server makes AAC of anything else.
const keeps = streams
    ? Object.entries({ flac: "flac", opus: "opus", alac: "alac", mp3: "mp4a.6B" })
        .filter(([, codec]) => Source.isTypeSupported(`audio/mp4; codecs="${codec}"`)).map(([name]) => name)
    : [];
// Songs that couldn't be added to the stream, by path: they're played on their own instead.
const failed = new Set();
// The stream: { source, buffer, type, now, next, end, from, songs, work, object }, from being where it's to start. Its
// songs, the one playing and the one after, are each { url, meta, token, length, start, end, controller, added, dropped },
// starting and ending at times along it. Songs are added one after another (songs, a promise of the last), and the
// buffer changed one change at a time (work).
let stream = null;
// How much of a song is fetched before it's added: a few seconds of lossless sound.
const CHUNK = 512 * 1024;
// Given up on for this visit, once a stream didn't start: songs play one at a time instead.
let unstreamable = false;
// The stream's last few steps, put in the server's log if a song doesn't start in it: there's no seeing a phone's
// console without a computer beside it.
const trail = [];

function note(step) {
    trail.push(`${(performance.now() / 1000).toFixed(2)}s ${step}`);
    if (trail.length > 60)
        trail.shift();
}

function report(message) {
    fetch("api/v1/client-errors", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ message, details: `${navigator.userAgent}\nKept as they are: ${keeps.join(", ") || "none"}\n${trail.join("\n")}`, page: location.pathname }),
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
    return { url, meta, token, length, start: null, end: null, controller: new AbortController(), added: false, dropped: false };
}

/** Starts a stream with this song. */
function begin(url, meta, length) {
    close();
    const source = new Source();
    const s = stream = {
        source, buffer: null, type: null, now: song(url, meta, null, length), next: null, end: 0,
        songs: Promise.resolve(), work: Promise.resolve(), object: URL.createObjectURL(source),
    };
    // A ManagedMediaSource won't open without it: AirPlay would need the music from somewhere else.
    audio.disableRemotePlayback = true;
    note(`begin ${path(url)}`);
    for (const name of ["sourceopen", "sourceended", "sourceclose", "startstreaming", "endstreaming"])
        source.addEventListener(name, () => note(`${name}${stream === s ? "" : " (old stream)"}`));
    source.addEventListener("sourceopen", () => add(s, s.now), { once: true });
    audio.src = s.object;
    // Not started a while after its song came, though asked to play: given up on, and played the old way. The server
    // may take a while to make the song's copy the first time.
    const watch = () => s.watchdog = setTimeout(() => {
        if (stream !== s || s.started || audio.paused)
            return;
        if (!s.now.added)
            return watch();
        giveUp(s, "A song didn't start in the stream on this device; it's played on its own instead.");
    }, 15000);
    watch();
}

function giveUp(s, message) {
    note("given up");
    report(message);
    unstreamable = true;
    const { url } = s.now, from = here();
    close();
    audio.src = source(url);
    seek(from);
    play();
}

/** Ends the stream, as another song plays on its own or the music stops. */
function close() {
    const s = stream;
    if (!s)
        return;
    stream = null;
    clearTimeout(s.watchdog);
    s.now.controller.abort();
    s.next?.controller.abort();
    URL.revokeObjectURL(s.object);
    audio.disableRemotePlayback = false;
}

/** The song to run on into after this one, added to the stream; one there already that isn't it is taken out first. */
function follow(url, meta, token, length) {
    const s = stream, next = s.next;
    if (next && path(next.url) === path(url)) {
        Object.assign(next, { meta, token, length });
        return;
    }
    if (next)
        drop(s, next);
    // Played on its own when it comes.
    s.next = failed.has(path(url)) ? null : song(url, meta, token, length);
    if (s.next)
        add(s, s.next);
    else
        finish(s);
}

function add(s, song) {
    s.songs = s.songs.then(() => fill(s, song)).catch(() => { });
}

/** Takes the song after this one out of the stream, or stops it being added. */
function drop(s, song) {
    song.dropped = true;
    song.controller.abort();
    s.songs = s.songs.then(async () => {
        if (song.start == null || stream !== s)
            return;
        await change(s, b => {
            // What was half read of it is forgotten too; there's none once the stream's said to have ended.
            if (s.source.readyState === "open")
                b.abort();
            b.remove(song.start, Infinity);
            return true;
        }).catch(() => { });
        s.end = song.start;
        finish(s);
    });
}

/** Fetches a song and adds it to the end of the stream, a piece at a time as it comes. */
async function fill(s, song) {
    if (song.dropped || stream !== s)
        return;
    song.start = s.end;
    s.filling = true;
    try {
        const response = await fetch(song.url + "&as=mp4" + (keeps.length ? "&keep=" + keeps.join(",") : ""), {
            signal: song.controller.signal,
            // Checked with the server each time, in case the song came as it is before its copy could be made.
            cache: "no-cache",
        });
        const type = response.headers.get("Content-Type") ?? "";
        note(`fetched ${path(song.url)}: ${response.status} ${type}, from ${song.start.toFixed(2)}`);
        if (!response.ok || !type.startsWith("audio/mp4") || !Source.isTypeSupported(type))
            throw new Error(`Not a song for the stream: ${response.status} ${type}`);
        await change(s, b => {
            if (!b) {
                s.buffer = s.source.addSourceBuffer(type);
                s.buffer.mode = "segments";
                s.buffer.addEventListener("bufferedchange", e => note(`let go of ${ranges(e.removedRanges)}, now ${ranges(s.buffer.buffered)}`));
            } else if (s.type !== type)
                b.changeType(type);
            s.type = type;
            // Each song's own time starts at nothing: it's moved along to where the last one ended.
            s.buffer.timestampOffset = song.start;
            return false;
        });
        const reader = response.body.getReader();
        let parts = [], size = 0;
        for (; ;) {
            const { done, value } = await reader.read();
            if (value) {
                parts.push(value);
                size += value.length;
            }
            if (size >= CHUNK || done && size) {
                await append(s, song, join(parts, size));
                if (!song.added && song === s.now && s.from != null) {
                    audio.currentTime = song.start + s.from;
                    s.from = null;
                }
                song.added = true;
                parts = [];
                size = 0;
            }
            if (done)
                break;
        }
        song.end = s.end = endOf(s, song);
        note(`added ${path(song.url)}, to ${song.end.toFixed(2)}; there's ${ranges(s.buffer.buffered)}`);
        finish(s);
    } catch (e) {
        if (stream !== s || song.dropped)
            return;
        console.warn("Melodex: a song couldn't be added to the stream.", e);
        note(`couldn't add ${path(song.url)}: ${e?.name} ${e?.message}`);
        report("A song couldn't be added to the stream.");
        failed.add(path(song.url));
        if (song === s.now && !song.added) {
            // Played on its own.
            close();
            audio.src = source(song.url);
            play();
            return;
        }
        // What came of it is taken out again; the next is played on its own when it comes, and what's here of this one
        // plays to its end.
        await change(s, b => {
            // What was half read of it is forgotten too; there's none once the stream's said to have ended.
            if (s.source.readyState === "open")
                b.abort();
            b.remove(song.start, Infinity);
            return true;
        }).catch(() => { });
        if (song === s.next) {
            s.next = null;
            s.end = song.start;
        } else
            song.end = s.end = endOf(s, song);
        finish(s);
    } finally {
        if (stream === s)
            s.filling = false;
    }
}

function ranges(r) {
    const out = [];
    for (let i = 0; r && i < r.length; i++)
        out.push(`${r.start(i).toFixed(2)}–${r.end(i).toFixed(2)}`);
    return out.join(", ") || "nothing";
}

function join(parts, size) {
    if (parts.length === 1)
        return parts[0];
    const bytes = new Uint8Array(size);
    let at = 0;
    for (const part of parts) {
        bytes.set(part, at);
        at += part.length;
    }
    return bytes;
}

// Added to the stream, once there's room: a browser keeps only so much, and what's been played is let go of for more.
async function append(s, song, bytes) {
    for (; ;) {
        if (song.dropped || stream !== s)
            throw new DOMException("No longer wanted.", "AbortError");
        try {
            await change(s, b => {
                b.appendBuffer(bytes);
                return true;
            });
            return;
        } catch (e) {
            if (e?.name !== "QuotaExceededError")
                throw e;
            note("full");
            const behind = audio.currentTime - 10, buffered = s.buffer.buffered;
            if (buffered.length && buffered.start(0) < behind - 1)
                await change(s, b => {
                    b.remove(0, behind);
                    return true;
                });
            else
                await new Promise(resolve => setTimeout(resolve, 1000));
        }
    }
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
            b.removeEventListener("updateend", end);
            b.removeEventListener("error", error);
            ok ? resolve() : reject(new Error("The stream couldn't take it."));
        };
        const end = settle(true), error = settle(false);
        b.addEventListener("updateend", end);
        b.addEventListener("error", error);
    }));
    s.work = work.catch(() => { });
    return work;
}

// Where a song ends along the stream: the end of what's there of it.
function endOf(s, song) {
    const buffered = s.buffer.buffered;
    for (let i = buffered.length - 1; i >= 0; i--)
        if (buffered.end(i) > song.start)
            return buffered.end(i);
    return song.start + (song.length || 0);
}

// The stream's said to end with its last song, so the element ends there; another song added after starts it again.
function finish(s) {
    s.work.then(() => {
        const last = s.next ?? s.now;
        if (stream === s && last.end != null && s.source.readyState === "open" && !s.buffer?.updating)
            try {
                s.source.endOfStream();
            } catch {
                // Changed in the meantime: said again when it's done.
            }
    });
}

/** Whether the song's start is in the stream, to jump to. */
function ready(song) {
    if (song.start == null || !stream?.buffer)
        return false;
    try {
        const buffered = stream.buffer.buffered;
        for (let i = 0; i < buffered.length; i++)
            if (buffered.start(i) <= song.start + 0.1 && buffered.end(i) > song.start + 0.5)
                return true;
    } catch {
    }
    return false;
}

// The stream's run on into the next song: it's the one playing now, and what came before is let go of.
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
    if (to.start > 0)
        change(s, b => {
            b.remove(0, to.start);
            return true;
        }).catch(() => { });
}

/** Jumps to the next song, there in the stream already; quietly when Player.cs chose it. */
function skip(quiet) {
    audio.currentTime = stream.next.start;
    advance(quiet);
}

// A stream run dry. While the song's still being fetched, it'll come; otherwise what was there was let go of to save
// memory (an iPhone may), and the stream starts again from here.
// How much of the stream there is from here on.
function ahead(s) {
    try {
        const buffered = s.buffer.buffered, at = audio.currentTime;
        for (let i = 0; i < buffered.length; i++)
            if (buffered.start(i) <= at + 0.1 && buffered.end(i) > at)
                return buffered.end(i) - at;
    } catch {
    }
    return 0;
}

function stalled() {
    const s = stream;
    setTimeout(() => {
        if (stream !== s || s.filling || !s.now.added || audio.paused || audio.readyState > 2 || ahead(s) > 1)
            return;
        note(`ran dry at ${audio.currentTime.toFixed(2)}; there's ${ranges(s.buffer?.buffered)}`);
        if (s.rebuilt)
            return giveUp(s, "The stream ran dry on this device; songs are played on their own instead.");
        const { now, next } = s, from = here();
        begin(now.url, now.meta, now.length);
        stream.now.token = now.token;
        stream.rebuilt = true;
        if (next)
            follow(next.url, next.meta, next.token, next.length);
        seek(from);
        play();
    }, 1500);
}

export function load(url, meta, length) {
    epoch++;
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
    if (stream && !stream.now.added)
        stream.from = seconds;
    else
        audio.currentTime = (stream ? stream.now.start : 0) + seconds;
}

export function stop() {
    epoch++;
    unprepare();
    close();
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    setMetadata(null);
    controls(false);
}

// ---- Volume, kept per browser ----

function storedVolume() {
    try {
        const v = Number(localStorage.getItem("melodex.volume"));
        return localStorage.getItem("melodex.volume") !== null && v >= 0 && v <= 1 ? v : 1;
    } catch {
        return 1;
    }
}

export function getVolume() {
    return audio ? audio.volume : storedVolume();
}

export function setVolume(value) {
    const v = Math.min(1, Math.max(0, value));
    if (audio) {
        audio.volume = v;
        spare.volume = v;
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
