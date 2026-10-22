import { initialize as initializeWidgets, dispose as disposeWidgets } from "./inbox-dashboard.js";

const cleanups = new WeakMap();

export function initialize(dotNetReference, root) {
    initializeWidgets(dotNetReference, root);
    let draggedQuery = null;
    let preview = null;
    let cancelResize = null;

    const clearPreview = () => {
        preview?.remove();
        preview = null;
        root.classList.remove("is-query-dragging");
    };
    const queryCard = target => target.closest('[data-analysis-query-id][draggable="true"]');
    const dragStart = event => {
        const card = queryCard(event.target);
        if (!card) return;
        draggedQuery = card;
        event.dataTransfer.setData("application/x-analysis-query", card.dataset.analysisQueryId);
        event.dataTransfer.effectAllowed = "move";
    };
    const dragOver = event => {
        const grid = event.target.closest("[data-inbox-dashboard-grid]");
        if (!draggedQuery || !grid || !root.contains(grid)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        root.classList.add("is-query-dragging");
        const cell = dropCell(grid, event, draggedQuery);
        if (!preview) {
            preview = document.createElement("div");
            preview.className = "analysis-query-drop-preview";
            preview.setAttribute("aria-hidden", "true");
            grid.append(preview);
        }
        preview.style.gridColumn = `${cell.column} / span ${cell.columns}`;
        preview.style.gridRow = `${cell.row} / span ${cell.rows}`;
    };
    const drop = async event => {
        const grid = event.target.closest("[data-inbox-dashboard-grid]");
        if (!draggedQuery || !grid || !root.contains(grid)) return;
        event.preventDefault();
        const card = draggedQuery;
        const cell = dropCell(grid, event, card);
        draggedQuery = null;
        clearPreview();
        await dotNetReference.invokeMethodAsync("PlaceDashboardQuery", card.dataset.analysisQueryId, cell.column, cell.row);
    };
    const dragEnd = () => {
        draggedQuery = null;
        clearPreview();
    };
    const dragLeave = event => {
        const grid = root.querySelector("[data-inbox-dashboard-grid]");
        if (grid && !grid.contains(event.relatedTarget)) clearPreview();
    };
    const pointerDown = event => {
        const handle = event.target.closest("[data-analysis-panel-resize]");
        if (!handle || event.button !== 0) return;
        event.preventDefault();
        const panel = handle.closest("[data-analysis-panel]");
        const bounds = panel.getBoundingClientRect();
        const startX = event.clientX;
        const startY = event.clientY;
        let width = Math.round(bounds.width);
        let height = Math.round(bounds.height);
        handle.setPointerCapture(event.pointerId);
        const move = next => {
            width = Math.round(clamp(bounds.width + next.clientX - startX,
                Math.min(280, panel.parentElement.clientWidth), Math.min(2400, panel.parentElement.clientWidth)));
            height = Math.round(clamp(bounds.height + next.clientY - startY, 160, 900));
            panel.style.width = `${width}px`;
            panel.style.height = `${height}px`;
        };
        const cleanup = () => {
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", stop);
            handle.removeEventListener("pointercancel", cancel);
            cancelResize = null;
        };
        const cancel = () => {
            cleanup();
            panel.style.width = `${bounds.width}px`;
            panel.style.height = `${bounds.height}px`;
        };
        const stop = async () => {
            cleanup();
            await dotNetReference.invokeMethodAsync("CommitAnalysisPanelResize", panel.dataset.analysisPanel, width, height);
        };
        cancelResize?.();
        cancelResize = cancel;
        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", stop);
        handle.addEventListener("pointercancel", cancel);
    };
    const keyDown = async event => {
        const handle = event.target.closest("[data-analysis-panel-resize]");
        if (handle && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) {
            event.preventDefault();
            const panel = handle.closest("[data-analysis-panel]");
            const bounds = panel.getBoundingClientRect();
            const width = Math.round(clamp(bounds.width + (event.key === "ArrowRight" ? 20 : event.key === "ArrowLeft" ? -20 : 0),
                Math.min(280, panel.parentElement.clientWidth), Math.min(2400, panel.parentElement.clientWidth)));
            const height = Math.round(clamp(bounds.height + (event.key === "ArrowDown" ? 20 : event.key === "ArrowUp" ? -20 : 0), 160, 900));
            await dotNetReference.invokeMethodAsync("CommitAnalysisPanelResize", panel.dataset.analysisPanel, width, height);
            return;
        }
        const card = queryCard(event.target);
        if (card && (event.key === "Enter" || event.key === " ")) {
            event.preventDefault();
            const existing = root.querySelector(`[data-analysis-widget-query-id="${card.dataset.analysisQueryId}"]`);
            const grid = root.querySelector("[data-inbox-dashboard-grid]");
            const cell = existing
                ? { column: Number.parseInt(existing.style.gridColumn, 10), row: Number.parseInt(existing.style.gridRow, 10) }
                : firstFreeCell(grid, Number(card.dataset.columnSpan), Number(card.dataset.rowSpan));
            await dotNetReference.invokeMethodAsync("PlaceDashboardQuery", card.dataset.analysisQueryId, cell.column, cell.row);
        }
    };
    const listeners = { dragstart: dragStart, dragover: dragOver, drop, dragend: dragEnd,
        dragleave: dragLeave, pointerdown: pointerDown, keydown: keyDown };
    for (const [name, listener] of Object.entries(listeners)) root.addEventListener(name, listener);
    cleanups.set(root, () => {
        cancelResize?.();
        clearPreview();
        for (const [name, listener] of Object.entries(listeners)) root.removeEventListener(name, listener);
    });
}

function dropCell(grid, event, card) {
    const styles = getComputedStyle(grid);
    const bounds = grid.getBoundingClientRect();
    const columns = Number(grid.dataset.columns);
    const rows = Number(grid.dataset.rows);
    const paddingLeft = parseFloat(styles.paddingLeft);
    const paddingRight = parseFloat(styles.paddingRight);
    const paddingTop = parseFloat(styles.paddingTop);
    const columnGap = parseFloat(styles.columnGap);
    const rowGap = parseFloat(styles.rowGap);
    const cellWidth = (grid.clientWidth - paddingLeft - paddingRight - columnGap * (columns - 1)) / columns;
    const columnSpan = Math.min(Number(card.dataset.columnSpan), columns);
    const rowSpan = Math.min(Number(card.dataset.rowSpan), rows);
    return {
        column: clamp(Math.floor((event.clientX - bounds.left - paddingLeft) / (cellWidth + columnGap)) + 1, 1, columns - columnSpan + 1),
        row: clamp(Math.floor((event.clientY - bounds.top - paddingTop) / (Number(grid.dataset.rowHeight) + rowGap)) + 1, 1, rows - rowSpan + 1),
        columns: columnSpan,
        rows: rowSpan
    };
}

function firstFreeCell(grid, columns, rows) {
    const widgets = [...grid.querySelectorAll("[data-inbox-widget-id]")].map(widget => ({
        column: Number.parseInt(widget.style.gridColumn, 10), row: Number.parseInt(widget.style.gridRow, 10),
        columns: Number(widget.dataset.columnSpan), rows: Number(widget.dataset.rowSpan)
    }));
    for (let row = 1; row <= Number(grid.dataset.rows) - rows + 1; row++) {
        for (let column = 1; column <= Number(grid.dataset.columns) - columns + 1; column++) {
            if (widgets.every(widget => column + columns <= widget.column || widget.column + widget.columns <= column ||
                row + rows <= widget.row || widget.row + widget.rows <= row)) return { column, row };
        }
    }
    return { column: 1, row: Number(grid.dataset.rows) };
}

function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
}

export function dispose(root) {
    cleanups.get(root)?.();
    cleanups.delete(root);
    disposeWidgets(root);
}
