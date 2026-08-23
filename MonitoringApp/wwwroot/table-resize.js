(() => {
    const storagePrefix = "alert-console-column-width-";

    function setColumnWidth(resizer, width) {
        const table = resizer.closest("table");
        const index = Number(resizer.dataset.columnIndex);
        const minimum = Number(resizer.dataset.minWidth);
        const column = table?.querySelectorAll("col")[index];
        if (!column) {
            return;
        }

        const constrainedWidth = Math.max(minimum, Math.round(width));
        column.style.width = `${constrainedWidth}px`;
        resizer.setAttribute("aria-valuenow", constrainedWidth.toString());
        localStorage.setItem(storagePrefix + resizer.dataset.columnKey, constrainedWidth.toString());
    }

    function restoreColumnWidths(root = document) {
        root.querySelectorAll?.(".column-resizer").forEach(resizer => {
            const savedWidth = Number(localStorage.getItem(storagePrefix + resizer.dataset.columnKey));
            if (savedWidth > 0) {
                setColumnWidth(resizer, savedWidth);
            }
        });
    }

    document.addEventListener("pointerdown", event => {
        const resizer = event.target.closest?.(".column-resizer");
        if (!resizer) {
            return;
        }

        event.preventDefault();
        const startX = event.clientX;
        const startWidth = resizer.parentElement.getBoundingClientRect().width;
        resizer.setPointerCapture(event.pointerId);

        const resize = moveEvent => setColumnWidth(resizer, startWidth + moveEvent.clientX - startX);
        const stop = () => {
            resizer.removeEventListener("pointermove", resize);
            resizer.removeEventListener("pointerup", stop);
            resizer.removeEventListener("pointercancel", stop);
        };

        resizer.addEventListener("pointermove", resize);
        resizer.addEventListener("pointerup", stop);
        resizer.addEventListener("pointercancel", stop);
    });

    document.addEventListener("keydown", event => {
        const resizer = event.target.closest?.(".column-resizer");
        if (!resizer || !["ArrowLeft", "ArrowRight"].includes(event.key)) {
            return;
        }

        event.preventDefault();
        const direction = event.key === "ArrowRight" ? 1 : -1;
        setColumnWidth(resizer, resizer.parentElement.getBoundingClientRect().width + direction * 10);
    });

    restoreColumnWidths();
    new MutationObserver(mutations => {
        for (const mutation of mutations) {
            mutation.addedNodes.forEach(node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    restoreColumnWidths(node);
                }
            });
        }
    }).observe(document.body, { childList: true, subtree: true });
})();