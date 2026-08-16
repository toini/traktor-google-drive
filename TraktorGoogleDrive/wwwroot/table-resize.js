// Drag-to-resize playlist columns, persisted per table in localStorage.
// (Was previously bundled into wavesurfer-blazor.js alongside a waveform player
// that rendered hardcoded dummy peaks; only this survived.)

window.initResizableTable = function (tableId) {
    const table = document.getElementById(tableId);
    if (!table || table.dataset.resizableInit === '1') return;
    table.dataset.resizableInit = '1';

    const headers = [...table.querySelectorAll('th')];
    const storageKey = `table-widths-${tableId}`;

    const saved = localStorage.getItem(storageKey);
    if (saved) {
        try {
            const widths = JSON.parse(saved);
            headers.forEach((h, i) => {
                if (widths[i]) h.style.width = `${widths[i]}px`;
            });
        } catch {
            localStorage.removeItem(storageKey);
        }
    }

    headers.forEach((header, index) => {
        if (index === headers.length - 1) return; // last column has nothing to its right

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
            localStorage.setItem(
                storageKey,
                JSON.stringify(headers.map((h) => parseInt(h.style.width || h.offsetWidth, 10))),
            );
        };

        resizer.addEventListener('mousedown', (e) => {
            startX = e.pageX;
            startWidth = parseInt(window.getComputedStyle(header).width, 10);
            document.addEventListener('mousemove', doDrag);
            document.addEventListener('mouseup', stopDrag);
            e.preventDefault();
            e.stopPropagation();
        });
    });
};
