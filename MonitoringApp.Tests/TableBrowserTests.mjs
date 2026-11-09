import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../MonitoringApp/wwwroot/table-resize.js", import.meta.url), "utf8");
const key = "alert-console-column-width-alert";

class Target {
    listeners = new Map();
    dataset = {};
    style = {};
    isConnected = true;
    nodeType = 1;
    captures = new Set();
    addEventListener(name, listener) {
        if (!this.listeners.has(name)) this.listeners.set(name, new Set());
        this.listeners.get(name).add(listener);
    }
    removeEventListener(name, listener) { this.listeners.get(name)?.delete(listener); }
    fire(name, supplied = {}) {
        const event = { target: this, pointerId: 7, clientX: 10, preventDefault() {}, ...supplied };
        for (const listener of [...(this.listeners.get(name) ?? [])]) listener(event);
    }
    count() { return [...this.listeners.values()].reduce((sum, listeners) => sum + listeners.size, 0); }
    setPointerCapture(id) { this.captures.add(id); }
    hasPointerCapture(id) { return this.captures.has(id); }
    releasePointerCapture(id) { this.captures.delete(id); }
    querySelectorAll() { return []; }
    matches() { return false; }
}

function fixture({ saved, failure } = {}) {
    const failures = { ...failure };
    const storage = new Map(saved === undefined ? [] : [[key, saved]]);
    const writes = [];
    const logs = [];
    const observers = [];
    const frames = new Map();
    const canceledFrames = [];
    let frameId = 0;
    const document = new Target();
    document.regions = [];
    document.body = new Target();
    function addRegion() {
        const region = new Target();
        const table = new Target();
        const column = { style: { width: "190px" } };
        const resizer = new Target();
        resizer.dataset = { columnIndex: "0", minWidth: "140", columnKey: "alert" };
        resizer.parentElement = { getBoundingClientRect: () => ({ width: parseFloat(column.style.width) }) };
        resizer.closest = selector => selector === "table" ? table : selector === ".table-region" ? region :
            selector === ".column-resizer" ? resizer : null;
        resizer.matches = selector => selector === ".column-resizer";
        resizer.setAttribute = (name, value) => { resizer[name] = value; };
        const scrollbar = new Target();
        scrollbar.scrollLeft = 0;
        scrollbar.firstElementChild = { style: {} };
        const container = new Target();
        container.scrollLeft = 0;
        container.querySelector = () => table;
        Object.defineProperty(table, "scrollWidth", { get() {
            if (failures.layout) throw failures.layout;
            return 900;
        } });
        table.querySelectorAll = selector => selector === "col" ? [column] : selector === ".column-resizer" ? [resizer] : [];
        const storageError = { hidden: true, textContent: "Localized storage error" };
        const browserError = { hidden: true, textContent: "Localized browser error" };
        const retry = new Target();
        retry.hidden = true;
        retry.closest = selector => selector === ".table-region" ? region : selector === "[data-table-retry]" ? retry : null;
        region.querySelector = selector => ({
            ".table-scrollbar": scrollbar, ".table-scroll": container,
            "[data-table-storage-error]": storageError, "[data-table-browser-error]": browserError,
            "[data-table-retry]": retry
        })[selector];
        region.querySelectorAll = selector => selector === ".column-resizer" ? [resizer] :
            selector === "[data-table-storage-error], [data-table-browser-error]" ? [storageError, browserError] : [];
        region.matches = selector => selector === ".table-region";
        document.regions.push(region);
        return { region, table, column, resizer, scrollbar, container, storageError, browserError, retry };
    }
    const item = addRegion();
    document.querySelectorAll = selector => selector === ".table-region" ? document.regions :
        document.regions.flatMap(region => region.querySelectorAll(selector));
    let mutation;
    vm.runInNewContext(source, {
        document, Node: { ELEMENT_NODE: 1 },
        localStorage: {
            getItem(name) { if (failures.read) throw failures.read; return storage.get(name) ?? null; },
            setItem(name, value) {
                if (failures.writeBefore) throw failures.writeBefore;
                storage.set(name, value); writes.push([name, value]);
                if (failures.writeAfter) throw failures.writeAfter;
            }
        },
        console: { error: (...arguments_) => logs.push(arguments_) },
        requestAnimationFrame(callback) { const id = ++frameId; frames.set(id, callback); return id; },
        cancelAnimationFrame(id) { canceledFrames.push(id); frames.delete(id); },
        ResizeObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); this.disconnected = false; }
            observe(table) { this.table = table; if (failures.observe) throw failures.observe; }
            disconnect() { if (failures.disconnect) throw failures.disconnect; this.disconnected = true; }
        },
        MutationObserver: class {
            constructor(callback) { mutation = callback; }
            observe() {}
        }
    }, { filename: "table-resize.js" });
    return { ...item, document, failures, storage, writes, logs, observers, frames, canceledFrames, addRegion,
        mutate: addedNodes => mutation([{ addedNodes }]),
        flushFrames() { for (const callback of frames.values()) callback(); frames.clear(); },
        detach() {
            for (const node of [item.region, item.table, item.resizer, item.scrollbar, item.container]) node.isConnected = false;
            document.regions = [];
            mutation([{ addedNodes: [] }]);
        }
    };
}

test("stored widths retain exact storage key, rounding and minimum without modifying table data", () => {
    const item = fixture({ saved: "139.4" });
    assert.equal(item.column.style.width, "140px");
    assert.equal(item.resizer["aria-valuenow"], "140");
    assert.equal(item.storage.get(key), "140");
    assert.equal(item.scrollbar.firstElementChild.style.width, "900px");
    assert.equal(item.retry.hidden, true);
    assert.deepEqual(item.logs, []);
});

for (const phase of ["read", "writeBefore", "writeAfter"]) {
    test(`${phase}: storage failure is visible and does not abort scrolling or explicit recovery`, () => {
        const failure = new Error("PRIVATE_STORAGE_DETAIL");
        const item = fixture({ saved: "240", failure: { [phase]: failure } });
        assert.equal(item.storageError.hidden, false);
        assert.equal(item.storageError.textContent, "Localized storage error");
        assert.equal(item.retry.hidden, false);
        assert.equal(item.observers.length, 1);
        assert.equal(item.scrollbar.count(), 1);
        assert.equal(item.container.count(), 1);
        assert.equal(item.logs[0][1], failure);
        delete item.failures[phase];
        item.document.fire("click", { target: item.retry });
        assert.equal(item.column.style.width, "240px");
        assert.equal(item.storageError.hidden, true);
        assert.equal(item.retry.hidden, true);
        assert.equal(item.observers[0].disconnected, true);
        assert.equal(item.scrollbar.count(), 1);
    });
}

for (const saved of ["invalid", "Infinity", "-10"]) {
    test(`invalid stored width ${saved}: explicit storage warning, no invalid CSS or rewrite`, () => {
        const item = fixture({ saved });
        assert.equal(item.storageError.hidden, false);
        assert.equal(item.column.style.width, "190px");
        assert.deepEqual(item.writes, []);
        assert.equal(item.scrollbar.count(), 1);
    });
}

for (const completion of ["pointerup", "pointercancel"]) {
    test(`${completion}: keeps already persisted move widths and releases all capture/listeners`, () => {
        const item = fixture();
        item.document.fire("pointerdown", { target: item.resizer });
        item.resizer.fire("pointermove", { clientX: 60 });
        assert.equal(item.storage.get(key), "240");
        item.resizer.fire(completion);
        assert.equal(item.resizer.count(), 0);
        assert.equal(item.resizer.hasPointerCapture(7), false);
        assert.equal(item.column.style.width, "240px");
        assert.equal(item.writes.length, 1);
    });
}

test("keyboard steps remain ten pixels and minimum-clamped", () => {
    const item = fixture({ saved: "140" });
    item.document.fire("keydown", { target: item.resizer, key: "ArrowLeft" });
    assert.equal(item.storage.get(key), "140");
    item.document.fire("keydown", { target: item.resizer, key: "ArrowRight" });
    assert.equal(item.storage.get(key), "150");
});

test("replacement gesture releases the old pointer before recapturing and has only three listeners", () => {
    const item = fixture();
    item.document.fire("pointerdown", { target: item.resizer });
    item.resizer.fire("pointermove", { clientX: 60 });
    item.document.fire("pointerdown", { target: item.resizer, clientX: 60 });
    assert.equal(item.resizer.count(), 3);
    assert.equal(item.resizer.hasPointerCapture(7), true);
    item.resizer.fire("pointermove", { clientX: 70 });
    assert.equal(item.storage.get(key), "250");
});

test("removed region disconnects observer, cancels frame and resize without late storage writes", () => {
    const item = fixture();
    item.document.fire("pointerdown", { target: item.resizer });
    item.resizer.fire("pointermove", { clientX: 60 });
    item.scrollbar.scrollLeft = 80;
    item.scrollbar.fire("scroll");
    assert.equal(item.container.scrollLeft, 80);
    item.detach();
    assert.equal(item.observers[0].disconnected, true);
    assert.equal(item.scrollbar.count(), 0);
    assert.equal(item.container.count(), 0);
    assert.equal(item.resizer.count(), 0);
    assert.equal(item.resizer.hasPointerCapture(7), false);
    assert.equal(item.frames.size, 0);
    assert.equal(item.canceledFrames.length, 1);
    item.resizer.fire("pointermove", { clientX: 300 });
    assert.equal(item.writes.length, 1);
});

test("added region itself restores widths and initializes once across later mutations", () => {
    const item = fixture({ saved: "240" });
    const added = item.addRegion();
    item.mutate([added.region]);
    assert.equal(added.column.style.width, "240px");
    assert.equal(added.scrollbar.count(), 1);
    assert.equal(item.observers.length, 2);
    item.mutate([]);
    assert.equal(item.observers.length, 2);
});

test("scrollbar and native container keep bidirectional synchronized offsets", () => {
    const item = fixture();
    item.scrollbar.scrollLeft = 80;
    item.scrollbar.fire("scroll");
    assert.equal(item.container.scrollLeft, 80);
    item.flushFrames();
    item.container.scrollLeft = 120;
    item.container.fire("scroll");
    assert.equal(item.scrollbar.scrollLeft, 120);
});

for (const phase of ["observe", "layout"]) {
    test(`${phase}: partial browser setup cleans listeners, never auto-retries and explicitly recovers`, () => {
        const failure = new Error("PRIVATE_BROWSER_DETAIL");
        const item = fixture({ failure: { [phase]: failure } });
        assert.equal(item.browserError.hidden, false);
        assert.equal(item.browserError.textContent, "Localized browser error");
        assert.equal(item.scrollbar.count(), 0);
        assert.equal(item.container.count(), 0);
        assert.equal(item.observers[0].disconnected, true);
        item.mutate([]);
        assert.equal(item.observers.length, 1);
        delete item.failures[phase];
        item.document.fire("click", { target: item.retry });
        assert.equal(item.observers.length, 2);
        assert.equal(item.browserError.hidden, true);
        assert.equal(item.scrollbar.count(), 1);
    });
}

test("cleanup failure independently removes listeners and blocks new observer until explicit retry succeeds", () => {
    const item = fixture();
    const failure = new Error("PRIVATE_CLEANUP_DETAIL");
    item.failures.disconnect = failure;
    item.document.fire("click", { target: item.retry });
    assert.equal(item.browserError.hidden, false);
    assert.equal(item.scrollbar.count(), 0);
    assert.equal(item.container.count(), 0);
    assert.equal(item.observers.length, 1);
    assert.equal(item.logs[0][1], failure);
    delete item.failures.disconnect;
    item.document.fire("click", { target: item.retry });
    assert.equal(item.observers[0].disconnected, true);
    assert.equal(item.observers.length, 2);
    assert.equal(item.browserError.hidden, true);
});

test("a successful later width write does not hide an earlier failed load until explicit full reload", () => {
    const item = fixture({ failure: { read: new Error("PRIVATE_READ_DETAIL") } });
    delete item.failures.read;
    item.document.fire("keydown", { target: item.resizer, key: "ArrowRight" });
    assert.equal(item.storage.get(key), "200");
    assert.equal(item.storageError.hidden, false);
    item.document.fire("click", { target: item.retry });
    assert.equal(item.storageError.hidden, true);
});

test("failed pointer capture cleans partial gesture ownership and allows a later explicit gesture", () => {
    const item = fixture();
    const capture = item.resizer.setPointerCapture.bind(item.resizer);
    const failure = new Error("PRIVATE_CAPTURE_DETAIL");
    item.resizer.setPointerCapture = () => { throw failure; };
    item.document.fire("pointerdown", { target: item.resizer });
    assert.equal(item.browserError.hidden, false);
    assert.equal(item.logs[0][1], failure);
    assert.equal(item.resizer.count(), 0);
    assert.equal(item.resizer.hasPointerCapture(7), false);
    item.resizer.setPointerCapture = capture;
    item.document.fire("pointerdown", { target: item.resizer });
    item.resizer.fire("pointermove", { clientX: 60 });
    item.resizer.fire("pointerup");
    assert.equal(item.storage.get(key), "240");
    assert.equal(item.resizer.count(), 0);
});

test("invalid column metadata surfaces an error and stops the active gesture without invalid CSS or storage", () => {
    const item = fixture();
    item.resizer.dataset.columnIndex = "999";
    item.document.fire("pointerdown", { target: item.resizer });
    item.resizer.fire("pointermove", { clientX: 60 });
    assert.equal(item.browserError.hidden, false);
    assert.equal(item.column.style.width, "190px");
    assert.equal(item.resizer.count(), 0);
    assert.equal(item.resizer.hasPointerCapture(7), false);
    assert.deepEqual(item.writes, []);
});
