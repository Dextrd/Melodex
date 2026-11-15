// A song, album or artist's menu (ItemMenu.razor) opens from its row or tile too, where it was pointed at: by
// right-clicking it with a mouse, or pressing and holding it on a touch screen. Links and fields in a row keep their own,
// except in a row marked .menu-row, which is a link as a whole. Playlists' and folders' menus (PlaylistMenu.razor) too.
// Every menu opens upward when there's no room below it.
(() => {
    // A tile is a link as a whole, so it opens from anywhere on it; in a row, links keep their own menu.
    const menuOf = target => {
        if (target.closest('input, textarea, select, .menu, .menu-backdrop'))
            return null;
        // A row that's a link as a whole, such as a playlist's, opens it from its link too.
        const tile = target.closest('.tile-wrap') ?? target.closest('.menu-row');
        if (!tile && target.closest('a'))
            return null;
        return (tile ?? target.closest('tr, li'))?.querySelector('.song-menu > .icon-button') ?? null;
    };

    // Where a right-click or a press was, for the menu it opens to appear there rather than by its button.
    let at = null;

    // With a menu open, its backdrop covers the page: what's under it, where it was pointed at, is what a right-click
    // or a press there is for.
    const under = (target, x, y) => target.closest('.menu-backdrop')
        ? document.elementsFromPoint(x, y).find(el => !el.closest('.menu-backdrop, .menu')) ?? target
        : target;

    // Closes the menu that's open, if any, and opens the button's afresh where it was pointed at.
    const open = (button, x, y) => {
        document.querySelector('.menu-backdrop')?.click();
        at = { x, y, time: Date.now() };
        // After the open menu has closed, so its button opens it again rather than closing it.
        setTimeout(() => button.click());
    };

    document.addEventListener('contextmenu', e => {
        const button = menuOf(under(e.target, e.clientX, e.clientY));
        // A touch screen's press is handled below, so it doesn't open and close again.
        if (e.pointerType === 'touch') {
            if (button)
                e.preventDefault();
            return;
        }
        // A right-click on the backdrop closes the open menu, and opens the one for what's under it, if anything has
        // one (the browser's own menu otherwise).
        if (!button) {
            if (e.target.closest('.menu-backdrop'))
                e.target.closest('.menu-backdrop').click();
            return;
        }
        e.preventDefault();
        open(button, e.clientX, e.clientY);
    });

    let timer = null, start = null, opened = false;
    document.addEventListener('touchstart', e => {
        if (e.touches.length !== 1)
            return;
        const { clientX: x, clientY: y } = e.touches[0];
        // Pressing elsewhere with a menu open opens that one instead, as a right-click does.
        const button = menuOf(under(e.target, x, y));
        if (!button)
            return;
        start = { x, y };
        timer = setTimeout(() => {
            timer = null;
            opened = true;
            navigator.vibrate?.(10);
            open(button, x, y);
        }, 500);
    }, { passive: true });
    const cancel = () => { clearTimeout(timer); timer = null; };
    document.addEventListener('touchmove', e => {
        if (timer && Math.hypot(e.touches[0].clientX - start.x, e.touches[0].clientY - start.y) > 10)
            cancel();
    }, { passive: true });
    document.addEventListener('touchend', e => {
        cancel();
        // The press opened the menu: the tap it ends with isn't also a click on the row.
        if (opened) {
            opened = false;
            e.preventDefault();
        }
    });
    document.addEventListener('touchcancel', cancel);

    // Any menu that opens where there's no room for it below, above the player bar, opens upward instead, if there's
    // more room there; one that would run off the side is moved back on. One opened by a right-click or a press opens where
    // it was.
    const place = menu => {
        if (at && Date.now() - at.time < 1000) {
            const { x, y } = at;
            at = null;
            const player = document.querySelector('.player');
            const floor = player && player.offsetParent ? Math.min(innerHeight, player.getBoundingClientRect().top) : innerHeight;
            // Its button keeps its own look: it wasn't the button that was clicked.
            menu.classList.add('at-pointer');
            Object.assign(menu.style, { position: 'fixed', right: 'auto', bottom: 'auto', translate: 'none' });
            const { width, height } = menu.getBoundingClientRect();
            menu.style.left = `${Math.max(8, Math.min(x, innerWidth - 8 - width))}px`;
            // Below the pointer, or above it; failing both, as near it as the window has room for.
            menu.style.top = `${y + height <= floor - 8 ? y : y - height >= 8 ? y - height : Math.max(8, floor - 8 - height)}px`;
            return;
        }
        const rect = menu.getBoundingClientRect();
        // And sideways, back onto the screen if it would run off an edge.
        const over = Math.min(0, innerWidth - 8 - rect.right) || Math.max(0, 8 - rect.left);
        if (over)
            menu.style.translate = `${over}px 0`;
        const player = document.querySelector('.player');
        const floor = player && player.offsetParent ? Math.min(innerHeight, player.getBoundingClientRect().top) : innerHeight;
        if (rect.bottom > floor - 8) {
            const opener = menu.parentElement.getBoundingClientRect();
            if (opener.top - rect.height > 8 || opener.top > floor - opener.bottom)
                menu.classList.add('up');
        }
        unclip(menu);
    };

    // A menu in a box that scrolls, such as the player's queue, would be cut off at its edges: it's drawn over the page
    // instead, where it is.
    const unclip = menu => {
        const rect = menu.getBoundingClientRect();
        for (let box = menu.parentElement; box && box !== document.body; box = box.parentElement) {
            if (getComputedStyle(box).overflowY === 'visible' && getComputedStyle(box).overflowX === 'visible')
                continue;
            const edge = box.getBoundingClientRect();
            if (rect.top < edge.top || rect.bottom > edge.bottom || rect.left < edge.left || rect.right > edge.right) {
                Object.assign(menu.style, { position: 'fixed', left: `${rect.left}px`, top: `${rect.top}px`, right: 'auto', bottom: 'auto', translate: 'none' });
                return;
            }
            if (box.classList.contains('page-scroll'))
                return;
        }
    };
    new MutationObserver(changes => {
        for (const change of changes)
            for (const node of change.addedNodes)
                if (node.nodeType === 1)
                    (node.matches('.menu') ? [node] : node.querySelectorAll('.menu')).forEach(place);
    }).observe(document.body, { childList: true, subtree: true });
})();
