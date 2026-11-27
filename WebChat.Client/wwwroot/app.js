// ===================================
// Theme Management
// ===================================

window.themeManager = {
    // Get the current theme
    getTheme: function () {
        return document.documentElement.getAttribute('data-theme') || 'light';
    },

    // Set theme and persist to localStorage
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem('theme', theme);
        return theme;
    },

    // Toggle between light and dark
    toggleTheme: function () {
        const current = this.getTheme();
        const next = current === 'dark' ? 'light' : 'dark';
        return this.setTheme(next);
    },

    // Initialize theme from localStorage or system preference
    init: function () {
        const stored = localStorage.getItem('theme');
        if (stored) {
            document.documentElement.setAttribute('data-theme', stored);
            return stored;
        }

        // Check system preference
        if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            document.documentElement.setAttribute('data-theme', 'dark');
            return 'dark';
        }

        return 'light';
    }
};

// Initialize theme immediately to prevent flash
themeManager.init();

// Listen for system theme changes
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) {
        // Only auto-switch if user hasn't set a preference
        if (!localStorage.getItem('theme')) {
            themeManager.setTheme(e.matches ? 'dark' : 'light');
        }
    });
}

// ===================================
// Page Visibility (for mobile background/foreground)
// ===================================

window.visibilityHelper = {
    _dotnetRef: null,

    register: function (dotnetRef) {
        this._dotnetRef = dotnetRef;
        // visibilitychange covers ordinary foreground/background. pageshow also fires on
        // bfcache restore, which is how Android PWAs often resume (without a clean
        // visibilitychange). online fires when connectivity returns. All route to the same
        // reconnect check; the .NET side dedupes concurrent calls.
        document.addEventListener('visibilitychange', this._onResume);
        window.addEventListener('pageshow', this._onResume);
        window.addEventListener('online', this._onResume);
    },

    _onResume: function () {
        const ref = window.visibilityHelper._dotnetRef;
        if (ref && document.visibilityState === 'visible') {
            ref.invokeMethodAsync('OnPageVisible');
        }
    },

    dispose: function () {
        document.removeEventListener('visibilitychange', this._onResume);
        window.removeEventListener('pageshow', this._onResume);
        window.removeEventListener('online', this._onResume);
        this._dotnetRef = null;
    }
};

// ===================================
// Chat Input Handling
// ===================================

// Prevent Enter from adding newlines in chat input (Shift+Enter still works)
document.addEventListener('keydown', function (e) {
    if (e.target.classList.contains('chat-input') && e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
    }
});

// Auto-resize textarea utilities
window.chatInput = {
    // Auto-resize textarea to fit content
    autoResize: function (element) {
        if (!element) return;

        // Reset height to auto to get the correct scrollHeight
        element.style.height = 'auto';

        // Set the height to match content (respects max-height from CSS)
        element.style.height = element.scrollHeight + 'px';
    },

    // Reset textarea to minimum height
    reset: function (element) {
        if (!element) return;
        element.style.height = 'auto';
    },

    // Pasting a screenshot and dropping files both end up in the composer's own file input, so
    // there is one path into the picker rather than three. The input's change event is what the
    // component is already listening to.
    bindFileDrops: function (composer) {
        if (!composer) return;
        const input = composer.querySelector('input[type=file]');
        if (!input) return;

        const hand = function (files) {
            if (!files || files.length === 0) return false;
            const transfer = new DataTransfer();
            for (const file of files) transfer.items.add(file);
            input.files = transfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
        };

        composer.addEventListener('dragover', function (e) { e.preventDefault(); });
        composer.addEventListener('drop', function (e) {
            if (hand(e.dataTransfer && e.dataTransfer.files)) e.preventDefault();
        });

        // Paste is bound on the document: a screenshot is pasted wherever the caret happens to
        // be, and the composer's textarea is not always focused when it lands. One document
        // listener at a time — each rebind replaces the last, whose composer is gone, so a stack
        // of them would hand every paste to detached inputs too.
        if (this._onPaste) document.removeEventListener('paste', this._onPaste);
        this._onPaste = function (e) {
            if (!e.clipboardData) return;
            const files = Array.from(e.clipboardData.files || []);
            if (files.length > 0 && hand(files)) e.preventDefault();
        };
        document.addEventListener('paste', this._onPaste);
    }
};

// ===================================
// Dismissing a dropdown
// ===================================

// A dropdown closes when a press lands outside it, and that press must still reach whatever it
// was aimed at. A full-screen backdrop cannot do this: it is the topmost thing under the finger,
// so it eats the press. On a phone the menus sit over the conversation list, which made every
// selection after using one cost two taps — one to dismiss, one to land.
//
// Listening on the document instead, in the capture phase, and never calling preventDefault or
// stopPropagation: the press closes the menu on its way to the row it was meant for.
window.outsidePress = {
    _watchers: new Map(),

    watch: function (root, dotnetRef) {
        if (!root) return;
        this.unwatch(root);
        const onDown = e => {
            if (root.contains(e.target)) return;
            window.outsidePress.unwatch(root);
            dotnetRef.invokeMethodAsync('CloseFromOutsidePress');
        };
        document.addEventListener('pointerdown', onDown, { capture: true });
        this._watchers.set(root, onDown);
    },

    unwatch: function (root) {
        const onDown = root && this._watchers.get(root);
        if (!onDown) return;
        document.removeEventListener('pointerdown', onDown, true);
        this._watchers.delete(root);
    }
};

// ===================================
// Attachments
// ===================================

// The picture viewer, zoomed to a picture's own size: scrolled to its middle, so it opens where it was.
window.imageViewer = {
    center: function (stage) {
        if (!stage) {
            return;
        }

        stage.scrollLeft = (stage.scrollWidth - stage.clientWidth) / 2;
        stage.scrollTop = (stage.scrollHeight - stage.clientHeight) / 2;
    }
};

window.attachments = {
    open: function (url, fileName) {
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName || '';
        link.target = '_blank';
        link.rel = 'noopener';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }
};

// Auto-resize on input event
document.addEventListener('input', function (e) {
    if (e.target.classList.contains('chat-input')) {
        window.chatInput.autoResize(e.target);
    }
});

// ===================================
// Chat Scroll Utilities
// ===================================

// The conversation's scroll: it follows the end while the reader is there, and the turn rail
// (TurnRailView) and the "Scroll to end" pill read it. What changes every frame — which turns are on
// screen, which one is being read, whether the pill shows — is decided here and written onto their
// elements, never sent to .NET per scroll tick.
window.chatScroll = {
    _stickyState: true,  // Follow the end: the reader is at it, or asked to go there
    _element: null,
    _scrollHandler: null,
    _interruptHandler: null,
    _contentResize: null,
    _contentMutation: null,
    _rail: null,
    _railRef: null,
    _railResize: null,
    _railWheel: null,
    _frame: 0,
    _current: null,
    _pillTimer: 0,
    _glide: null,  // A smooth scroll the page started: { target, sticky, timer }

    // Where a jump leaves its turn's first row: below the edge, so the question reads as a heading.
    _turnOffset: 24,
    _followZone: 50,  // pixels from the end still counted as at it
    _pillDelay: 150,  // showing waits this long, so a topic switch's first frames never flash it

    isAtBottom: function (element, threshold) {
        if (!element) return true;
        return element.scrollHeight - element.scrollTop - element.clientHeight <= (threshold ?? this._followZone);
    },

    scrollToBottom: function (element, smooth) {
        if (!element) return;
        this._endGlide();
        this._stickyState = true;
        requestAnimationFrame(() => {
            if (smooth) {
                element.scrollTo({ top: element.scrollHeight, behavior: 'smooth' });
            } else {
                element.scrollTop = element.scrollHeight;
            }
        });
    },

    initStickyScroll: function (element) {
        if (!element) return;
        this._detachContent();

        this._element = element;
        this._stickyState = true;

        const self = this;
        this._scrollHandler = () => {
            const glide = self._glide;
            if (glide && self._arrived(element, glide)) {
                self._endGlide();
            }
            // A smooth scroll passes through the follow zone on its way somewhere else, or has not
            // reached the end it is going to: neither says where the reader wants to be.
            self._stickyState = self._glide ? self._glide.sticky : self.isAtBottom(element);
            self.refresh();
        };
        element.addEventListener('scroll', this._scrollHandler, { passive: true });

        // The reader taking the wheel ends a smooth scroll the page started.
        this._interruptHandler = () => self._endGlide();
        for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown']) {
            element.addEventListener(type, this._interruptHandler, { passive: true });
        }

        // A row grows with no scroll and no render of the list: an image loading, a group opening, a
        // reply streaming into its bubble. Each row is watched, and rows come and go with the list.
        this._contentResize = new ResizeObserver(() => self.refresh());
        this._contentResize.observe(element);
        for (const child of element.children) this._contentResize.observe(child);
        this._contentMutation = new MutationObserver(records => {
            for (const record of records) {
                for (const node of record.addedNodes) {
                    if (node.nodeType === Node.ELEMENT_NODE) self._contentResize.observe(node);
                }
                for (const node of record.removedNodes) {
                    if (node.nodeType === Node.ELEMENT_NODE) self._contentResize.unobserve(node);
                }
            }
            self.refresh();
        });
        this._contentMutation.observe(element, { childList: true });

        this.refresh();
    },

    // Asked again when the frame comes: a jump made in between (TurnRailView) has stopped following, and
    // a snap queued before it would cancel the jump.
    scrollToBottomIfSticky: function (element) {
        if (!element || !this._stickyState) return;
        requestAnimationFrame(() => {
            if (this._stickyState) element.scrollTop = element.scrollHeight;
        });
    },

    // The pill: to the end, following it from there on.
    scrollToEnd: function () {
        const area = this._element;
        if (!area) return;
        this._showPill(false);
        this._glideTo(area, area.scrollHeight - area.clientHeight, true);
    },

    // A strip on the rail: to that turn's first row, and no longer following the end — a reply
    // streaming in would otherwise pull the reader straight back.
    scrollToTurn: function (index) {
        const area = this._element;
        const row = area?.querySelector(`[data-turn="${index}"]`);
        if (!row) return;
        const top = row.getBoundingClientRect().top - area.getBoundingClientRect().top + area.scrollTop;
        this._glideTo(area, top - this._turnOffset, false);
    },

    // The rail's step buttons: the turn before or after the one being read.
    stepTurn: function (by) {
        if (this._current === null) return;
        this.scrollToTurn(this._current + by);
    },

    currentTurn: function () {
        return this._current;
    },

    attachRail: function (root, dotNetRef) {
        this.detachRail();
        if (!root) return;
        this._rail = root;
        this._railRef = dotNetRef;

        const track = root.querySelector('.turn-rail-track');
        this._railResize = new ResizeObserver(entries => {
            const height = entries[0]?.contentRect.height ?? 0;
            dotNetRef.invokeMethodAsync('RailResized', height);
        });
        if (track) this._railResize.observe(track);

        // The rail stands beside the conversation, not in it, so a wheel turned over it would scroll
        // nothing: it is handed on.
        const self = this;
        this._railWheel = e => {
            const area = self._element;
            if (!area || e.ctrlKey || e.deltaY === 0) return;
            e.preventDefault();
            self._endGlide();
            const unit = e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16
                : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? area.clientHeight : 1;
            area.scrollBy({ top: e.deltaY * unit });
        };
        root.addEventListener('wheel', this._railWheel, { passive: false });
        this.refresh();
    },

    detachRail: function () {
        this._railResize?.disconnect();
        if (this._rail && this._railWheel) {
            this._rail.removeEventListener('wheel', this._railWheel);
        }
        this._rail = null;
        this._railRef = null;
        this._railResize = null;
        this._railWheel = null;
    },

    // At most once a frame, however many scrolls, resizes and renders asked for it.
    refresh: function () {
        if (this._frame) return;
        this._frame = requestAnimationFrame(() => {
            this._frame = 0;
            this._update();
        });
    },

    _update: function () {
        const area = this._element;
        if (!area) return;

        const areaTop = area.getBoundingClientRect().top;
        const turns = [...area.querySelectorAll('[data-turn]')]
            .map(row => {
                const rect = row.getBoundingClientRect();
                const top = rect.top - areaTop + area.scrollTop;
                return { index: Number(row.dataset.turn), top, bottom: top + rect.height };
            })
            .sort((a, b) => a.index - b.index);

        const viewTop = area.scrollTop;
        const viewBottom = viewTop + area.clientHeight;
        const end = area.scrollHeight;

        // A turn runs from its first row to the next turn's, so a long reply keeps its strip lit.
        const inView = turns.map((turn, i) =>
            turn.top < viewBottom && (turns[i + 1]?.top ?? end) > viewTop);

        // The turn being read: the first whose first row shows (below where a jump leaves one, so
        // stepping on never lands on the same turn again), else the one whose reply fills the screen.
        const readFrom = viewTop + this._turnOffset + 1;
        let current = null;
        for (let i = 0; i < turns.length; i++) {
            if (turns[i].bottom > readFrom) {
                current = turns[i].top < viewBottom ? i : (i > 0 ? i - 1 : null);
                break;
            }
            current = i;
        }
        this._current = current;

        // The exact end, not the follow zone: a step can still move inside the zone.
        const atEnd = end - viewBottom <= 1;
        const rail = this._rail;
        if (rail) {
            for (const mark of rail.querySelectorAll('.turn-rail-mark')) {
                const first = Number(mark.dataset.first);
                const last = Number(mark.dataset.last);
                let lit = false;
                for (let i = first; i <= last && !lit; i++) lit = inView[i] === true;
                const next = lit ? 'true' : 'false';
                if (mark.dataset.inView !== next) mark.dataset.inView = next;
            }
            const previous = rail.querySelector('.turn-rail-nav.previous');
            const following = rail.querySelector('.turn-rail-nav.next');
            if (previous) previous.disabled = current === null || current <= 0;
            if (following) following.disabled = current === null || atEnd || current + 1 >= turns.length;
        }

        const away = !this.isAtBottom(area) && !(this._glide?.sticky);
        if (!away) {
            this._showPill(false);
        } else if (!this._pillTimer && !this._pill()?.classList.contains('visible')) {
            this._pillTimer = setTimeout(() => {
                this._pillTimer = 0;
                const now = this._element;
                if (now && !this.isAtBottom(now) && !(this._glide?.sticky)) this._showPill(true);
            }, this._pillDelay);
        }
    },

    _pill: function () {
        return this._element?.parentElement?.querySelector('.scroll-to-end') ?? null;
    },

    _showPill: function (show) {
        if (!show && this._pillTimer) {
            clearTimeout(this._pillTimer);
            this._pillTimer = 0;
        }
        this._pill()?.classList.toggle('visible', show);
    },

    _glideTo: function (area, top, sticky) {
        this._endGlide();
        const target = Math.max(0, Math.min(top, area.scrollHeight - area.clientHeight));
        this._stickyState = sticky;
        const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (reduced || Math.abs(area.scrollTop - target) <= 1) {
            area.scrollTop = target;
            return;
        }
        // Ended by arriving, by the reader scrolling, or — should the browser stop short — in time.
        const glide = { target, sticky, timer: 0 };
        glide.timer = setTimeout(() => {
            if (this._glide === glide) this._endGlide();
        }, 1500);
        this._glide = glide;
        area.scrollTo({ top: target, behavior: 'smooth' });
    },

    _arrived: function (area, glide) {
        return glide.sticky
            ? this.isAtBottom(area, 1)
            : Math.abs(area.scrollTop - glide.target) <= 2;
    },

    _endGlide: function () {
        if (!this._glide) return;
        clearTimeout(this._glide.timer);
        this._glide = null;
    },

    _detachContent: function () {
        const element = this._element;
        if (element && this._scrollHandler) {
            element.removeEventListener('scroll', this._scrollHandler);
        }
        if (element && this._interruptHandler) {
            for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown']) {
                element.removeEventListener(type, this._interruptHandler);
            }
        }
        this._contentResize?.disconnect();
        this._contentMutation?.disconnect();
        this._contentResize = null;
        this._contentMutation = null;
        this._scrollHandler = null;
        this._interruptHandler = null;
    },

    dispose: function () {
        this._detachContent();
        this.detachRail();
        this._endGlide();
        if (this._frame) cancelAnimationFrame(this._frame);
        if (this._pillTimer) clearTimeout(this._pillTimer);
        this._frame = 0;
        this._pillTimer = 0;
        this._element = null;
        this._current = null;
        this._stickyState = true;
    }
};

// ===================================
// Favicon
// ===================================

window.faviconHelper = {
    _baseTitle: document.title,

    setColor: function (color) {
        if (!/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.test(color)) return;
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 50" width="120" height="50"><text x="60" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="40" fill="${color}">ᓚᘏᗢ</text></svg>`;
        const link = document.querySelector('link[rel="icon"]');
        if (link) {
            link.href = 'data:image/svg+xml,' + encodeURIComponent(svg);
        }
    },

    setSpaceTitle: function (spaceName) {
        document.title = spaceName
            ? this._baseTitle + ' \u2014 ' + spaceName
            : this._baseTitle;
    }
};

// ===================================
// Per-space manifest
// ===================================

// The page's host serves the manifest from what it is asked with, since it knows no household: the
// page names its space's slug, name and colour once the hub has told it them, and remembers them for
// the next launch's first manifest (index.html's spaceManifest).
window.manifestHelper = {
    setSpace: function (slug, name, color) {
        const link = document.querySelector('link[rel="manifest"]');
        if (!link || !window.spaceManifest) return;
        window.spaceManifest.remember(slug, name, color);
        const href = window.spaceManifest.href(slug, name, color);
        if (link.getAttribute('href') !== href) {
            link.setAttribute('href', href);
        }
    }
};

// ===================================
// Per-space accent (CSS custom property)
// ===================================

window.accentHelper = {
    setVar: function (color) {
        if (!/^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.test(color)) return;
        document.documentElement.style.setProperty('--space-accent', color);
    }
};

// ===================================
// Native <dialog> helpers
// ===================================

window.hearthSheet = window.hearthSheet || {};
window.hearthSheet.showDialog = function (el) { if (el && !el.open) el.showModal(); };
window.hearthSheet.closeDialog = function (el) { if (el && el.open) el.close(); };

// ===================================
// Hearth sheet drag gesture
// ===================================

Object.assign(window.hearthSheet, {
    _el: null, _ref: null, _rows: null,
    _startY: 0, _startX: 0, _lastY: 0, _lastT: 0, _vy: 0, _dragging: false, _axisLocked: null, _startOffset: 0,
    _rowsStartY: 0, _rowsAtTop: false, _rowsAtBottom: false, _rowsMode: null,

    register: function (peekBar, dotnetRef) {
        const sheet = peekBar.closest('.hearth');
        if (!sheet) return;
        this._el = sheet;
        this._ref = dotnetRef;
        this._rows = sheet.querySelector('.hearth-rows');
        // Drag the sheet from anywhere in the chrome (handle, search, agent strip, padding).
        sheet.addEventListener('pointerdown', this._onDown);
        // Chromium decides what a gesture was from the touch stream, and the pointer handlers
        // above never touch it. Leaving a fast drag's touchmoves unconsumed puts the input
        // pipeline in a state where the next tap arrives as pointerdown/touchstart/pointerup/
        // touchend and no click at all, for about a second — which is why flicking the drawer
        // open and then picking a conversation cost two taps. preventDefault on pointermove
        // does not reach that decision; only the touch event does.
        sheet.addEventListener('touchmove', this._onSheetTouchMove, { passive: false });
        // The conversation list scrolls natively; a pull-down at its very top collapses the
        // sheet. Touch handlers (not pointer) so normal list scrolling keeps its momentum.
        if (this._rows) {
            this._rows.addEventListener('touchstart', this._onRowsTouchStart, { passive: true });
            this._rows.addEventListener('touchmove', this._onRowsTouchMove, { passive: false });
            this._rows.addEventListener('touchend', this._onRowsTouchEnd);
            this._rows.addEventListener('touchcancel', this._onRowsTouchEnd);
            // Reaching the bottom of the list is what asks for the next page. The margin is a
            // screenful, so the rows arrive before the person runs out of list to read.
            this._rows.addEventListener('scroll', this._onRowsScroll, { passive: true });
        }
    },

    _onRowsScroll: function () {
        const h = window.hearthSheet;
        const rows = h._rows;
        if (!rows || !h._ref) return;
        if (rows.scrollTop + rows.clientHeight < rows.scrollHeight - rows.clientHeight) return;
        h._ref.invokeMethodAsync('ReachedEndOfList');
    },

    // A list too short to scroll can never reach its own bottom, so a tall screen would sit on
    // one page forever with no way to ask for the next. Asked after each render; the paging
    // effect is what decides whether anything is left to fetch.
    checkRows: function () {
        const h = window.hearthSheet;
        if (!h._rows || !h._ref) return;
        if (h._rows.scrollHeight > h._rows.clientHeight) return;
        h._ref.invokeMethodAsync('ReachedEndOfList');
    },

    // The drag gesture only applies to the mobile bottom-sheet layout, not the desktop rail.
    _isSheet: function () { return window.matchMedia('(max-width: 767px)').matches; },

    focus: function (el) { if (el) requestAnimationFrame(() => el.focus()); },

    _onDown: function (e) {
        const h = window.hearthSheet;
        if (!h._isSheet()) return;                  // desktop rail doesn't drag
        // The grabber handle is the primary drag affordance — let drags start on it.
        const onHandle = !!e.target.closest('.hearth-handle');
        // Otherwise don't start a drag from a control where the press means something else.
        if (!onHandle && e.target.closest('button, dialog, input, textarea, select')) return;
        // The conversation list owns its own pull-to-collapse via touch handlers (so it keeps
        // native scroll momentum); the pointer drag covers only the sheet chrome.
        if (h._rows && h._rows.contains(e.target)) return;
        h._startY = h._lastY = e.clientY; h._startX = e.clientX; h._lastT = e.timeStamp;
        // Capture the sheet's current translateY so the drag continues from where it rests
        // (0 = full … restPeek = peek) instead of snapping to the peek position.
        const rect = h._el.getBoundingClientRect();
        h._startOffset = rect.top - (window.innerHeight - rect.height);
        h._vy = 0; h._axisLocked = null; h._dragging = true;
        document.addEventListener('pointermove', h._onMove, { passive: false });
        document.addEventListener('pointerup', h._onUp);
    },

    _onMove: function (e) {
        const h = window.hearthSheet;
        if (!h._dragging) return;
        const dy = e.clientY - h._startY;
        const dx = e.clientX - h._startX;
        if (h._axisLocked === null) {
            const THRESH = 8;                       // px before we commit to an axis (tunable, spec §10)
            if (Math.abs(dy) < THRESH && Math.abs(dx) < THRESH) return;
            h._axisLocked = Math.abs(dy) >= Math.abs(dx) ? 'y' : 'x';
            if (h._axisLocked === 'y') h._el.classList.add('dragging');
        }
        if (h._axisLocked !== 'y') return;          // horizontal/ambiguous → ignore (no swipe-delete in v1)
        // Written synchronously, like the rows handler: deferring this to requestAnimationFrame
        // let the final move's write land after the release had already settled, leaving a stale
        // inline offset that overrode every later detent change — the sheet sat where the drag
        // left it and a topic tap selected invisibly instead of closing the drawer.
        const base = h._el.getBoundingClientRect().height; // ~92dvh
        const restPeek = base - 64;
        const offset = Math.min(restPeek, Math.max(0, h._startOffset + dy));
        h._el.style.setProperty('--sheet-offset', offset + 'px');
        h._vy = (e.clientY - h._lastY) / Math.max(1, e.timeStamp - h._lastT);
        h._lastY = e.clientY; h._lastT = e.timeStamp;
        e.preventDefault();
    },

    // Gated on _dragging rather than on the axis lock: the lock only engages 8px in, and the
    // moves before it are part of the same gesture. _onDown leaves _dragging false for touches
    // inside the conversation list, so its native scrolling is untouched.
    _onSheetTouchMove: function (e) {
        if (window.hearthSheet._dragging) e.preventDefault();
    },

    _onUp: function () {
        const h = window.hearthSheet;
        h._dragging = false;
        h._el.classList.remove('dragging');
        document.removeEventListener('pointermove', h._onMove);
        document.removeEventListener('pointerup', h._onUp);
        const wasDrag = h._axisLocked === 'y';
        const traveled = Math.abs(h._lastY - h._startY);
        h._settle();
        // After a real drag, swallow the trailing click so the handle's @onclick
        // (CycleDetent) doesn't fire on top of the committed detent. Below the tap slop
        // the gesture was a tap whose finger drifted, and its click is the intent.
        const TAP_SLOP = 24;                        // px, tunable (spec §10)
        if (wasDrag && traveled >= TAP_SLOP) h._swallowTrailingClick();
    },

    // A drag's own trailing click (a mouse release always sends one) arrives with no new
    // pointerdown in front of it. A genuine follow-up tap starts with one, so the first
    // pointerdown disarms the swallow instead of letting it eat the tap that switches
    // conversations right after the sheet was dragged open.
    _swallowTrailingClick: function () {
        function cleanup() {
            document.removeEventListener('click', swallow, true);
            document.removeEventListener('pointerdown', cleanup, true);
        }
        function swallow(ev) { ev.stopPropagation(); ev.preventDefault(); cleanup(); }
        document.addEventListener('click', swallow, { capture: true });
        document.addEventListener('pointerdown', cleanup, { capture: true });
        setTimeout(cleanup, 350);
    },

    _settle: function () {
        const h = window.hearthSheet;
        if (h._axisLocked !== 'y') { h._el.style.removeProperty('--sheet-offset'); return; }
        const base = h._el.getBoundingClientRect().height;
        const current = parseFloat(getComputedStyle(h._el).getPropertyValue('--sheet-offset')) || (base - 64);
        const ratio = current / base;               // 0 = full, ~1 = peek
        const FLICK = 0.6;                          // px/ms threshold (tunable, spec §10)
        let detent;
        if (h._vy < -FLICK) detent = 'Full';
        else if (h._vy > FLICK) detent = 'Peek';
        else detent = ratio > 0.66 ? 'Peek' : ratio > 0.28 ? 'Half' : 'Full';
        h._el.style.removeProperty('--sheet-offset');   // let the .detent-* class drive the resting transform
        if (h._ref) h._ref.invokeMethodAsync('CommitDetent', detent);
    },

    // ---- Conversation list: native scroll, plus pull-to-collapse at the top ----

    _onRowsTouchStart: function (e) {
        const h = window.hearthSheet;
        h._rowsMode = null;
        if (!h._isSheet() || e.touches.length !== 1) return;
        h._rowsStartY = h._lastY = e.touches[0].clientY;
        h._rowsAtTop = h._rows.scrollTop <= 0;
        h._rowsAtBottom = h._rows.scrollTop + h._rows.clientHeight >= h._rows.scrollHeight - 1;
        h._lastT = e.timeStamp; h._vy = 0;
        const rect = h._el.getBoundingClientRect();
        h._startOffset = rect.top - (window.innerHeight - rect.height);
    },

    _onRowsTouchMove: function (e) {
        const h = window.hearthSheet;
        if (h._rowsMode === 'native' || !h._isSheet() || e.touches.length !== 1) return;
        const y = e.touches[0].clientY;
        const dy = y - h._rowsStartY;
        if (h._rowsMode === null) {
            // On a list too short to scroll both edge flags are true, so this decides for
            // every touch. Below the tap slop the movement is a tap whose finger drifted —
            // engaging the sheet would eat the tap's click on browsers that deliver
            // touchmove under their own click slop (tunable, spec §10).
            const TAP_SLOP = 24;
            if (Math.abs(dy) < TAP_SLOP) return;
            // Pull down at the top collapses; pull up at the bottom expands. Otherwise hand
            // the gesture back to the browser for normal (momentum) scrolling.
            if ((h._rowsAtTop && dy > 0) || (h._rowsAtBottom && dy < 0)) {
                h._rowsMode = 'sheet'; h._el.classList.add('dragging');
                h._rowsStartY = y;                  // track from here: no slop-sized jump
            }
            else { h._rowsMode = 'native'; return; }
        }
        e.preventDefault();
        const base = h._el.getBoundingClientRect().height;
        const restPeek = base - 64;
        const offset = Math.min(restPeek, Math.max(0, h._startOffset + (y - h._rowsStartY)));
        h._el.style.setProperty('--sheet-offset', offset + 'px');
        h._vy = (y - h._lastY) / Math.max(1, e.timeStamp - h._lastT);
        h._lastY = y; h._lastT = e.timeStamp;
    },

    _onRowsTouchEnd: function () {
        const h = window.hearthSheet;
        if (h._rowsMode === 'sheet') {
            h._el.classList.remove('dragging');
            h._axisLocked = 'y';
            h._settle();
            // Swallow the trailing click so a pull-to-collapse doesn't also select a topic.
            h._swallowTrailingClick();
        }
        h._rowsMode = null;
    },

    registerCommandKey: function (dotnetRef) {
        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k'
                && !(e.target.classList && e.target.classList.contains('chat-input'))) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OpenSearch');
            }
        });
    }
});

// ===================================
// Clipboard
// ===================================

window.clipboardHelper = {
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            // Fallback for older browsers / insecure contexts
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
        }
    }
};

// The field replaces the title in place, so the name it opens with is the one already on
// screen: selecting it means typing replaces the title and a tap places the caret instead.
// rAF because the input only exists after Blazor's render commits.
window.inlineEdit = {
    focusAndSelect: function (el) {
        if (!el) return;
        requestAnimationFrame(() => {
            el.focus();
            el.select();
        });
    }
};

// ===================================
// Element Utilities
// ===================================

window.getBoundingClientRect = function (element) {
    if (!element) return {top: 0, left: 0, width: 0, height: 0};
    const rect = element.getBoundingClientRect();
    return {
        top: rect.top,
        left: rect.left,
        width: rect.width,
        height: rect.height
    };
};

// ===================================
// Tooltip (lightweight)
// ===================================

(function () {
    let timeout;
    const tooltip = () => document.getElementById('tooltip');

    document.addEventListener('mouseenter', e => {
        const target = e.target.closest?.('[data-tooltip]');
        if (!target) return;

        const x = e.clientX, y = e.clientY;
        timeout = setTimeout(() => {
            const tip = tooltip();
            if (tip) {
                tip.textContent = target.dataset.tooltip;
                tip.style.left = (x) + 'px';
                tip.style.top = (y - 24) + 'px';
                tip.classList.add('visible');
            }
        }, 400);
    }, true);

    document.addEventListener('mouseleave', e => {
        if (e.target.closest?.('[data-tooltip]')) {
            clearTimeout(timeout);
            const tip = tooltip();
            if (tip) tip.classList.remove('visible');
        }
    }, true);
})();

// ===================================
// Alert tone
// ===================================

// A ringing timer sounds until it ends: two short beeps every second and a half, and a buzz on a
// phone that has one. Drawn with the Web Audio API so there is no sound file to ship. A page the
// person has not touched yet may not be allowed to play; the alert still shows, and the push that
// brought the person here is what reached them.
window.alertTone = {
    _context: null,
    _timer: null,

    start: function () {
        if (this._timer) return;
        try {
            this._context ??= new (window.AudioContext || window.webkitAudioContext)();
            this._context.resume?.();
        } catch {
            this._context = null;
        }
        const ring = () => {
            this._beep(0);
            this._beep(0.25);
            navigator.vibrate?.([200, 100, 200]);
        };
        ring();
        this._timer = setInterval(ring, 1500);
    },

    stop: function () {
        if (this._timer) clearInterval(this._timer);
        this._timer = null;
        navigator.vibrate?.(0);
    },

    _beep: function (at) {
        const ctx = this._context;
        if (!ctx) return;
        const start = ctx.currentTime + at;
        const oscillator = ctx.createOscillator();
        const gain = ctx.createGain();
        oscillator.frequency.value = 880;
        gain.gain.setValueAtTime(0.0001, start);
        gain.gain.exponentialRampToValueAtTime(0.3, start + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.18);
        oscillator.connect(gain).connect(ctx.destination);
        oscillator.start(start);
        oscillator.stop(start + 0.2);
    }
};

// A bubble's actions menu opens below its button unless the messages area has no room for it there
// and more above: a menu past the area's bottom sits under the composer, where a thumb must scroll to it.
window.messageActions = {
    opensUp: function (button, menuHeight) {
        if (!button) return false;
        const area = button.closest('.messages-area');
        const bounds = area ? area.getBoundingClientRect() : { top: 0, bottom: window.innerHeight };
        const rect = button.getBoundingClientRect();
        const below = bounds.bottom - rect.bottom;
        const above = rect.top - bounds.top;
        return below < menuHeight && above > below;
    }
};
