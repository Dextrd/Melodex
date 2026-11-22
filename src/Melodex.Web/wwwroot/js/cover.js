// A picture that didn't load: the server answers 503 while it's still fetching one, and 404 when there's none. An
// <img> can't tell which, so the covers still waiting are asked after together, in one request every couple of
// seconds, which leaves the browser's few connections to the server free for the page itself.
const waiting = new Set();
const every = 2000;
const maxChecks = 150;
let timer = null;

window.melodexCoverFailed = function (img) {
    // Not shown broken while it's asked after (a mosaic's parts show; a cover's picture waits hidden anyway).
    img.dataset.failed = '';
    waiting.add(img);
    timer ??= setTimeout(check, every);
};

const path = img => new URL(img.src).pathname;

// A cover's own placeholder shows, or a mosaic part's.
function blank(img) {
    const holder = img.parentElement;
    if (holder && !holder.classList.replace('pending', 'blank'))
        holder.classList.add('blank');
}

async function check() {
    const covers = [...waiting].filter(img => img.isConnected);
    waiting.clear();
    let states = {};
    if (covers.length > 0) {
        try {
            const response = await fetch('api/v1/artwork/status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify([...new Set(covers.map(path))]),
            });
            if (response.ok)
                states = await response.json();
        } catch {
        }
    }
    for (const img of covers) {
        if (!img.isConnected)
            continue;
        const checks = Number(img.dataset.checks || 0) + 1;
        img.dataset.checks = checks;
        const state = states[path(img)];
        if (state === 'none' || (state !== 'ready' && checks >= maxChecks)) {
            blank(img);
        } else if (state === 'ready') {
            const url = new URL(img.src);
            url.searchParams.set('try', checks);
            img.src = url.toString();
        } else {
            waiting.add(img);
        }
    }
    timer = waiting.size > 0 ? setTimeout(check, every) : null;
}

// A playlist's four covers show together once they're all in (or given up on), not one by one as each arrives.
window.melodexMosaicPart = function (img) {
    if (img.naturalWidth > 0)
        delete img.dataset.failed;
    const mosaic = img.closest('.playlist-mosaic');
    if (!mosaic || [...mosaic.querySelectorAll('img')].some(part => !part.complete))
        return;
    mosaic.classList.replace('pending', 'loaded');
};
