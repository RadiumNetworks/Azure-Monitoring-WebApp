import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../MonitoringApp/wwwroot/js/ui-language.js", import.meta.url), "utf8");

class Target {
    listeners = new Map();
    addEventListener(name, listener) {
        if (!this.listeners.has(name)) this.listeners.set(name, new Set());
        this.listeners.get(name).add(listener);
    }
    fire(name, supplied = {}) {
        const event = { target: this, defaultPrevented: false,
            preventDefault() { this.defaultPrevented = true; }, ...supplied };
        for (const listener of this.listeners.get(name) ?? []) listener(event);
        return event;
    }
}

class Element extends Target {
    dataset = {};
    value = "";
    defaultValue = "";
    type = "text";
    checked = false;
    defaultChecked = false;
    disabled = false;
    isConnected = true;
    editor = false;
    parentSelector = null;
    parentDialog = null;
    matches() { return this.editor; }
    closest(selector) { return selector === "dialog" ? this.parentDialog : this.parentSelector; }
    querySelectorAll() { return []; }
}

class Select extends Element {
    multiple = false;
    options = [];
    get selectedOptions() { return this.options.filter(option => option.selected); }
}

function fixture({ culture = "en-US", unsupported, initialValue = "original", kind = "text" } = {}) {
    const failures = {};
    const submissions = [];
    const logs = [];
    const timers = [];
    const input = kind === "multiple" ? new Select() : new Element();
    input.editor = true;
    input.type = kind;
    input.value = initialValue;
    input.defaultValue = "original";
    if (kind === "multiple") {
        input.multiple = true;
        input.options = [{ value: "original", defaultSelected: true, selected: true },
            { value: "second", defaultSelected: false, selected: false }];
    }
    const select = new Select();
    select.editor = true;
    select.value = culture;
    select.disabled = true;
    const returnUrl = new Element();
    returnUrl.value = "/old";
    const apply = new Element();
    apply.disabled = true;
    const cancel = new Element();
    let focused = 0;
    cancel.focus = () => {
        if (failures.focus) throw failures.focus;
        focused++;
    };
    const proceed = new Element();
    const dialog = new Element();
    dialog.open = false;
    dialog.showModal = () => {
        if (failures.showModal) throw failures.showModal;
        dialog.open = true;
    };
    dialog.close = () => {
        if (failures.close) throw failures.close;
        dialog.open = false;
    };
    if (unsupported) dialog[unsupported] = undefined;
    const outsideError = { hidden: true, textContent: "Localized browser failure", closest: () => null };
    const insideError = { hidden: true, textContent: "Localized browser failure", closest: () => dialog };
    const unavailable = { hidden: true, textContent: "Localized browser unavailable" };
    class Form extends Element {
        elements = { namedItem: name => name === "Culture" ? select : returnUrl };
        querySelector() { return apply; }
        submit() {
            if (failures.submitBefore) throw failures.submitBefore;
            submissions.push({ culture: select.disabled ? undefined : select.value, returnUrl: returnUrl.value });
            if (failures.submitAfter) throw failures.submitAfter;
        }
    }
    const form = new Form();
    const selector = new Element();
    selector.dataset.currentCulture = culture;
    selector.querySelector = name => ({
        "[data-language-form]": form, dialog, "[data-language-cancel]": cancel,
        "[data-language-continue]": proceed, "[data-language-unavailable]": unavailable
    })[name];
    selector.querySelectorAll = () => [outsideError, insideError];
    select.parentSelector = selector;
    const document = new Target();
    document.body = new Element();
    document.querySelectorAll = name => name === "[data-language-selector]" ? [selector] : [input, select];
    document.querySelector = () => document.explicitDirty ? {} : null;
    let observer;
    vm.runInNewContext(source, {
        document, Element, HTMLSelectElement: Select, HTMLFormElement: Form,
        location: { pathname: "/operator/kql", search: "?filter=original%20value", hash: "#results" },
        console: { error: (...arguments_) => logs.push(arguments_) },
        setTimeout: callback => timers.push(callback),
        MutationObserver: class {
            constructor(callback) { observer = callback; }
            observe() {}
        }
    }, { filename: "ui-language.js" });
    return { failures, submissions, logs, input, select, form, selector, dialog, apply, cancel, proceed,
        outsideError, insideError, unavailable, document, culture,
        focused: () => focused,
        choose: () => { select.value = culture === "en-US" ? "de-DE" : "en-US"; },
        dirty() { input.value = "PRIVATE unsaved input"; document.fire("input", { target: input }); },
        observe: records => observer(records), flush: () => { for (const callback of timers.splice(0)) callback(); } };
}

for (const culture of ["en-US", "de-DE"]) {
    test(`${culture}: clean native POST preserves culture field and exact path/query/fragment, once`, () => {
        const item = fixture({ culture });
        item.choose();
        assert.equal(item.form.fire("submit").defaultPrevented, true);
        assert.deepEqual(item.submissions, [{
            culture: culture === "en-US" ? "de-DE" : "en-US",
            returnUrl: "/operator/kql?filter=original%20value#results"
        }]);
        assert.equal(item.select.disabled, false);
        assert.equal(item.apply.disabled, true);
        assert.equal(item.proceed.disabled, true);
        item.form.fire("submit");
        item.proceed.fire("click");
        assert.equal(item.submissions.length, 1);
        assert.deepEqual(item.logs, []);
    });

    for (const action of ["cancel", "Escape", "nativeCancel", "continue"]) {
        test(`${culture}: dirty warning ${action} preserves edits and submits only after confirmation`, () => {
            const item = fixture({ culture });
            item.dirty();
            item.choose();
            item.form.fire("submit");
            assert.equal(item.dialog.open, true);
            assert.equal(item.focused(), 1);
            assert.equal(item.submissions.length, 0);
            if (action === "cancel") item.cancel.fire("click");
            else if (action === "Escape") assert.equal(item.dialog.fire("keydown", { key: "Escape" }).defaultPrevented, true);
            else if (action === "nativeCancel") item.dialog.fire("cancel");
            else item.proceed.fire("click");
            assert.equal(item.input.value, "PRIVATE unsaved input");
            if (action === "continue") assert.equal(item.submissions.length, 1);
            else {
                assert.equal(item.select.value, culture);
                assert.equal(item.submissions.length, 0);
                item.choose();
                item.form.fire("submit");
                assert.equal(item.dialog.open, true);
            }
            assert.deepEqual(item.logs, []);
        });
    }
}

for (const unsupported of ["showModal", "close"]) {
    test(`missing ${unsupported}: selector remains disabled with a visible localized notice`, () => {
        const item = fixture({ unsupported });
        assert.equal(item.select.disabled, true);
        assert.equal(item.apply.disabled, true);
        assert.equal(item.unavailable.hidden, false);
        assert.equal(item.selector.dataset.languageReady, undefined);
        assert.equal(item.form.listeners.size, 0);
        assert.deepEqual(item.submissions, []);
    });
}

for (const phase of ["showModal", "focus", "close", "submitBefore", "submitAfter"]) {
    test(`${phase} failure: visible safe error, retained input, original diagnostic and explicit retry only`, () => {
        const item = fixture();
        item.dirty();
        item.choose();
        const failure = new Error("PRIVATE_BROWSER_DETAIL");
        item.failures[phase] = failure;
        item.form.fire("submit");
        if (["close", "submitBefore", "submitAfter"].includes(phase)) item.proceed.fire("click");
        assert.equal(item.input.value, "PRIVATE unsaved input");
        assert.equal(item.submissions.length, phase === "submitAfter" ? 1 : 0);
        assert.equal(item.logs.length, 1);
        assert.equal(item.logs[0][1], failure);
        assert.equal(item.outsideError.hidden, item.dialog.open);
        assert.equal(item.insideError.hidden, !item.dialog.open);
        assert.equal(item.outsideError.textContent, "Localized browser failure");
        assert.equal(item.insideError.textContent, "Localized browser failure");
        assert.equal(item.apply.disabled, false);
        delete item.failures[phase];
        const before = item.submissions.length;
        item.form.fire("submit");
        item.proceed.fire("click");
        assert.equal(item.submissions.length, before + 1);
        assert.equal(item.outsideError.hidden, true);
        assert.equal(item.insideError.hidden, true);
    });
}

for (const kind of ["text", "checkbox", "radio", "multiple"]) {
    test(`${kind}: reverting to the original value removes the dirty warning`, () => {
        const item = fixture({ kind });
        if (kind === "text") item.input.value = "edited";
        else if (kind === "multiple") item.input.options[1].selected = true;
        else item.input.checked = true;
        item.document.fire("change", { target: item.input });
        item.choose();
        item.form.fire("submit");
        assert.equal(item.dialog.open, true);
        item.cancel.fire("click");
        if (kind === "text") item.input.value = "original";
        else if (kind === "multiple") item.input.options[1].selected = false;
        else item.input.checked = false;
        item.document.fire("change", { target: item.input });
        item.choose();
        item.form.fire("submit");
        assert.equal(item.submissions.length, 1);
        assert.equal(item.dialog.open, false);
    });
}

test("initial autofill and explicit page-dirty marker each require confirmation", () => {
    for (const autofill of [false, true]) {
        const item = fixture({ initialValue: autofill ? "autofilled" : "original" });
        item.document.explicitDirty = !autofill;
        item.choose();
        item.form.fire("submit");
        assert.equal(item.dialog.open, true);
        assert.equal(item.submissions.length, 0);
    }
});

test("enhancedload and mutations do not duplicate handlers or forget existing edits", () => {
    const item = fixture();
    item.dirty();
    item.document.fire("enhancedload");
    item.observe([{ addedNodes: [new Element()] }]);
    assert.equal(item.form.listeners.get("submit").size, 1);
    assert.equal(item.proceed.listeners.get("click").size, 1);
    item.choose();
    item.form.fire("submit");
    assert.equal(item.dialog.open, true);
    item.proceed.fire("click");
    assert.equal(item.submissions.length, 1);
});

for (const prevented of [false, true]) {
    test(`explicit reset (${prevented ? "prevented" : "confirmed"}) is the only submit-independent edit reset`, () => {
        const item = fixture();
        item.dirty();
        item.document.fire("submit", { target: { querySelectorAll: () => [item.input] } });
        item.choose();
        item.form.fire("submit");
        assert.equal(item.dialog.open, true);
        item.cancel.fire("click");
        item.document.fire("reset", { target: { querySelectorAll: () => [item.input] }, defaultPrevented: prevented });
        item.flush();
        item.choose();
        item.form.fire("submit");
        assert.equal(item.dialog.open, prevented);
        assert.equal(item.submissions.length, prevented ? 0 : 1);
    });
}
