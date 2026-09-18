(function () {
    function show(anchor) {
        const popup = anchor.querySelector('.tooltip-popup');
        if (!popup) return;

        popup.style.display = 'block';
        popup.style.position = 'fixed';

        const rect = anchor.getBoundingClientRect();

        // Position above by default
        let top = rect.top - popup.offsetHeight - 6;

        // If it would go above viewport, show below instead
        if (top < 4) {
            top = rect.bottom + 6;
        }

        // Align right edge to anchor right edge
        let left = rect.right - popup.offsetWidth;

        // If it would go off-screen left, clamp to viewport edge
        if (left < 4) {
            left = 4;
        }

        popup.style.top = top + 'px';
        popup.style.left = left + 'px';
    }

    function hide(anchor) {
        const popup = anchor.querySelector('.tooltip-popup');
        if (!popup) return;
        popup.style.display = 'none';
    }

    // Captured on the document, mouseenter and mouseleave also fire for the document itself
    // and for text nodes, neither of which has closest(); those are never inside an anchor.
    function closest(el) {
        return el instanceof Element ? el.closest('.tooltip-anchor') : null;
    }

    document.addEventListener('mouseenter', function (e) {
        const anchor = closest(e.target);
        if (anchor) show(anchor);
    }, true);

    document.addEventListener('mouseleave', function (e) {
        const anchor = closest(e.target);
        if (anchor) hide(anchor);
    }, true);

    document.addEventListener('focusin', function (e) {
        const anchor = closest(e.target);
        if (anchor) show(anchor);
    });

    document.addEventListener('focusout', function (e) {
        const anchor = closest(e.target);
        if (anchor) hide(anchor);
    });
})();
