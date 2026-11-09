const cleanups = new WeakMap();

export function initialize(dotNetReference, root) {
    dispose(root);
    let cancelGesture = null;
    const gestureFinished = () => { cancelGesture = null; };
    const onPointerDown = event => {
        if (event.button !== 0) return;
        const handle = event.target.closest("[data-inbox-resize-handle]");
        if (!handle || !root.contains(handle)) {
            const dragHandle = event.target.closest("[data-inbox-drag-handle]");
            if (dragHandle && root.contains(dragHandle) &&
                !event.target.closest("button, input, select, textarea, a, [contenteditable='true']")) {
                cancelGesture?.();
                cancelGesture = beginMove(event, dragHandle, dotNetReference, gestureFinished);
            }
            return;
        }

        cancelGesture?.();
        cancelGesture = beginResize(event, handle, dotNetReference, gestureFinished);
    };

    root.addEventListener("pointerdown", onPointerDown);
    cleanups.set(root, () => {
        root.removeEventListener("pointerdown", onPointerDown);
        cancelGesture?.();
    });
}

function beginMove(event, handle, dotNetReference, gestureFinished) {
    event.preventDefault();
    event.stopPropagation();

    const widget = handle.closest("[data-inbox-widget-id]");
    const grid = widget.closest("[data-inbox-dashboard-grid]");
    const styles = getComputedStyle(grid);
    const columns = Number(grid.dataset.columns);
    const rows = Number(grid.dataset.rows);
    const rowHeight = Number(grid.dataset.rowHeight);
    const columnGap = parseFloat(styles.columnGap) || 0;
    const rowGap = parseFloat(styles.rowGap) || 0;
    const paddingLeft = parseFloat(styles.paddingLeft) || 0;
    const paddingRight = parseFloat(styles.paddingRight) || 0;
    const cellWidth = (grid.clientWidth - paddingLeft - paddingRight - columnGap * (columns - 1)) / columns;
    const startX = event.clientX;
    const startY = event.clientY;
    const startColumn = Number.parseInt(widget.style.gridColumn, 10);
    const startRow = Number.parseInt(widget.style.gridRow, 10);
    const columnSpan = Number(widget.dataset.columnSpan);
    const rowSpan = Number(widget.dataset.rowSpan);
    let nextColumn = startColumn;
    let nextRow = startRow;
    const originalTransform = widget.style.transform;

    handle.setPointerCapture(event.pointerId);
    widget.classList.add("is-moving");

    const move = moveEvent => {
        const deltaX = moveEvent.clientX - startX;
        const deltaY = moveEvent.clientY - startY;
        nextColumn = clamp(startColumn + Math.round(deltaX / (cellWidth + columnGap)), 1, columns - columnSpan + 1);
        nextRow = clamp(startRow + Math.round(deltaY / (rowHeight + rowGap)), 1, rows - rowSpan + 1);
        widget.style.transform = `translate(${deltaX}px, ${deltaY}px)`;
    };

    const cleanup = () => {
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", stop);
        handle.removeEventListener("pointercancel", stop);
        widget.style.transform = originalTransform;
        widget.classList.remove("is-moving");
        if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
        gestureFinished();
    };
    const stop = async () => {
        cleanup();
        await dotNetReference.invokeMethodAsync("CommitDashboardMove", widget.dataset.inboxWidgetId, nextColumn, nextRow);
    };

    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", stop);
    handle.addEventListener("pointercancel", stop);
    return cleanup;
}

function beginResize(event, handle, dotNetReference, gestureFinished) {
    event.preventDefault();
    event.stopPropagation();

    const widget = handle.closest("[data-inbox-widget-id]");
    const grid = widget.closest("[data-inbox-dashboard-grid]");
    const styles = getComputedStyle(grid);
    const columns = Number(grid.dataset.columns);
    const rowHeight = Number(grid.dataset.rowHeight);
    const columnGap = parseFloat(styles.columnGap) || 0;
    const rowGap = parseFloat(styles.rowGap) || 0;
    const paddingLeft = parseFloat(styles.paddingLeft) || 0;
    const paddingRight = parseFloat(styles.paddingRight) || 0;
    const cellWidth = (grid.clientWidth - paddingLeft - paddingRight - columnGap * (columns - 1)) / columns;
    const startX = event.clientX;
    const startY = event.clientY;
    const startColumns = Number(widget.dataset.columnSpan);
    const startRows = Number(widget.dataset.rowSpan);
    let nextColumns = startColumns;
    let nextRows = startRows;
    const originalColumnEnd = widget.style.gridColumnEnd;
    const originalRowEnd = widget.style.gridRowEnd;

    handle.setPointerCapture(event.pointerId);
    widget.classList.add("is-resizing");

    const move = moveEvent => {
        nextColumns = clamp(Math.round(startColumns + (moveEvent.clientX - startX) / (cellWidth + columnGap)), 1, columns);
        nextRows = clamp(Math.round(startRows + (moveEvent.clientY - startY) / (rowHeight + rowGap)), 1, 10);
        widget.style.gridColumnEnd = `span ${nextColumns}`;
        widget.style.gridRowEnd = `span ${nextRows}`;
    };

    const cleanup = restorePreview => {
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", stop);
        handle.removeEventListener("pointercancel", stop);
        widget.classList.remove("is-resizing");
        if (restorePreview) {
            widget.style.gridColumnEnd = originalColumnEnd;
            widget.style.gridRowEnd = originalRowEnd;
        }
        if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
        gestureFinished();
    };
    const stop = async () => {
        cleanup(false);
        await dotNetReference.invokeMethodAsync("CommitDashboardResize", widget.dataset.inboxWidgetId, nextColumns, nextRows);
    };

    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", stop);
    handle.addEventListener("pointercancel", stop);
    return () => cleanup(true);
}

export function dispose(root) {
    cleanups.get(root)?.();
    cleanups.delete(root);
}

function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
}
