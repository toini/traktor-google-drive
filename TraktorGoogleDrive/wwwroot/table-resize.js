// Drag-to-resize playlist columns, persisted per table in localStorage.
// (Was previously bundled into wavesurfer-blazor.js alongside a waveform player
// that rendered hardcoded dummy peaks; only this survived.)
//
// Widths are keyed by each header's data-col id, not by position: columns can
// be hidden and shown per playlist, and a positional key would move every
// saved width onto whichever column slid into that slot.
//
//   table-widths:v2:<tableId>  ->  {"title": 260, "label": 140, ...}
//
// Only the column the user actually dragged is stored. The others stay auto,
// so with table-layout: fixed they absorb the slack when a column is hidden
// instead of every remembered width being stretched.

(function () {
    const storageKey = (tableId) => `table-widths:v2:${tableId}`;
    // The v1 format: an array of widths by header position.
    const legacyKey = (tableId) => `table-widths-${tableId}`;

    function readWidths(tableId) {
        try {
            const parsed = JSON.parse(localStorage.getItem(storageKey(tableId)) || '{}');
            return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
        } catch {
            return {};
        }
    }

    function writeWidths(tableId, widths) {
        try {
            localStorage.setItem(storageKey(tableId), JSON.stringify(widths));
        } catch {
            /* storage unavailable or full: resizing still works for this visit */
        }
    }

    // A v1 array is only meaningful against the column order it was saved
    // with, which the table declares in data-legacy-cols. Map it onto ids
    // when the length matches that order exactly; otherwise it was saved by
    // an even older column set and cannot be placed, so drop it.
    function migrateLegacy(table, tableId) {
        let raw;
        try {
            raw = localStorage.getItem(legacyKey(tableId));
        } catch {
            return;
        }
        if (raw === null) return;

        try {
            const legacyCols = (table.dataset.legacyCols || '').split(',').filter(Boolean);
            const widths = JSON.parse(raw);
            if (Array.isArray(widths) && legacyCols.length > 0 && widths.length === legacyCols.length
                && localStorage.getItem(storageKey(tableId)) === null) {
                const migrated = {};
                legacyCols.forEach((id, i) => {
                    const w = parseInt(widths[i], 10);
                    if (w > 0) migrated[id] = w;
                });
                writeWidths(tableId, migrated);
            }
        } catch {
            /* unparseable: nothing to carry over */
        }

        try {
            localStorage.removeItem(legacyKey(tableId));
        } catch {
            /* ignore */
        }
    }

    function wireHeader(table, tableId, header) {
        header.dataset.resizable = '1';

        const saved = readWidths(tableId)[header.dataset.col];
        if (saved > 0) header.style.width = `${saved}px`;

        const resizer = document.createElement('div');
        resizer.className = 'column-resizer';
        header.style.position = 'relative';
        header.appendChild(resizer);

        let startX = 0;
        let startWidth = 0;
        let pointerId = null;

        const doDrag = (e) => {
            if (e.pointerId !== pointerId) return;
            const newWidth = startWidth + e.clientX - startX;
            if (newWidth > 50) header.style.width = `${newWidth}px`;
        };

        const stopDrag = (e) => {
            if (e.pointerId !== pointerId) return;
            pointerId = null;
            resizer.removeEventListener('pointermove', doDrag);
            resizer.removeEventListener('pointerup', stopDrag);
            resizer.removeEventListener('pointercancel', stopDrag);
            resizer.removeEventListener('lostpointercapture', stopDrag);
            if (resizer.hasPointerCapture(e.pointerId)) resizer.releasePointerCapture(e.pointerId);
            const widths = readWidths(tableId);
            widths[header.dataset.col] = parseInt(header.style.width || header.offsetWidth, 10);
            writeWidths(tableId, widths);
            // The click this press produces comes after pointerup: right away
            // for a mouse, a moment later for a tap. Swallow it if it comes.
            suppressClickUntil = performance.now() + CLICK_AFTER_DRAG_MS;
        };

        resizer.addEventListener('pointerdown', (e) => {
            if (e.button !== 0 || pointerId !== null) return;
            pointerId = e.pointerId;
            startX = e.clientX;
            startWidth = parseInt(window.getComputedStyle(header).width, 10);
            // Capture keeps the drag going when the pointer leaves the 6px
            // strip, and routes every move and the release to the handle.
            resizer.setPointerCapture(e.pointerId);
            resizer.addEventListener('pointermove', doDrag);
            resizer.addEventListener('pointerup', stopDrag);
            resizer.addEventListener('pointercancel', stopDrag);
            resizer.addEventListener('lostpointercapture', stopDrag);
            suppressClickUntil = Infinity; // until the press ends
            e.preventDefault(); // no text selection while dragging
        });
    }

    // A press on the handle ends in a click on the header (the common ancestor
    // of where it started and ended), which is the header's sort. Blazor's own
    // listener cannot be told apart from a real sort click, so the click is
    // stopped in the capture phase, before it reaches Blazor. It used to be a
    // one-shot listener removed by setTimeout(0) from mousedown, which ran
    // long before the mouseup, so every real drag still sorted.
    const CLICK_AFTER_DRAG_MS = 400;
    let suppressClickUntil = 0;
    document.addEventListener('click', (e) => {
        if (performance.now() > suppressClickUntil) return;
        suppressClickUntil = 0; // one click per press
        e.stopPropagation();
        e.preventDefault();
    }, { capture: true });

    // Idempotent, and cheap enough to call after every render: it only wires
    // headers it has not seen. A column shown again is a new <th> element, so
    // it gets its resizer and its remembered width here.
    window.initResizableTable = function (tableId) {
        const table = document.getElementById(tableId);
        if (!table) return;

        if (table.dataset.resizableInit !== '1') {
            table.dataset.resizableInit = '1';
            migrateLegacy(table, tableId);
        }

        table.querySelectorAll('thead th[data-col]').forEach((header) => {
            if (header.dataset.resizable !== '1') wireHeader(table, tableId, header);
        });
    };
})();
