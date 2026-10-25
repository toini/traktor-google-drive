// Sidebar mode for MainLayout. A classic script, not a module, so the layout
// can ask isNarrow() synchronously before its first render.
(() => {
    // Keep in step with the max-width media queries in MainLayout.razor.css.
    const NARROW = '(max-width: 999.98px)';
    const media = () => window.matchMedia(NARROW);

    window.traktorLayout = {
        isNarrow: () => media().matches,

        /** Tells the layout when the width crosses the breakpoint and when Esc is pressed. */
        watch(layout) {
            media().addEventListener('change', (e) => layout.invokeMethodAsync('OnNarrowChanged', e.matches));
            document.addEventListener('keydown', (e) => {
                if (e.key !== 'Escape' || !document.querySelector('.page.drawer-open')) return;
                e.preventDefault();
                layout.invokeMethodAsync('CloseFromEscape');
            });
        },

        /** Focuses the active playlist in the drawer, else the drawer itself. */
        focusInto(container) {
            if (!container) return;
            const target = container.querySelector('a.tree-row.active') ?? container;
            target.focus({ preventScroll: false });
        },
    };
})();
