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

        const doDrag = (e) => {
            const newWidth = startWidth + e.pageX - startX;
            if (newWidth > 50) header.style.width = `${newWidth}px`;
        };

        const stopDrag = () => {
            document.removeEventListener('mousemove', doDrag);
            document.removeEventListener('mouseup', stopDrag);
            const widths = readWidths(tableId);
            widths[header.dataset.col] = parseInt(header.style.width || header.offsetWidth, 10);
            writeWidths(tableId, widths);
        };

        resizer.addEventListener('mousedown', (e) => {
            startX = e.pageX;
            startWidth = parseInt(window.getComputedStyle(header).width, 10);
            document.addEventListener('mousemove', doDrag);
            document.addEventListener('mouseup', stopDrag);

            // A drag's mouseup (or even a stray click on the resizer strip)
            // fires a click that bubbles into the header's sort handler.
            // Swallow the very next click in the capture phase, before it
            // can reach Blazor's delegated listener.
            const swallowClick = (ev) => ev.stopPropagation();
            document.addEventListener('click', swallowClick, { capture: true, once: true });
            setTimeout(() => document.removeEventListener('click', swallowClick, { capture: true }), 0);

            e.preventDefault();
            e.stopPropagation();
        });
    }

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
