import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const languageSource = await readFile(new URL("../MonitoringApp/wwwroot/js/kql-language.js", import.meta.url), "utf8");
const adapterSource = await readFile(new URL("../MonitoringApp/wwwroot/js/kql-completion.js", import.meta.url), "utf8");
const actualDictionary = JSON.parse(await readFile(new URL("../MonitoringApp/wwwroot/js/kql-completion.json", import.meta.url), "utf8"));
const hintMessages = {};
for (const culture of ["en", "de"]) {
    const xml = await readFile(new URL(`../MonitoringApp/Resources/UiResources${culture === "de" ? ".de-DE" : ""}.resx`, import.meta.url), "utf8");
    hintMessages[culture] = Object.fromEntries([...xml.matchAll(/<data name="KqlEditor\.Hints\.([^"]+)"[^>]*><value>(.*?)<\/value><\/data>/g)]
        .map(([, key, value]) => [["Error", "Warning", "Info"].includes(key) ? key.toLowerCase() : key, value]));
}
const dictionary = [
    { word: "where", contexts: ["operator"], info: { en: "Filter rows", de: "Zeilen filtern" } },
    { word: "take", contexts: ["operator"] }, { word: "order", contexts: ["operator"], next: ["by"] },
    ...["project", "project-away", "project-rename", "extend", "summarize", "distinct", "limit", "sort"]
        .map(word => ({ word, contexts: ["operator"] })),
    "count", "max", "tostring", "strcat",
    { word: "let", contexts: ["statement"] }, { word: "materialize", contexts: ["source", "expression"] }
];

class Element extends EventTarget {
    children = [];
    dataset = {};
    style = {};
    attributes = new Map();
    className = "";
    hidden = false;
    isConnected = true;
    parentElement = null;
    clientWidth = 500;
    clientHeight = 120;
    clientLeft = 1;
    clientTop = 1;
    offsetLeft = 0;
    offsetTop = 0;
    scrollLeft = 0;
    scrollTop = 0;
    classList = {
        contains: name => this.className.split(/\s+/).includes(name),
        add: name => { this.className = [...new Set([...this.className.split(/\s+/), name])].join(" ").trim(); },
        remove: name => { this.className = this.className.split(/\s+/).filter(item => item !== name).join(" "); }
    };
    get textContent() { return this.children.map(child => child.textContent).join(""); }
    set textContent(value) { this.children = [new TextNode(value)]; }
    append(...children) {
        for (const child of children) {
            if (child.fragment) this.append(...child.children);
            else { child.parentElement = this; this.children.push(child); }
        }
    }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) { this.attributes.delete(name); }
    getBoundingClientRect() { return { left: 0, top: 0, right: 500, bottom: 24, width: 500, height: 24 }; }
    getClientRects() { return [this.getBoundingClientRect()]; }
    contains(node) { return this === node || this.children.some(child => child instanceof Element && child.contains(node)); }
    closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) ?? null; }
    matches(selector) {
        if (selector === ":disabled") return !!this.disabled || !!this.fieldsetDisabled;
        if (selector.startsWith(".")) return this.classList.contains(selector.slice(1).split("[")[0]) &&
            (!selector.includes("[") || this.matches(selector.slice(selector.indexOf("["))));
        const names = [...selector.matchAll(/\[data-([\w-]+)\]/g)].map(match =>
            match[1].replace(/-([a-z])/g, (_, letter) => letter.toUpperCase()));
        return names.length > 0 && names.every(name => this.dataset[name] !== undefined);
    }
    querySelectorAll(selector) {
        return this.children.flatMap(child => child instanceof Element ?
            [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)] : []);
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
    fire(name, properties = {}) {
        const event = Object.assign(new Event(name, { cancelable: true }), properties);
        this.dispatchEvent(event);
        return event;
    }
}

class TextNode {
    constructor(text) { this.textContent = text; }
    get length() { return this.textContent.length; }
}

async function fixture({ count = 1, failure, language = "en-US", missingLanguage = false, dictionaryEntries = dictionary } = {}) {
    const observers = [];
    const logs = [];
    const requests = [];
    const document = new Element();
    document.documentElement = { lang: language };
    document.currentScript = { src: "https://example.test/js/kql-completion.js" };
    document.body = new Element();
    document.createElement = () => {
        const element = new Element();
        element.focus = () => { document.activeElement = element; element.fire("focus"); };
        return element;
    };
    document.createTextNode = text => new TextNode(text);
    document.createDocumentFragment = () => Object.assign(new Element(), { fragment: true });
    document.createTreeWalker = root => {
        const textNodes = element => element.children.flatMap(child =>
            child instanceof TextNode ? [child] : textNodes(child));
        const nodes = textNodes(root);
        return { nextNode: () => nodes.shift() ?? null };
    };
    document.createRange = () => ({ setStart() {}, setEnd() {}, getBoundingClientRect: () => ({ bottom: 24 }) });
    const roots = Array.from({ length: count }, () => {
        const root = new Element();
        root.dataset = { kqlTables: '["SecurityEvent"]', kqlCatalogVersion: "1",
            kqlSchemaWorkspace: "workspace-a", kqlWorkspaceCount: "1",
            kqlSchemaLoading: "Loading", kqlSchemaFailed: "Schema failed",
            kqlSchemaTitle: "{0} columns ({1})", kqlSchemaEmpty: "No columns returned",
            kqlSchemaUnavailable: "Table not discovered in this workspace",
            kqlDiagnosticMessages: JSON.stringify(hintMessages[language.startsWith("de") ? "de" : "en"]),
            kqlKeywordNoInfo: language.startsWith("de") ? "Keine Hilfe hinterlegt" : "No help stored",
            kqlKeywordSyntax: language.startsWith("de") ? "Syntax / Vorlage" : "Syntax / template",
            kqlKeywordDetails: "Details", kqlKeywordExample: language.startsWith("de") ? "Beispiel" : "Example" };
        const input = new Element();
        Object.assign(input, { value: "", selectionStart: 0, selectionEnd: 0, maxLength: -1 });
        input.setAttribute("aria-describedby", "existing-help");
        input.focus = () => { document.activeElement = input; };
        input.setSelectionRange = (start, end) => { input.selectionStart = start; input.selectionEnd = end; };
        const overlay = new Element();
        const tooltip = new Element();
        tooltip.hidden = true;
        const error = { hidden: true };
        const schemaError = { hidden: true };
        const hints = new Element();
        hints.hidden = true;
        const hintError = { hidden: true };
        const keywordBrowser = new Element();
        const keywordToggle = document.createElement("button");
        keywordToggle.disabled = true;
        keywordToggle.setAttribute("aria-expanded", "false");
        const keywordPanel = new Element();
        keywordPanel.hidden = true;
        const keywordList = new Element();
        const keywordHelp = new Element();
        keywordHelp.textContent = "Hover or focus a keyword";
        keywordPanel.append(keywordList, keywordHelp);
        keywordBrowser.append(keywordToggle, keywordPanel);
        root.append(keywordBrowser);
        root.querySelector = selector => ({
            textarea: input, ".free-kql-completion": overlay, "[data-kql-info-tooltip]": tooltip,
            "[data-kql-completion-error]": error, "[data-kql-schema-error]": schemaError,
            "[data-kql-hints]": hints, "[data-kql-diagnostics-error]": hintError,
            "[data-kql-keywords]": keywordBrowser, "[data-kql-keyword-toggle]": keywordToggle,
            "[data-kql-keyword-panel]": keywordPanel, "[data-kql-keyword-list]": keywordList,
            "[data-kql-keyword-help]": keywordHelp
        })[selector];
        const changes = [];
        input.addEventListener("change", () => changes.push(input.value));
        return { root, input, overlay, tooltip, error, schemaError, hints, hintError, changes,
            keywordBrowser, keywordToggle, keywordPanel, keywordList, keywordHelp };
    });
    document.execCommand = (command, showUi, text) => {
        assert.equal(command, "insertText");
        assert.equal(showUi, false);
        if (failure === "editing-throws") throw new Error("NATIVE_EDIT_FAILURE");
        if (failure === "editing-rejected") return false;
        const input = document.activeElement;
        const start = input.selectionStart;
        if (failure !== "editing-no-change") {
            input.value = input.value.slice(0, start) + text + input.value.slice(input.selectionEnd);
            input.selectionStart = input.selectionEnd = start + text.length;
            input.fire("input");
        }
        return true;
    };
    document.querySelectorAll = selector => selector === "[data-kql-editor]" ?
        roots.filter(item => item.root.isConnected).map(item => item.root) : [];
    const window = { innerHeight: 900 };
    const context = vm.createContext({
        document, window, URL, Event, AbortController, Node: Element, Element, NodeFilter: { SHOW_TEXT: 4 },
        console: { error: (...args) => logs.push(args) },
        fetch: async url => {
            requests.push(String(url));
            if (failure === "fetch") throw new Error("PRIVATE_FETCH_FAILURE");
            return { ok: failure !== "http", status: 503,
                json: async () => failure === "dictionary" ? [] : dictionaryEntries };
        },
        MutationObserver: class {
            kind = "mutation";
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe(target) { this.target = target; }
            disconnect() { this.disconnected = true; }
        },
        ResizeObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe(target) { this.target = target; }
            disconnect() { this.disconnected = true; }
        }
    });
    if (!missingLanguage) vm.runInContext(languageSource, context, { filename: "kql-language.js" });
    vm.runInContext(adapterSource, context, { filename: "kql-completion.js" });
    const settle = () => new Promise(resolve => setImmediate(resolve));
    await settle();
    function type(item, value, cursor = value.length) {
        document.activeElement = item.input;
        item.input.value = value;
        item.input.selectionStart = item.input.selectionEnd = cursor;
        item.input.fire("input");
    }
    function mutate(item, attributeName) {
        observers.find(observer => observer.kind === "mutation" && observer.target === item.input)?.callback([{ attributeName }]);
    }
    return { ...roots[0], roots, document, window, observers, requests, logs, settle, type, mutate,
        ghost: item => (item ?? roots[0]).overlay.querySelector(".free-kql-completion-ghost") };
}

test("keyword reference lists every actual JSON word, including nested next entries, once and alphabetically", async () => {
    const f = await fixture({ dictionaryEntries: actualDictionary });
    const words = [];
    const collect = entries => {
        for (const entry of entries) {
            words.push(typeof entry === "string" ? entry : entry.word);
            if (entry.next) collect(entry.next);
        }
    };
    collect(actualDictionary);
    const expected = [...new Set(words)].sort((left, right) => left.localeCompare(right));
    assert.deepEqual(f.keywordList.children.map(button => button.textContent), expected);
    assert.equal(f.keywordToggle.disabled, false);
    assert.equal(f.keywordPanel.hidden, true);
    f.keywordToggle.fire("click");
    assert.equal(f.keywordPanel.hidden, false);
    assert.equal(f.keywordToggle.getAttribute("aria-expanded"), "true");
    f.type(f, "SecurityEvent | wh");
    assert.deepEqual(f.keywordList.children.map(button => button.textContent), expected);
    assert.equal(f.input.value, "SecurityEvent | wh");
    assert.deepEqual(f.changes, []);
});

for (const language of ["en-US", "de-DE", "de-AT", "en-GB"]) {
    test(`keyword reference shows localized hover, focus and touch help without editing: ${language}`, async () => {
        const f = await fixture({ language });
        f.type(f, "SecurityEvent | wh");
        const selection = [f.input.selectionStart, f.input.selectionEnd];
        f.keywordToggle.fire("click");
        const where = f.keywordList.children.find(button => button.textContent === "where");
        const take = f.keywordList.children.find(button => button.textContent === "take");
        where.fire("mouseenter");
        assert.equal(f.keywordHelp.textContent, `where\n${language.startsWith("de") ? "Zeilen filtern" : "Filter rows"}`);
        take.focus();
        assert.equal(f.keywordHelp.textContent, `take\n${f.root.dataset.kqlKeywordNoInfo}`);
        where.fire("click");
        assert.ok(f.keywordHelp.textContent.includes(language.startsWith("de") ? "Zeilen filtern" : "Filter rows"));
        assert.equal(f.input.value, "SecurityEvent | wh");
        assert.deepEqual([f.input.selectionStart, f.input.selectionEnd], selection);
        assert.deepEqual(f.changes, []);
    });
}

test("reference includes nested-only words and prefers stored help when a word is repeated", async () => {
    const f = await fixture({ dictionaryEntries: [
        { word: "order", next: [{ word: "by", info: "Nested grouping help" }, "asc"] }, "by",
        { word: "where", info: "English help" }
    ] });
    assert.deepEqual(f.keywordList.children.map(button => button.textContent), ["asc", "by", "order", "where"]);
    f.keywordList.children[1].fire("mouseenter");
    assert.equal(f.keywordHelp.textContent, "by\nNested grouping help");
    f.keywordList.children[3].focus();
    assert.equal(f.keywordHelp.textContent, "where\nEnglish help");
});

test("reference keyboard navigation uses a single tab stop and Escape restores the toggle", async () => {
    const f = await fixture();
    assert.equal(f.keywordToggle.fire("keydown", { key: "ArrowDown" }).defaultPrevented, true);
    assert.equal(f.keywordPanel.hidden, false);
    assert.equal(f.document.activeElement, f.keywordList.children[0]);
    const first = f.keywordList.children[0];
    assert.equal(first.fire("keydown", { key: "ArrowDown" }).defaultPrevented, true);
    assert.equal(f.document.activeElement, f.keywordList.children[1]);
    f.keywordList.children[1].fire("keydown", { key: "End" });
    assert.equal(f.document.activeElement, f.keywordList.children.at(-1));
    f.keywordList.children.at(-1).fire("keydown", { key: "Home" });
    assert.equal(f.document.activeElement, first);
    assert.equal(f.keywordList.children.filter(button => button.tabIndex === 0).length, 1);
    assert.equal(first.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.equal(f.keywordBrowser.fire("keydown", { key: "Escape" }).defaultPrevented, true);
    assert.equal(f.keywordPanel.hidden, true);
    assert.equal(f.keywordToggle.getAttribute("aria-expanded"), "false");
    assert.equal(f.document.activeElement, f.keywordToggle);
    f.keywordToggle.fire("click");
    f.document.fire("click");
    assert.equal(f.keywordPanel.hidden, true);
});

test("reference treats JSON help as text, not markup", async () => {
    const help = "<img src=x onerror=alert(1)>\nExample: where Value < 3 && Value > 1";
    const f = await fixture({ dictionaryEntries: [{ word: "where", info: help }] });
    f.keywordList.children[0].fire("mouseenter");
    assert.equal(f.keywordHelp.textContent, `where\n${help}`);
    assert.ok(f.keywordHelp.children.every(child => child instanceof TextNode));
});

for (const failure of ["fetch", "http", "dictionary", "language"]) {
    test(`keyword reference remains disabled after ${failure} failure with visible existing error`, async () => {
        const f = await fixture({ failure, missingLanguage: failure === "language" });
        assert.equal(f.keywordToggle.disabled, true);
        f.keywordToggle.fire("click");
        assert.equal(f.keywordPanel.hidden, true);
        assert.equal(f.keywordList.children.length, 0);
        assert.equal(f.error.hidden, false);
        assert.ok(f.logs.length > 0);
    });
}

test("reference obeys disabled state but allows read-only lookup", async () => {
    const f = await fixture();
    f.input.readOnly = true;
    f.mutate(f, "readonly");
    assert.equal(f.keywordToggle.disabled, false);
    f.keywordToggle.fire("click");
    f.input.disabled = true;
    f.mutate(f, "disabled");
    assert.equal(f.keywordToggle.disabled, true);
    assert.equal(f.keywordPanel.hidden, true);
    f.input.disabled = false;
    f.mutate(f, "disabled");
    assert.equal(f.keywordToggle.disabled, false);
});

test("reference uses one dictionary request, independent panels and disposal-safe handlers", async () => {
    const f = await fixture({ count: 2 });
    assert.equal(f.requests.length, 1);
    const [first, second] = f.roots;
    first.keywordToggle.fire("click");
    assert.equal(first.keywordPanel.hidden, false);
    assert.equal(second.keywordPanel.hidden, true);
    const oldButton = first.keywordList.children[0];
    first.root.isConnected = false;
    f.document.fire("enhancedload");
    assert.equal(first.keywordList.children.length, 0);
    assert.equal(first.keywordPanel.hidden, true);
    assert.equal(first.keywordToggle.disabled, true);
    oldButton.fire("mouseenter");
    assert.equal(first.keywordHelp.textContent, "Hover or focus a keyword");
    second.keywordToggle.fire("click");
    second.keywordList.children.find(button => button.textContent === "where").focus();
    assert.equal(second.keywordHelp.textContent, "where\nFilter rows");
    assert.equal(second.keywordPanel.hidden, false);
});

for (const language of ["en-US", "de-DE"]) {
    test(`AP6 localized structure hints preserve text, completion and input validity: ${language}`, async () => {
        const f = await fixture({ language });
        f.type(f, "SecurityEvent )");
        const messages = hintMessages[language.startsWith("de") ? "de" : "en"];
        assert.ok(f.hints.textContent.includes(messages.UnexpectedCloser));
        assert.ok(f.hints.textContent.includes(messages.Limited));
        assert.equal(f.hintError.hidden, true);
        assert.equal(f.input.value, "SecurityEvent )");
        assert.equal(f.input.getAttribute("aria-invalid"), null);
        assert.deepEqual(f.changes, []);
        assert.equal(f.input.fire("keydown", { key: "Enter" }).defaultPrevented, false);
        f.type(f, "SecurityEvent | wh");
        assert.equal(f.ghost().textContent, "ere");
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
        assert.equal(f.input.value, "SecurityEvent | where");
    });
}

test("AP6 unknown column hints wait for schema and word completion, then use safe text and exact ranges", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    const name = "<img src=x onerror=alert(1)>";
    const query = `SecurityEvent | project ['${name}']`;
    f.type(f, query);
    await f.settle();
    f.document.activeElement = null;
    f.input.fire("blur");
    const button = f.hints.querySelector("[data-kql-hint-start]");
    assert.ok(button.textContent.includes(name));
    assert.ok(button.children.every(child => child instanceof TextNode));
    assert.equal(query.slice(Number(button.dataset.kqlHintStart), Number(button.dataset.kqlHintEnd)), `['${name}']`);
    const click = new Event("click");
    Object.defineProperty(click, "target", { value: button });
    f.hints.dispatchEvent(click);
    assert.equal(f.document.activeElement, f.input);
    assert.equal(f.input.selectionStart, Number(button.dataset.kqlHintStart));
    assert.equal(f.input.selectionEnd, Number(button.dataset.kqlHintEnd));
    assert.equal(f.input.value, query);
    assert.deepEqual(f.changes, []);
});

for (const status of ["Loading", "Failed", "Unavailable", "Empty"]) {
    test(`AP6 ${status} metadata never becomes an unknown-column warning`, async () => {
        const f = await fixture();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => ({ ...response(), status, columns: [] }) });
        f.type(f, "SecurityEvent | project Missing ");
        await f.settle();
        assert.equal(f.hints.querySelector("[data-kql-hint-start]"), null);
        const expected = status === "Loading" ? "SchemaPending" : status === "Failed" ? "SchemaFailed" : "SchemaLimited";
        assert.ok(f.hints.textContent.includes(hintMessages.en[expected]));
        assert.equal(f.hintError.hidden, true);
    });
}

test("AP6 schema changes and multi-workspace selection remove obsolete column warnings", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "SecurityEvent | project Missing ");
    await f.settle();
    assert.ok(f.hints.querySelector("[data-kql-hint-start]"));
    f.root.dataset.kqlWorkspaceCount = "2";
    f.mutate(f, "data-kql-workspace-count");
    assert.equal(f.hints.querySelector("[data-kql-hint-start]"), null);
    assert.ok(f.hints.textContent.includes(hintMessages.en.SchemaMultiple));
    f.root.dataset.kqlCatalogVersion = "2";
    f.mutate(f, "data-kql-catalog-version");
    assert.equal(f.hints.querySelector("[data-kql-hint-start]"), null);
});

test("AP6 IME, disabled and readonly states preserve hints without creating edit events", async () => {
    const f = await fixture();
    f.type(f, "SecurityEvent )");
    f.input.fire("compositionstart");
    assert.equal(f.hints.hidden, true);
    f.input.fire("compositionend");
    assert.equal(f.hints.hidden, false);
    f.input.readOnly = true;
    f.mutate(f, "readonly");
    assert.ok(f.hints.textContent.includes(hintMessages.en.UnexpectedCloser));
    f.input.disabled = true;
    f.mutate(f, "disabled");
    assert.equal(f.hints.querySelector("[data-kql-hint-start]").disabled, true);
    assert.equal(f.input.value, "SecurityEvent )");
    assert.deepEqual(f.changes, []);
});

test("AP6 malformed diagnostics metadata produces only a localized failure and leaves completion working", async () => {
    const f = await fixture();
    f.root.dataset.kqlDiagnosticMessages = "{PRIVATE_HINT_DETAILS";
    f.type(f, "SecurityEvent | wh");
    assert.equal(f.hints.hidden, true);
    assert.equal(f.hintError.hidden, false);
    assert.ok(f.logs.some(entry => String(entry[0]).includes("local KQL editor hints")));
    assert.equal(f.ghost().textContent, "ere");
    f.root.dataset.kqlDiagnosticMessages = JSON.stringify(hintMessages.en);
    f.input.fire("input");
    assert.equal(f.hintError.hidden, true);
    assert.equal(f.hints.hidden, false);
});

test("AP6 multiple editors have independent accessible hints and disposal removes their listeners and descriptions", async () => {
    const f = await fixture({ count: 2 });
    f.type(f.roots[0], "SecurityEvent )");
    f.type(f.roots[1], "SecurityEvent (");
    assert.notEqual(f.roots[0].hints.id, f.roots[1].hints.id);
    assert.ok(f.roots[0].hints.textContent.includes(hintMessages.en.UnexpectedCloser));
    assert.ok(f.roots[1].hints.textContent.includes(hintMessages.en.OpenDelimiter));
    f.root.isConnected = false;
    f.observers.find(observer => observer.kind === "mutation" && observer.target === f.document.body)
        .callback([{ addedNodes: [], removedNodes: [f.root] }]);
    assert.equal(f.hints.hidden, true);
    assert.equal(f.hints.textContent, "");
    assert.equal(f.input.getAttribute("aria-describedby"), "existing-help");
    f.input.fire("input");
    assert.equal(f.hints.textContent, "");
});

test("Tab accepts the visible suffix and immediately notifies the existing onchange binding", async () => {
    const f = await fixture();
    f.type(f, "SecurityEvent | wh");
    let inputEvents = 0;
    f.input.addEventListener("input", () => inputEvents++);
    assert.equal(f.ghost().textContent, "ere");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, "SecurityEvent | where");
    assert.deepEqual(f.changes, ["SecurityEvent | where"]);
    assert.equal(inputEvents, 1);
    assert.equal(f.ghost(), null);
});

for (const failure of ["editing-throws", "editing-rejected", "editing-no-change", "editing-unavailable"]) {
    test(`native edit failure is visible, keeps normal Tab and never claims a successful change: ${failure}`, async () => {
        const f = await fixture({ failure });
        if (failure === "editing-unavailable") delete f.document.execCommand;
        f.type(f, "SecurityEvent | wh");
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
        assert.equal(f.input.value, "SecurityEvent | wh");
        assert.equal(f.input.selectionStart, f.input.value.length);
        assert.equal(f.input.selectionEnd, f.input.value.length);
        assert.equal(f.error.hidden, false);
        assert.ok(f.logs.length > 0);
        assert.deepEqual(f.changes, []);
        assert.equal(f.ghost(), null);
    });
}

test("native completion recovers after a visible rejected edit", async () => {
    const f = await fixture();
    const edit = f.document.execCommand;
    f.document.execCommand = () => false;
    f.type(f, "T | wh");
    f.input.fire("keydown", { key: "Tab" });
    assert.equal(f.error.hidden, false);
    f.document.execCommand = edit;
    f.type(f, "T | wh");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, "T | where");
    assert.equal(f.error.hidden, true);
    assert.deepEqual(f.changes, ["T | where"]);
});

for (const properties of [
    { key: "Enter" }, { key: "Tab", shiftKey: true }, { key: "Tab", ctrlKey: true },
    { key: "Tab", altKey: true }, { key: "Tab", metaKey: true }
]) {
    test(`normal key behavior is not hijacked: ${JSON.stringify(properties)}`, async () => {
        const f = await fixture();
        f.type(f, "T | wh");
        assert.equal(f.input.fire("keydown", properties).defaultPrevented, false);
        assert.equal(f.input.value, "T | wh");
        assert.deepEqual(f.changes, []);
    });
}

test("Escape dismisses completion, and focus/IME transitions preserve normal text editing", async () => {
    const f = await fixture();
    f.type(f, "T | wh");
    assert.equal(f.input.fire("keydown", { key: "Escape" }).defaultPrevented, true);
    assert.equal(f.ghost(), null);
    f.input.fire("input");
    assert.equal(f.ghost().textContent, "ere");
    f.input.fire("compositionstart");
    assert.equal(f.ghost(), null);
    assert.equal(f.input.fire("keydown", { key: "Tab", isComposing: true }).defaultPrevented, false);
    f.input.fire("compositionend");
    assert.equal(f.ghost().textContent, "ere");
    f.document.activeElement = null;
    f.input.fire("blur");
    assert.equal(f.ghost(), null);
    assert.equal(f.input.value, "T | wh");
});

test("cursor movement, selection and stale suggestions cannot overwrite query text", async () => {
    const f = await fixture();
    f.type(f, "T | wh existing", 6);
    assert.equal(f.ghost(), null);
    f.type(f, "T | wh");
    f.input.selectionStart = 0;
    f.document.fire("selectionchange");
    assert.equal(f.ghost(), null);
    f.type(f, "T | wh");
    f.input.value = "T | ta";
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.equal(f.input.value, "T | ta");
    f.type(f, "T | wh");
    f.input.selectionStart = 0;
    f.input.selectionEnd = 2;
    f.document.fire("selectionchange");
    assert.equal(f.ghost(), null);
});

test("disabled, inherited disabled, read-only and maxlength constraints remain enforced", async () => {
    const f = await fixture();
    for (const flag of ["disabled", "fieldsetDisabled", "readOnly"]) {
        f.input[flag] = true;
        f.type(f, "T | wh");
        assert.equal(f.ghost(), null);
        f.input[flag] = false;
    }
    f.input.maxLength = 8;
    f.type(f, "T | wh");
    assert.equal(f.ghost(), null);
    f.input.maxLength = 9;
    f.type(f, "T | wh");
    assert.equal(f.ghost().textContent, "ere");
});

for (const [language, help] of [["en-US", "Filter rows"], ["de-DE", "Zeilen filtern"]]) {
    test(`F1 and Escape preserve localized accessible help: ${language}`, async () => {
        const f = await fixture({ language });
        f.type(f, "where");
        assert.equal(f.input.fire("keydown", { key: "F1" }).defaultPrevented, true);
        assert.equal(f.tooltip.hidden, false);
        assert.equal(f.tooltip.textContent, help);
        assert.ok(f.input.getAttribute("aria-describedby").includes(f.tooltip.id));
        f.input.fire("keydown", { key: "Escape" });
        assert.equal(f.tooltip.hidden, true);
        assert.equal(f.input.getAttribute("aria-describedby"), `existing-help ${f.hints.id}`);
    });
}

for (const failure of ["fetch", "http", "dictionary", "language"]) {
    test(`initialization failure is visible without modifying text: ${failure}`, async () => {
        const f = await fixture({ failure, missingLanguage: failure === "language" });
        assert.equal(f.error.hidden, false);
        assert.equal(f.logs.length, 1);
        assert.equal(f.input.value, "");
        assert.equal(f.root.classList.contains("free-kql-highlighted"), false);
    });
}

test("workspace catalog changes and server-loaded drafts replace stale completion", async () => {
    const f = await fixture();
    f.type(f, "SecurityE");
    assert.equal(f.ghost().textContent, "vent");
    f.root.dataset.kqlTables = '["Perf"]';
    f.root.dataset.kqlCatalogVersion = "2";
    f.mutate(f, "data-kql-tables");
    assert.equal(f.ghost(), null);
    f.input.value = "T | ta";
    f.input.selectionStart = f.input.selectionEnd = 6;
    f.mutate(f, "data-kql-version");
    assert.equal(f.ghost().textContent, "ke");
});

test("independent editors and disposal do not leak suggestions or leave active handlers", async () => {
    const f = await fixture({ count: 2 });
    f.type(f.roots[0], "T | wh");
    f.type(f.roots[1], "T | ta");
    assert.equal(f.ghost(f.roots[1]).textContent, "ke");
    assert.equal(f.roots[0].input.value, "T | wh");
    assert.notEqual(f.roots[0].tooltip.id, f.roots[1].tooltip.id);
    f.root.isConnected = false;
    f.observers.find(observer => observer.target === f.document.body)
        .callback([{ addedNodes: [], removedNodes: [f.root] }]);
    assert.equal(f.overlay.children.length, 0);
    assert.equal(f.observers.filter(observer => observer.disconnected).length, 2);
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.equal(f.input.value, "T | wh");
});

test("schema failures are explicit and a late schema response cannot enter a newer catalog", async () => {
    const f = await fixture();
    let resolve;
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: () => {
        calls++;
        return new Promise(done => { resolve = done; });
    } });
    f.type(f, "SecurityEvent");
    await f.settle();
    assert.equal(calls, 1);
    f.root.dataset.kqlCatalogVersion = "2";
    f.root.dataset.kqlTables = '["Perf"]';
    f.mutate(f, "data-kql-catalog-version");
    resolve({ workspaceKey: "workspace-a", tableName: "SecurityEvent", catalogVersion: 1,
        status: "Ready", columns: [{ name: "PRIVATE_OLD_SCHEMA", type: "string" }] });
    await f.settle();
    assert.ok(!f.overlay.textContent.includes("PRIVATE_OLD_SCHEMA"));
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => { throw new Error("PRIVATE_SCHEMA_FAILURE"); } });
    f.type(f, "Perf");
    await f.settle();
    assert.equal(f.schemaError.hidden, false);
    assert.equal(f.input.value, "Perf");
    assert.equal(f.logs.length, 1);
});

test("multiline preview is clipped and source strings/comments never become keyword markup", async () => {
    const f = await fixture();
    f.type(f, "T | wh\n| take 10", 6);
    assert.equal(f.ghost().textContent, "ere");
    assert.equal(f.overlay.querySelector(".free-kql-preview").style.maxHeight, "24px");
    f.type(f, "'<where>' // where");
    assert.equal(f.overlay.querySelectorAll(".free-kql-keyword").length, 0);
    assert.equal(f.overlay.textContent, "'<where>' // where\n");
    assert.equal(f.requests.length, 1);
});

const response = (name = "SecurityEvent", version = 1, workspace = "workspace-a") => ({
    workspaceKey: workspace, tableName: name, catalogVersion: version,
    status: "Ready", columns: [{ name: "Computer", type: "string" }, { name: "Details", type: "dynamic" }]
});

test("structured schema produces the existing tooltip from names/types, not localized transport text", async () => {
    const f = await fixture();
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async (method, name, version) => {
        calls++;
        assert.equal(method, "GetTableSchemaAsync");
        return response(name, version);
    } });
    f.type(f, "SecurityEvent");
    await f.settle();
    f.input.fire("keydown", { key: "F1" });
    assert.equal(f.tooltip.textContent, "SecurityEvent columns (workspace-a)\nComputer: string\nDetails: dynamic");
    f.type(f, "SecurityEvent | wh");
    await f.settle();
    assert.equal(calls, 1);
    assert.equal(f.ghost().textContent, "ere");
    assert.equal(f.schemaError.hidden, true);
});

for (const [status, details] of [
    ["Empty", "No columns returned"], ["Unavailable", "Table not discovered in this workspace"], ["Failed", "Schema failed"]
]) {
    test(`tooltip preserves the distinct structured ${status} state`, async () => {
        const f = await fixture();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => ({ ...response(), status, columns: [] }) });
        f.type(f, "SecurityEvent");
        await f.settle();
        f.input.fire("keydown", { key: "F1" });
        assert.equal(f.tooltip.textContent, `SecurityEvent columns (workspace-a)\n${details}`);
        assert.equal(f.schemaError.hidden, status !== "Failed");
        assert.equal(f.logs.length, 0);
    });
}

for (const invalid of ["localized tooltip text", { ...response(), workspaceKey: "other" },
    { ...response(), catalogVersion: 0 }, { ...response(), tableName: "Other" }]) {
    test(`malformed or foreign structured response is explicitly rejected: ${JSON.stringify(invalid)}`, async () => {
        const f = await fixture();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => invalid });
        f.type(f, "SecurityEvent");
        await f.settle();
        assert.equal(f.schemaError.hidden, false);
        assert.equal(f.logs.length, 1);
        f.input.fire("keydown", { key: "F1" });
        assert.equal(f.tooltip.textContent, "SecurityEvent columns (workspace-a)\nSchema failed");
    });
}

test("workspace changes replace the source cache and discard a late result for the previous source", async () => {
    const f = await fixture();
    let finish;
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: (_, name, version) => {
        calls++;
        if (version === 1) return new Promise(resolve => { finish = resolve; });
        return Promise.resolve(response(name, version, "workspace-b"));
    } });
    f.type(f, "SecurityEvent");
    await f.settle();
    f.root.dataset.kqlSchemaWorkspace = "workspace-b";
    f.root.dataset.kqlCatalogVersion = "2";
    f.mutate(f, "data-kql-schema-workspace");
    await f.settle();
    finish(response());
    await f.settle();
    f.input.fire("keydown", { key: "F1" });
    assert.equal(calls, 2);
    assert.ok(f.tooltip.textContent.includes("(workspace-b)"));
    assert.ok(!f.tooltip.textContent.includes("(workspace-a)"));
    assert.equal(f.schemaError.hidden, true);
});

test("refresh retries failed schemas while a case-only workspace change does not issue duplicate requests", async () => {
    const f = await fixture();
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async (_, name, version) => {
        calls++;
        return version === 1 ? { ...response(name, version), status: "Failed", columns: [] } : response(name, version);
    } });
    f.type(f, "SecurityEvent");
    await f.settle();
    assert.equal(f.schemaError.hidden, false);
    f.root.dataset.kqlCatalogVersion = "2";
    f.mutate(f, "data-kql-catalog-version");
    await f.settle();
    assert.equal(f.schemaError.hidden, true);
    assert.equal(calls, 2);
    f.root.dataset.kqlSchemaWorkspace = "WORKSPACE-A";
    f.mutate(f, "data-kql-schema-workspace");
    await f.settle();
    assert.equal(calls, 2);
});

test("different editor instances keep same-name table schemas in separate workspaces", async () => {
    const f = await fixture({ count: 2 });
    f.roots[1].root.dataset.kqlSchemaWorkspace = "workspace-b";
    f.mutate(f.roots[1], "data-kql-schema-workspace");
    for (const [index, item] of f.roots.entries()) {
        const workspace = index === 0 ? "workspace-a" : "workspace-b";
        f.window.kqlCompletion.connect(item.root, { invokeMethodAsync: async (_, name, version) => response(name, version, workspace) });
        f.type(item, "SecurityEvent");
    }
    await f.settle();
    assert.ok(f.roots[0].overlay.querySelector("[data-kql-info]").dataset.kqlInfo.includes("(workspace-a)"));
    assert.ok(f.roots[1].overlay.querySelector("[data-kql-info]").dataset.kqlInfo.includes("(workspace-b)"));
});

test("AP3 asynchronous schema arrival immediately offers a column and Tab notifies Blazor", async () => {
    const f = await fixture();
    let finish;
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: () => {
        calls++;
        return new Promise(resolve => { finish = resolve; });
    } });
    f.type(f, "SecurityEvent | where Comp");
    await f.settle();
    assert.equal(f.ghost(), null);
    f.input.fire("input");
    await f.settle();
    assert.equal(calls, 1);
    finish(response());
    await f.settle();
    assert.equal(f.ghost().textContent, "uter");
    assert.equal(f.ghost().dataset.kqlInfo, "Computer: string");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, "SecurityEvent | where Computer");
    assert.deepEqual(f.changes, ["SecurityEvent | where Computer"]);
    assert.equal(calls, 1);
});

for (const attribute of ["kqlWorkspaceCount", "kqlSchemaWorkspace", "kqlCatalogVersion"]) {
    test(`AP3 revalidates column provenance on Tab even before mutation delivery: ${attribute}`, async () => {
        const f = await fixture();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
        f.type(f, "SecurityEvent | where Comp");
        await f.settle();
        assert.equal(f.ghost().textContent, "uter");
        f.root.dataset[attribute] = attribute === "kqlSchemaWorkspace" ? "other" : "2";
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
        assert.equal(f.input.value, "SecurityEvent | where Comp");
        assert.deepEqual(f.changes, []);
        f.mutate(f, `data-${attribute.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`);
        assert.equal(f.ghost(), null);
    });
}

for (const prefix of ["Event", "['Event"]) {
    test(`AP3 quotes an unsafe column name without appending malformed KQL: ${prefix}`, async () => {
        const f = await fixture();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => ({
            ...response(), columns: [{ name: "Event Source", type: "string" }]
        }) });
        f.type(f, `SecurityEvent | where ${prefix}`);
        await f.settle();
        assert.equal(f.ghost().textContent, "['Event Source']");
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
        assert.equal(f.input.value, "SecurityEvent | where ['Event Source']");
        assert.deepEqual(f.changes, ["SecurityEvent | where ['Event Source']"]);
    });
}

test("AP3 quoted table insertion loads its actual name and supports its direct schema", async () => {
    const f = await fixture();
    f.root.dataset.kqlTables = '["Web Server"]';
    f.mutate(f, "data-kql-tables");
    const names = [];
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async (_, name, version) => {
        names.push(name);
        return response(name, version);
    } });
    f.type(f, "Web");
    assert.equal(f.ghost().textContent, "['Web Server']");
    f.input.fire("keydown", { key: "Tab" });
    await f.settle();
    assert.equal(f.input.value, "['Web Server']");
    assert.deepEqual(names, ["Web Server"]);
    assert.equal(f.overlay.querySelector(".free-kql-table").dataset.kqlInfo,
        "Web Server columns (workspace-a)\nComputer: string\nDetails: dynamic");
    f.type(f, "['Web Server'] | where Comp");
    assert.equal(f.ghost().textContent, "uter");
});

test("AP3 editor-specific source schemas never supply another editor's columns", async () => {
    const f = await fixture({ count: 2 });
    for (const [index, item] of f.roots.entries()) {
        const workspace = index ? "workspace-b" : "workspace-a";
        item.root.dataset.kqlSchemaWorkspace = workspace;
        f.mutate(item, "data-kql-schema-workspace");
        f.window.kqlCompletion.connect(item.root, { invokeMethodAsync: async (_, name, version) => ({
            ...response(name, version, workspace),
            columns: [{ name: index ? "CounterValue" : "Computer", type: "string" }]
        }) });
        f.type(item, "SecurityEvent | where Comp");
        await f.settle();
    }
    assert.equal(f.ghost(f.roots[1]), null);
    f.type(f.roots[0], "SecurityEvent | where Comp");
    assert.equal(f.ghost().textContent, "uter");
});

test("AP3 workspace-count mutation hides the column ghost but does not stop keyword completion", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "SecurityEvent | where Comp");
    await f.settle();
    assert.equal(f.ghost().textContent, "uter");
    f.root.dataset.kqlWorkspaceCount = "2";
    f.mutate(f, "data-kql-workspace-count");
    assert.equal(f.ghost(), null);
    f.type(f, "SecurityEvent | wh");
    assert.equal(f.ghost().textContent, "ere");
    f.root.dataset.kqlWorkspaceCount = "1";
    f.mutate(f, "data-kql-workspace-count");
    f.type(f, "SecurityEvent | where Comp");
    assert.equal(f.ghost().textContent, "uter");
});

test("AP4 editing projection/renaming/aggregation recomputes suggestions without additional metadata queries", async () => {
    const f = await fixture();
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => {
        calls++;
        return response();
    } });
    for (const [query, suffix] of [
        ["SecurityEvent | project Computer | where Comp", "uter"],
        ["SecurityEvent | project Details | where Comp", null],
        ["SecurityEvent | project-rename Device = Computer | where Dev", "ice"],
        ["SecurityEvent | project-rename Device = Computer | where Comp", null],
        ["SecurityEvent | summarize Anzahl = count() by Computer | where Anz", "ahl"],
        ["SecurityEvent | summarize Anzahl = count() by Computer | where Det", null],
        ["SecurityEvent | where true | take 10 | limit 5 | project Comp", "uter"],
        ["SecurityEvent | extend Device = Computer, Copied = Dev", "ice"],
        ["SecurityEvent | project Device = Computer, Copied = Dev", null]
    ]) {
        f.type(f, query);
        await f.settle();
        assert.equal(f.ghost()?.textContent ?? null, suffix, query);
    }
    assert.equal(calls, 1);
    assert.equal(f.schemaError.hidden, true);
    assert.deepEqual(f.changes, []);
});

test("AP4 sequential extend aliases can be accepted via Tab and then used downstream", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "SecurityEvent | extend Device = Computer, Copied = Dev");
    await f.settle();
    assert.equal(f.ghost().dataset.kqlInfo, "Device: string");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, "SecurityEvent | extend Device = Computer, Copied = Device");
    f.type(f, `${f.input.value} | project Copied | where Cop`);
    assert.equal(f.ghost().textContent, "ied");
    assert.equal(f.ghost().dataset.kqlInfo, "Copied: string");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.changes.at(-1), "SecurityEvent | extend Device = Computer, Copied = Device | project Copied | where Copied");
});

test("AP4 unknown derived types do not appear as fabricated types in ghost help", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "SecurityEvent | summarize Largest = max(Details) by Computer | where Lar");
    await f.settle();
    assert.equal(f.ghost().textContent, "gest");
    assert.equal(f.ghost().dataset.kqlInfo, "Largest");
    assert.equal(f.input.fire("keydown", { key: "F1" }).defaultPrevented, true);
    assert.equal(f.tooltip.textContent, "Largest");
});

test("AP4 a changed source or stale async reply cannot restore derived columns", async () => {
    const f = await fixture();
    let finish;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: () => new Promise(resolve => { finish = resolve; }) });
    f.type(f, "SecurityEvent | project Device = Computer | where Dev");
    await f.settle();
    assert.equal(f.ghost(), null);
    const old = finish;
    f.root.dataset.kqlCatalogVersion = "2";
    f.root.dataset.kqlSchemaWorkspace = "workspace-b";
    f.mutate(f, "data-kql-schema-workspace");
    await f.settle();
    old(response());
    await f.settle();
    assert.equal(f.ghost(), null);
    finish({ ...response("SecurityEvent", 2, "workspace-b"), status: "Unavailable", columns: [] });
    await f.settle();
    assert.equal(f.ghost(), null);
    assert.equal(f.input.value, "SecurityEvent | project Device = Computer | where Dev");
});

test("AP4 accepting a derived name rechecks the edited pipeline instead of using an old ghost", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "SecurityEvent | extend Device = Computer | where Dev");
    await f.settle();
    assert.equal(f.ghost().textContent, "ice");
    f.input.value = "SecurityEvent | project Computer | where Dev";
    f.input.selectionStart = f.input.selectionEnd = f.input.value.length;
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.equal(f.input.value, "SecurityEvent | project Computer | where Dev");
    assert.deepEqual(f.changes, []);
});

test("AP5 a materialized binding receives late metadata and Tab notifies the unchanged Blazor binding", async () => {
    const f = await fixture();
    let finish;
    let calls = 0;
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: () => {
        calls++;
        return new Promise(resolve => { finish = resolve; });
    } });
    const definition = "let Events = materialize(SecurityEvent | project Computer);";
    f.type(f, `${definition} Events | where Comp`);
    await f.settle();
    assert.equal(f.ghost(), null);
    finish(response());
    await f.settle();
    assert.equal(f.ghost().textContent, "uter");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.deepEqual(f.changes, [`${definition} Events | where Computer`]);
    f.type(f, `${definition} Even`);
    assert.equal(f.ghost().textContent, "ts");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, `${definition} Events`);
    await f.settle();
    assert.equal(calls, 1);
});

test("AP5 variable/scalar suggestions remain editor-local and honor their distinct positions", async () => {
    const f = await fixture({ count: 2 });
    for (const item of f.roots)
        f.window.kqlCompletion.connect(item.root, { invokeMethodAsync: async () => response() });
    f.type(f, "let Events = SecurityEvent; let Threshold = 5; SecurityEvent | take Thres");
    await f.settle();
    assert.equal(f.ghost().textContent, "hold");
    assert.equal(f.ghost().dataset.kqlInfo, "Threshold: long");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    f.type(f, "let Threshold = 5; Thres");
    assert.equal(f.ghost(), null);
    f.type(f.roots[1], "Even");
    assert.equal(f.ghost(f.roots[1]), null);
    f.type(f.roots[1], "SecurityEvent | take Thres");
    assert.equal(f.ghost(f.roots[1]), null);
});

test("AP5 source changes cannot resurrect a materialized schema using a stale reply", async () => {
    const f = await fixture();
    const pending = [];
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: () => new Promise(resolve => pending.push(resolve)) });
    f.type(f, "let Events = materialize(SecurityEvent | project Computer); Events | where Comp");
    await f.settle();
    f.root.dataset.kqlSchemaWorkspace = "workspace-b";
    f.root.dataset.kqlCatalogVersion = "2";
    f.mutate(f, "data-kql-schema-workspace");
    await f.settle();
    pending[0](response());
    await f.settle();
    assert.equal(f.ghost(), null);
    pending[1]({ ...response("SecurityEvent", 2, "workspace-b"), columns: [{ name: "CounterValue", type: "real" }] });
    await f.settle();
    assert.equal(f.ghost(), null);
    f.type(f, "let Events = materialize(SecurityEvent | project CounterValue); Events | where CounterV");
    assert.equal(f.ghost().textContent, "alue");
});

test("AP5 changing a let definition invalidates an old ghost before Tab acceptance", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "let Events = SecurityEvent | project Computer; Events | where Comp");
    await f.settle();
    assert.equal(f.ghost().textContent, "uter");
    f.input.value = "let Events = SecurityEvent | project Details; Events | where Comp";
    f.input.selectionStart = f.input.selectionEnd = f.input.value.length;
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.deepEqual(f.changes, []);
});

test("AP5 quoted tabular and scalar binding names are safely accepted", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "let ['Recent Events'] = SecurityEvent | project Computer; Rece");
    await f.settle();
    assert.equal(f.ghost().textContent, "['Recent Events']");
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
    assert.equal(f.input.value, "let ['Recent Events'] = SecurityEvent | project Computer; ['Recent Events']");
    f.type(f, `${f.input.value} | where Comp`);
    assert.equal(f.ghost().textContent, "uter");
    f.type(f, "let ['Upper Bound'] = 5; SecurityEvent | take Upp");
    assert.equal(f.ghost().textContent, "['Upper Bound']");
    f.input.fire("keydown", { key: "Tab" });
    assert.equal(f.input.value, "let ['Upper Bound'] = 5; SecurityEvent | take ['Upper Bound']");
});

test("AP5 multi-workspace mode retains variable names and scalars without pretending to know materialized columns", async () => {
    const f = await fixture();
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async () => response() });
    f.type(f, "let Events = materialize(SecurityEvent); Events | where Comp");
    await f.settle();
    assert.equal(f.ghost().textContent, "uter");
    f.root.dataset.kqlWorkspaceCount = "2";
    f.mutate(f, "data-kql-workspace-count");
    assert.equal(f.ghost(), null);
    f.type(f, "let Events = materialize(SecurityEvent); Even");
    assert.equal(f.ghost().textContent, "ts");
    f.type(f, "let Threshold = 5; SecurityEvent | take Thres");
    assert.equal(f.ghost().textContent, "hold");
});

const ap8Response = (name, version = 1, workspace = "workspace-a") => ({
    ...response(name, version, workspace), columns: [
        { name: "Computer", type: "string" },
        { name: name === "SecurityEvent" ? "EventId" : "RemoteOnly", type: "long" }
    ]
});

for (const query of [
    "SecurityEvent | join (OtherEvents) on Computer | project Rem",
    "union SecurityEvent, OtherEvents | project Rem",
    "let R = materialize(OtherEvents); SecurityEvent | join (R) on Computer | project Rem"
]) {
    test(`AP8 loads both schemas once and accepts only after both arrive: ${query}`, async () => {
        const f = await fixture({ dictionaryEntries: actualDictionary });
        f.root.dataset.kqlTables = '["SecurityEvent","OtherEvents"]';
        f.mutate(f, "data-kql-tables");
        const pending = new Map();
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: (method, name, version) => {
            assert.equal(method, "GetTableSchemaAsync");
            assert.equal(version, 1);
            assert.ok(!pending.has(name), "no duplicate in-flight request");
            return new Promise(resolve => pending.set(name, resolve));
        } });
        f.type(f, query);
        await f.settle();
        assert.deepEqual([...pending.keys()].sort(), ["OtherEvents", "SecurityEvent"]);
        assert.equal(f.ghost(), null);
        pending.get("SecurityEvent")(ap8Response("SecurityEvent"));
        await f.settle();
        assert.equal(f.ghost(), null);
        pending.get("OtherEvents")(ap8Response("OtherEvents"));
        await f.settle();
        assert.equal(f.ghost().textContent, "oteOnly");
        assert.equal(f.ghost().dataset.kqlInfo, "RemoteOnly: long");
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, true);
        assert.equal(f.input.value, query.slice(0, -3) + "RemoteOnly");
        assert.deepEqual(f.changes, [query.slice(0, -3) + "RemoteOnly"]);
        assert.equal(pending.size, 2);
        assert.deepEqual(f.logs, []);
    });
}

for (const operator of ["join (OtherEvents) on Computer", "union OtherEvents"]) {
    test(`AP8 changing workspace discards both late source responses: ${operator}`, async () => {
        const f = await fixture({ dictionaryEntries: actualDictionary });
        f.root.dataset.kqlTables = '["SecurityEvent","OtherEvents"]';
        f.mutate(f, "data-kql-tables");
        const pending = [];
        f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: (_method, name, version) =>
            new Promise(resolve => pending.push({ name, version, resolve })) });
        const query = `SecurityEvent | ${operator} | project Rem`;
        f.type(f, query);
        await f.settle();
        assert.equal(pending.length, 2);
        f.root.dataset.kqlSchemaWorkspace = "workspace-b";
        f.root.dataset.kqlCatalogVersion = "2";
        f.mutate(f, "data-kql-schema-workspace");
        await f.settle();
        assert.equal(pending.length, 4);
        for (const item of pending.slice(0, 2)) item.resolve(ap8Response(item.name));
        await f.settle();
        assert.equal(f.ghost(), null);
        for (const item of pending.slice(2))
            item.resolve({ ...ap8Response(item.name, 2, "workspace-b"), columns: [{ name: "Computer", type: "string" }] });
        await f.settle();
        assert.equal(f.ghost(), null);
        assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
        assert.deepEqual(f.changes, []);
    });
}

for (const language of ["en-US", "de-DE"]) {
    test(`AP8 join/union reference help comes from the actual localized JSON: ${language}`, async () => {
        const f = await fixture({ dictionaryEntries: actualDictionary, language });
        f.type(f, "SecurityEvent | join (OtherEvents) on Computer");
        for (const word of ["join", "union"]) {
            f.keywordList.children.find(button => button.textContent === word).fire("mouseenter");
            const entry = actualDictionary.find(entry => entry.word === word);
            assert.ok(f.keywordHelp.textContent.startsWith(`${word}\n${entry.info[language.slice(0, 2)]}`));
            assert.ok(f.keywordHelp.textContent.includes(entry.syntax));
            assert.ok(f.keywordHelp.textContent.includes(entry.example));
        }
        assert.deepEqual(f.changes, []);
        assert.equal(f.requests.length, 1);
        assert.match(f.requests[0], /kql-completion\.json\?v=20261112-3$/);
    });
}

test("AP8 join suggestions revalidate both schemas at Tab when workspace count changes", async () => {
    const f = await fixture({ dictionaryEntries: actualDictionary });
    f.root.dataset.kqlTables = '["SecurityEvent","OtherEvents"]';
    f.mutate(f, "data-kql-tables");
    f.window.kqlCompletion.connect(f.root, { invokeMethodAsync: async (_method, name, version) => ap8Response(name, version) });
    f.type(f, "SecurityEvent | join OtherEvents on Computer | project Rem");
    await f.settle();
    assert.equal(f.ghost().textContent, "oteOnly");
    f.root.dataset.kqlWorkspaceCount = "2";
    assert.equal(f.input.fire("keydown", { key: "Tab" }).defaultPrevented, false);
    assert.deepEqual(f.changes, []);
    f.mutate(f, "data-kql-workspace-count");
    assert.equal(f.ghost(), null);
    assert.ok(f.hints.textContent.includes(hintMessages.en.SchemaMultiple));
    f.type(f, "union SecurityEvent, OtherE");
    assert.equal(f.ghost().textContent, "vents");
});

for (const language of ["en-US", "de-DE", "de-AT", "en-GB"]) {
    test(`structured JSON help separates compact hover from detailed reference: ${language}`, async () => {
        const entry = { word: "where", contexts: ["operator"],
            info: { en: "Filter rows.", de: "Zeilen filtern." },
            syntax: "<table> | where <condition>",
            details: { en: "Only matching rows remain.", de: "Nur passende Zeilen bleiben." },
            example: "datatable(Value:long)[1, 2, 3]\n| where Value > 1\n| project Value" };
        const f = await fixture({ language, dictionaryEntries: [entry] });
        const localized = language.startsWith("de") ? "de" : "en";
        const query = "SecurityEvent | wh";
        f.type(f, query);
        assert.equal(f.ghost().dataset.kqlInfo, `${entry.info[localized]}\n${entry.syntax}`);
        assert.ok(!f.ghost().dataset.kqlInfo.includes(entry.example));
        assert.ok(!f.ghost().dataset.kqlInfo.includes(entry.details[localized]));
        f.keywordToggle.fire("click");
        f.keywordList.children[0].fire("mouseenter");
        const sections = f.keywordHelp.children.filter(child => child instanceof Element);
        assert.deepEqual(sections.map(section => section.children[0].textContent),
            [f.root.dataset.kqlKeywordSyntax, "Details", f.root.dataset.kqlKeywordExample]);
        assert.deepEqual(sections.map(section => section.children[1].textContent),
            [entry.syntax, entry.details[localized], entry.example]);
        assert.equal(f.input.value, query);
        assert.deepEqual(f.changes, []);
        f.type(f, "SecurityEvent | where ");
        const word = f.overlay.querySelector(".free-kql-keyword");
        assert.equal(word.dataset.kqlInfo, `${entry.info[localized]}\n${entry.syntax}`);
    });
}

test("structured help localizes each field independently with exact, base and English fallback", async () => {
    const f = await fixture({ language: "de-AT", dictionaryEntries: [{
        word: "where", info: { en: "English", de: "Deutsch", "de-AT": "Exakt" },
        syntax: { en: "English template", de: "Deutsche Vorlage" },
        details: { en: "English detail fallback" }, example: { fr: "Not available" }
    }] });
    f.keywordList.children[0].focus();
    assert.ok(f.keywordHelp.textContent.startsWith("where\nExakt"));
    assert.ok(f.keywordHelp.textContent.includes("Deutsche Vorlage"));
    assert.ok(f.keywordHelp.textContent.includes("English detail fallback"));
    assert.ok(!f.keywordHelp.textContent.includes("Not available"));
    assert.equal(f.keywordHelp.children.filter(child => child instanceof Element).length, 2);
});

test("structured reference fields remain literal text and safely replace a previous long example", async () => {
    const unsafe = "<img src=x onerror=alert(1)>\n<script>invalid()</script>";
    const f = await fixture({ dictionaryEntries: [
        { word: "where", info: "Summary", syntax: unsafe, details: unsafe, example: unsafe },
        { word: "take", info: "Legacy function help" }
    ] });
    f.keywordList.children.find(button => button.textContent === "where").fire("click");
    const sections = f.keywordHelp.children.filter(child => child instanceof Element);
    assert.equal(sections.length, 3);
    assert.ok(sections.every(section => section.children[1].children.every(child => child instanceof TextNode)));
    assert.ok(sections.every(section => section.children[1].textContent === unsafe));
    f.keywordList.children.find(button => button.textContent === "take").focus();
    assert.equal(f.keywordHelp.textContent, "take\nLegacy function help");
    assert.ok(f.keywordHelp.children.every(child => child instanceof TextNode));
});

test("template-only structured help does not claim that no help is stored", async () => {
    const f = await fixture({ dictionaryEntries: [{ word: "where", contexts: ["operator"],
        syntax: "<table> | where <condition>", example: "T\n| where Value > 0" }] });
    f.keywordList.children[0].focus();
    assert.ok(!f.keywordHelp.textContent.includes(f.root.dataset.kqlKeywordNoInfo));
    assert.equal(f.keywordHelp.children.filter(child => child instanceof Element).length, 2);
    f.type(f, "SecurityEvent | wh");
    assert.equal(f.ghost().dataset.kqlInfo, "<table> | where <condition>");
});
