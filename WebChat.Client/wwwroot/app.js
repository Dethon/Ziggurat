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
        // be, and the composer's textarea is not always focused when it lands.
        document.addEventListener('paste', function (e) {
            if (!e.clipboardData) return;
            const files = Array.from(e.clipboardData.files || []);
            if (files.length > 0 && hand(files)) e.preventDefault();
        });
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

window.chatScroll = {
    _stickyState: true,  // Track if we should stick to bottom
    _element: null,
    _scrollHandler: null,

    // Check if user is scrolled to near the bottom (within threshold)
    isAtBottom: function (element) {
        if (!element) return true;
        const threshold = 50; // pixels from bottom to consider "at bottom"
        return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold;
    },

    // Scroll element to the bottom with smooth animation
    scrollToBottom: function (element, smooth) {
        if (!element) return;
        this._stickyState = true;  // Reset sticky state when forcing scroll
        requestAnimationFrame(() => {
            if (smooth) {
                element.scrollTo({
                    top: element.scrollHeight,
                    behavior: 'smooth'
                });
            } else {
                element.scrollTop = element.scrollHeight;
            }
        });
    },

    // Initialize scroll tracking for an element
    initStickyScroll: function (element) {
        if (!element) return;

        // Clean up previous handler if exists
        if (this._element && this._scrollHandler) {
            this._element.removeEventListener('scroll', this._scrollHandler);
        }

        this._element = element;
        this._stickyState = true;

        // Track scroll position to detect user scrolling away
        const self = this;
        this._scrollHandler = () => {
            self._stickyState = self.isAtBottom(element);
        };

        element.addEventListener('scroll', this._scrollHandler, {passive: true});
    },

    // Scroll to bottom only if sticky state is true
    scrollToBottomIfSticky: function (element) {
        if (!element) return;
        if (this._stickyState) {
            requestAnimationFrame(() => {
                element.scrollTop = element.scrollHeight;
            });
        }
    },

    // Dispose scroll tracking
    dispose: function () {
        if (this._element && this._scrollHandler) {
            this._element.removeEventListener('scroll', this._scrollHandler);
        }
        this._element = null;
        this._scrollHandler = null;
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
        // The conversation list scrolls natively; a pull-down at its very top collapses the
        // sheet. Touch handlers (not pointer) so normal list scrolling keeps its momentum.
        if (this._rows) {
            this._rows.addEventListener('touchstart', this._onRowsTouchStart, { passive: true });
            this._rows.addEventListener('touchmove', this._onRowsTouchMove, { passive: false });
            this._rows.addEventListener('touchend', this._onRowsTouchEnd);
            this._rows.addEventListener('touchcancel', this._onRowsTouchEnd);
        }
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

