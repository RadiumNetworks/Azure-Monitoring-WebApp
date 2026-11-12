import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { performance } from "node:perf_hooks";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../MonitoringApp/wwwroot/js/kql-language.js", import.meta.url), "utf8");
const context = vm.createContext({});
vm.runInContext(source, context, { filename: "kql-language.js" });
const language = context.kqlLanguage;
const plain = value => JSON.parse(JSON.stringify(value));
const entries = language.normalizeEntries([
    ...["where", "take", "summarize"].map(word => ({ word, contexts: ["operator"] })),
    { word: "order", contexts: ["operator"], next: [{ word: "by", next: ["asc", "desc"] }] }
]);
const snapshot = (value, extra = {}) => ({
    value, selectionStart: value.length, selectionEnd: value.length,
    disabled: false, readOnly: false, maxLength: -1, ...extra
});
const realEntries = language.normalizeEntries(JSON.parse(await readFile(
    new URL("../MonitoringApp/wwwroot/js/kql-completion.json", import.meta.url), "utf8")));

test("language analysis runs without DOM, fetch or a workspace and exposes an immutable API", () => {
    assert.equal(context.document, undefined);
    assert.equal(context.fetch, undefined);
    assert.equal(Object.isFrozen(language), true);
    assert.equal(language.completion(snapshot("T | wh"), entries).suffix, "ere");
});

test("the published JSON dictionary still validates, including localized help and next lists", async () => {
    const dictionary = JSON.parse(await readFile(new URL("../MonitoringApp/wwwroot/js/kql-completion.json", import.meta.url), "utf8"));
    const normalized = language.normalizeEntries(dictionary);
    assert.ok(normalized.length > 10);
    assert.ok(normalized.some(entry => entry.next));
    assert.ok(normalized.some(entry => entry.info));
});

for (const invalid of [[], null, ["Where"], ["where", "where"], [{ word: "where", next: [] }],
    [{ word: "where", info: "" }], [{ word: "where", info: {} }], [{ word: "where", info: { de: " " } }]]) {
    test(`invalid dictionaries fail explicitly: ${JSON.stringify(invalid)}`, () => {
        assert.throws(() => language.normalizeEntries(invalid), /KQL|dictionary|info/);
    });
}

for (const [value, suffix] of [
    ["T | wh", "ere"], ["SecurityEvent | wh", "ere"], ["let x = 1; T | wh", "ere"],
    ["T | order ", "by"], ["SecurityEvent | order b", "y"], ["SecurityEvent | order by d", "esc"],
    ["T | order /* ; | */ b", "y"], ["T | order // ; |\n b", "y"]
]) {
    test(`contextual keyword completion: ${JSON.stringify(value)}`, () => {
        assert.equal(language.completion(snapshot(value), entries)?.suffix, suffix);
    });
}

for (const value of ["", "where", "unknown", "s", "42", "'wh", '"wh', '@"wh', "```wh", "// wh", "/* wh"]) {
    test(`no unsupported, ambiguous, exact or non-code suggestion: ${JSON.stringify(value)}`, () => {
        assert.equal(language.completion(snapshot(value), entries,
            [{ word: "search" }, { word: "summarize" }]), null);
    });
}

test("table entries retain casing and no longer leak into operator or expression positions", () => {
    const tables = [{ word: "SecurityEvent" }, { word: "where" }];
    assert.equal(language.completion(snapshot("SecurityE"), entries, tables).suffix, "vent");
    assert.equal(language.completion(snapshot("securitye"), entries, tables), null);
    assert.equal(language.completion(snapshot("wh"), entries, tables).entry.word, "where");
    assert.equal(language.completion(snapshot("T | order S"), entries, tables), null);
    assert.equal(language.completion(snapshot("T | SecurityE"), entries, tables), null);
});

for (const extra of [
    { disabled: true }, { readOnly: true }, { selectionStart: 1 }, { maxLength: 8 }
]) {
    test(`input constraints suppress completion: ${JSON.stringify(extra)}`, () => {
        assert.equal(language.completion(snapshot("T | wh", extra), entries), null);
    });
}

test("completion preserves length boundaries and never replaces subsequent text on the same line", () => {
    assert.equal(language.completion(snapshot("T | wh", { maxLength: 9 }), entries).suffix, "ere");
    assert.equal(language.completion(snapshot("T | wh existing", { selectionStart: 6, selectionEnd: 6 }), entries), null);
    assert.equal(language.completion(snapshot("T | wh\nexisting", { selectionStart: 6, selectionEnd: 6 }), entries).suffix, "ere");
    assert.equal(language.completion(snapshot("T | wh   ", { selectionStart: 6, selectionEnd: 6 }), entries).suffix, "ere");
});

for (const literal of ["'a;|()'", '"a;|()"', '@"a"";|()"', "'a\\';|()'", "```a;\n|()```"]) {
    test(`literal content does not become query boundaries: ${literal}`, () => {
        const query = `let x = ${literal}; SecurityEvent | wh`;
        const parsed = language.analyze(query);
        assert.equal(parsed.isCode, true);
        assert.equal(parsed.statementStart, query.indexOf("; SecurityEvent") + 1);
        assert.equal(parsed.pipelineStart, query.lastIndexOf("|") + 1);
        assert.deepEqual(plain(parsed.groups), []);
        const token = parsed.tokens.find(token => token.kind === "literal");
        assert.equal(query.slice(token.start, token.end), literal.startsWith("@") ? literal.slice(1) : literal);
        assert.equal(language.completion(snapshot(query), entries).suffix, "ere");
    });
}

test("comment ranges and incomplete lexical states are available without adding comments to keyword tokens", () => {
    const query = "SecurityEvent /* ; | ) */\n// ( ; |\n| wh";
    const parsed = language.analyze(query);
    assert.equal(parsed.pipelineStart, query.lastIndexOf("|") + 1);
    assert.deepEqual(plain(parsed.mismatchedClosers), []);
    assert.equal(parsed.trivia.length, 2);
    assert.equal(query.slice(parsed.trivia[0].start, parsed.trivia[0].end), "/* ; | ) */");
    assert.equal(parsed.tokens.filter(token => token.kind === "word").length, 2);
    for (const [text, state] of [["// x", "line"], ["/* x", "block"], ["'x\\", "string"], ["```x", "multiline"]]) {
        const result = language.analyze(text);
        assert.equal(result.isCode, false);
        assert.equal(result.state, state);
    }
});

test("cursor context is nested and does not inherit pipes from a finished materialize expression", () => {
    const definition = "let E = materialize(SecurityEvent | where Computer == 'a;|b')";
    const inside = language.analyze(definition, definition.indexOf("where") + "where".length);
    assert.equal(inside.groups.length, 1);
    assert.equal(inside.groups[0].opener, "(");
    assert.equal(inside.pipelineStart, definition.indexOf("|") + 1);
    const after = language.analyze(definition);
    assert.equal(after.pipelineStart, 0);
    assert.equal(after.statementStart, 0);
    assert.deepEqual(plain(after.groups), []);
    const query = `${definition}; E | wh`;
    assert.equal(language.analyze(query).statementStart, query.indexOf("; E") + 1);
    assert.equal(language.analyze(query).pipelineStart, query.lastIndexOf("|") + 1);
});

test("protected identifiers and dynamic brackets keep literal punctuation out of the parent scope", () => {
    for (const query of ["['A;|()']", 'bag["A;|()"]', "dynamic({'key': [';|)']})"]) {
        const parsed = language.analyze(query);
        assert.equal(parsed.statementStart, 0);
        assert.equal(parsed.pipelineStart, 0);
        assert.deepEqual(plain(parsed.groups), []);
        assert.deepEqual(plain(parsed.mismatchedClosers), []);
    }
    const open = language.analyze("SecurityEvent | where (Computer == ");
    assert.equal(open.groups[0].opener, "(");
    assert.equal(open.isCode, true);
});

test("mismatched closing brackets are recorded without corrupting outstanding groups", () => {
    const parsed = language.analyze("materialize(SecurityEvent ] | where ");
    assert.equal(parsed.groups.length, 1);
    assert.equal(parsed.mismatchedClosers[0].text, "]");
    assert.equal(parsed.pipelineStart, "materialize(SecurityEvent ] |".length);
});

test("cursor suffix does not influence analysis and invalid cursor positions fail explicitly", () => {
    const text = "SecurityEvent | wh 'unterminated";
    const cursor = text.indexOf(" '");
    assert.equal(language.analyze(text, cursor).isCode, true);
    assert.equal(language.analyze(text).isCode, false);
    for (const cursor of [-1, text.length + 1, 0.5, NaN]) {
        assert.throws(() => language.analyze(text, cursor), /cursor/);
    }
});

test("analysis retains original UTF-16 offsets and does not mutate dictionary or editor snapshots", () => {
    const value = "'😀'; Tabelle_ä | wh";
    const word = language.tokenize(value).tokens.find(token => token.text === "Tabelle_ä");
    assert.equal(word.start, value.indexOf("Tabelle_ä"));
    const input = Object.freeze(snapshot(value));
    const before = JSON.stringify(entries);
    assert.equal(language.completion(input, entries).suffix, "ere");
    assert.equal(JSON.stringify(entries), before);
});

test("record a reproducible 64 KiB local analysis baseline", t => {
    const query = "let E = materialize(SecurityEvent | where Computer == 'a;|b');\n".repeat(1200).slice(0, 65536);
    for (let count = 0; count < 5; count++) language.analyze(query);
    const times = [];
    for (let count = 0; count < 30; count++) {
        const start = performance.now();
        const result = language.analyze(query);
        times.push(performance.now() - start);
        assert.equal(result.cursor, 65536);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; 64 KiB ASCII; 30 iterations; p95=${times[28].toFixed(2)} ms`);
});

const schemaContext = {
    workspaceKey: "workspace-a", tableName: "Perf", catalogVersion: 7, workspaceCount: 1
};
const readySchema = {
    workspaceKey: "workspace-a", tableName: "Perf", catalogVersion: 7,
    status: "Ready", columns: [{ name: "Computer", type: "string" }, { name: "Details", type: "dynamic" }]
};

test("structured schemas preserve source, names and KQL types without tooltip parsing", () => {
    const result = language.normalizeTableSchema(readySchema, schemaContext);
    assert.deepEqual(plain(result), readySchema);
    assert.notEqual(result.columns, readySchema.columns);
    assert.deepEqual(plain(language.columnSchema(result, schemaContext)), {
        status: "Ready", columns: readySchema.columns
    });
    assert.equal(language.normalizeTableSchema({ ...readySchema, workspaceKey: "WORKSPACE-A" }, schemaContext).status, "Ready");
});

for (const status of ["Empty", "Unavailable", "Failed", "Loading"]) {
    test(`structured state is explicit, never a successful empty schema: ${status}`, () => {
        const schema = { ...readySchema, status, columns: [] };
        assert.deepEqual(plain(language.columnSchema(schema, schemaContext)), { status, columns: [] });
    });
}

for (const change of [
    { workspaceKey: "workspace-b" }, { tableName: "Other" }, { catalogVersion: 6 },
    { catalogVersion: "7" }, { status: "Success" }, { columns: null },
    { columns: [] }, { status: "Empty" },
    { columns: [{ name: "Computer", type: "" }] },
    { columns: [{ name: "", type: "int" }] },
    { columns: [{ name: "Computer", type: "int" }, { name: "Computer", type: "string" }] }
]) {
    test(`invalid or incorrectly attributed schema is rejected: ${JSON.stringify(change)}`, () => {
        assert.throws(() => language.normalizeTableSchema({ ...readySchema, ...change }, schemaContext), /KQL schema response/);
    });
}

test("single-first policy never offers first-workspace columns as a multi-workspace schema", () => {
    assert.deepEqual(plain(language.columnSchema(readySchema, { ...schemaContext, workspaceCount: 2 })),
        { status: "MultipleWorkspaces", columns: [] });
    assert.deepEqual(plain(language.columnSchema(readySchema, { ...schemaContext, workspaceCount: 0 })),
        { status: "NoWorkspace", columns: [] });
    assert.deepEqual(plain(language.columnSchema(null, schemaContext)), { status: "Unknown", columns: [] });
    for (const change of [{ workspaceKey: "other" }, { tableName: "Other" }, { catalogVersion: 8 }]) {
        assert.deepEqual(plain(language.columnSchema(readySchema, { ...schemaContext, ...change })),
            { status: "Stale", columns: [] });
    }
});

const tables = [{ word: "SecurityEvent" }, { word: "Perf" }, { word: "Web Server" }];
const catalog = {
    workspaceKey: "workspace-a", workspaceCount: 1, catalogVersion: 7,
    schemas: [{ ...readySchema, tableName: "SecurityEvent", columns: [
        ...readySchema.columns, { name: "Event Source", type: "string" },
        { name: "where", type: "long" }, { name: "O'Brien", type: "string" },
        { name: "Folder\\Name", type: "string" }
    ] }]
};
const suggest = (text, metadata = catalog, names = tables) =>
    language.completion(snapshot(text), realEntries, names, metadata);
const accepted = (text, result) => text.slice(0, result.replaceStart) + result.suffix;

for (const [text, expected] of [
    ["SecurityE", "SecurityEvent"], ["let n = ';|'; SecurityE", "let n = ';|'; SecurityEvent"],
    ["le", "let"], ["let n = 1; le", "let n = 1; let"],
    ["let n = 1; sea", "let n = 1; search"], ["let Result = tolow", "let Result = tolower"],
    ["// ; |\nSecurityE", "// ; |\nSecurityEvent"],
    ["let Result = SecurityE", "let Result = SecurityEvent"],
    ["let Result = materialize(SecurityE", "let Result = materialize(SecurityEvent"],
    ["(SecurityE", "(SecurityEvent"],
    ["SecurityEvent | wh", "SecurityEvent | where"],
    ["SecurityEvent | order ", "SecurityEvent | order by"],
    ["SecurityEvent | order b", "SecurityEvent | order by"],
    ["SecurityEvent | where Comp", "SecurityEvent | where Computer"],
    ["SecurityEvent | where (Comp", "SecurityEvent | where (Computer"],
    ["SecurityEvent | where isempty(Comp", "SecurityEvent | where isempty(Computer"],
    ["SecurityEvent | where Computer == 'a;|b' and Comp", "SecurityEvent | where Computer == 'a;|b' and Computer"],
    ["SecurityEvent /* ; | */ | project Comp", "SecurityEvent /* ; | */ | project Computer"],
    ["SecurityEvent | project Computer, Det", "SecurityEvent | project Computer, Details"],
    ["SecurityEvent | extend NewName = Comp", "SecurityEvent | extend NewName = Computer"],
    ["SecurityEvent | summarize count() by Comp", "SecurityEvent | summarize count() by Computer"],
    ["SecurityEvent | order by Comp", "SecurityEvent | order by Computer"],
    ["SecurityEvent | where tolow", "SecurityEvent | where tolower"],
    ["['SecurityEvent'] | where Comp", "['SecurityEvent'] | where Computer"],
    ["let Result = materialize(SecurityEvent | where Comp", "let Result = materialize(SecurityEvent | where Computer"],
    ["Web", "['Web Server']"], ["['Web", "['Web Server']"],
    ["SecurityEvent | project Event", "SecurityEvent | project ['Event Source']"],
    ["SecurityEvent | project ['Event", "SecurityEvent | project ['Event Source']"],
    ["SecurityEvent | project whe", "SecurityEvent | project ['where']"],
    ["SecurityEvent | project O", "SecurityEvent | project ['O\\'Brien']"],
    ["SecurityEvent | project Fol", "SecurityEvent | project ['Folder\\\\Name']"]
]) {
    test(`AP3 direct context and safe identifier insertion: ${text}`, () => {
        const result = suggest(text);
        assert.ok(result);
        assert.equal(accepted(text, result), expected);
    });
}

for (const text of [
    "wh", "let SecurityE", "SecurityEvent | SecurityE", "SecurityEvent | where SecurityE",
    "let n = 1; tolow", "let n = 1; se",
    "SecurityEvent | Comp", "SecurityEvent | order Comp", "SecurityEvent | take Comp",
    "SecurityEvent | where 'Comp", "SecurityEvent | where \"Comp", "SecurityEvent | where ```Comp",
    "SecurityEvent | where // Comp", "SecurityEvent | where /* Comp",
    "SecurityEvent | where Details.Comp", "SecurityEvent | where Details['Comp",
    "SecurityEvent | evaluate bag_unpack(Details) | where Comp",
    "UnknownTable | where Comp", "SecurityEvent | unknown Comp",
    "let SecurityEvent = Other; SecurityEvent | where Comp",
    "let ['SecurityEvent'] = Other; SecurityEvent | where Comp",
    "UnknownFunction(SecurityE", "SecurityEvent | where unknown(SecurityE",
    "SecurityEvent | where computer"
]) {
    test(`AP3 suppresses unrelated, unsafe or not-yet-supported context: ${text}`, () => {
        assert.equal(suggest(text), null);
    });
}

test("AP3 never resolves ambiguous columns or functions by arbitrary preference", () => {
    const schema = catalog.schemas[0];
    for (const column of [{ name: "ComputerName", type: "string" }, { name: "tolower_custom", type: "string" },
        { name: "tolower", type: "string" }]) {
        const metadata = { ...catalog, schemas: [{ ...schema, columns: [...schema.columns, column] }] };
        assert.equal(suggest(`SecurityEvent | where ${column.name.startsWith("Comp") ? "Comp" : "tolow"}`, metadata), null);
    }
});

for (const status of ["Loading", "Empty", "Unavailable", "Failed"]) {
    test(`AP3 ${status} schema does not invent columns or disable keyword assistance`, () => {
        const metadata = { ...catalog, schemas: [{ ...catalog.schemas[0], status, columns: [] }] };
        assert.equal(suggest("SecurityEvent | where Comp", metadata), null);
        assert.equal(suggest("SecurityEvent | where tolow", metadata).suffix, "er");
        assert.equal(suggest("SecurityEvent | wh", metadata).suffix, "ere");
    });
}

test("AP3 requires exact source version and one workspace for columns, but keeps table union", () => {
    for (const metadata of [
        { ...catalog, workspaceCount: 2 }, { ...catalog, workspaceCount: 0 },
        { ...catalog, workspaceKey: "other" }, { ...catalog, catalogVersion: 8 }, { ...catalog, schemas: [] }
    ]) {
        assert.equal(suggest("SecurityEvent | where Comp", metadata), null);
        assert.equal(suggest("SecurityE", metadata).suffix, "vent");
    }
});

test("AP3 quoted insertion obeys replacement-aware maxlength and preserves cursor suffix", () => {
    const text = "SecurityEvent | project Event";
    const expected = "SecurityEvent | project ['Event Source']";
    for (const [limit, valid] of [[expected.length - 1, false], [expected.length, true]]) {
        const result = language.completion(snapshot(text, { maxLength: limit }), realEntries, tables, catalog);
        assert.equal(!!result, valid);
        if (result) assert.equal(accepted(text, result), expected);
    }
    const value = `${text}\n| take 10`;
    const result = language.completion(snapshot(value, { selectionStart: text.length, selectionEnd: text.length }),
        realEntries, tables, catalog);
    assert.equal(value.slice(0, result.replaceStart) + result.suffix + value.slice(text.length), `${expected}\n| take 10`);
});

test("protected identifier tokens retain original ranges and decoded source names", () => {
    const value = "['Web Server'] | project ['O\\'Brien'], Computer";
    const result = language.identifierTokens(value);
    assert.deepEqual(plain(result.filter(token => token.kind === "identifier").map(token => ({
        name: token.name, text: value.slice(token.start, token.end)
    }))), [{ name: "Web Server", text: "['Web Server']" }, { name: "O'Brien", text: "['O\\'Brien']" }]);
});

for (const contexts of [[], ["unknown"], ["operator", "operator"], "operator"]) {
    test(`JSON keyword contexts fail explicitly if malformed: ${JSON.stringify(contexts)}`, () => {
        assert.throws(() => language.normalizeEntries([{ word: "where", contexts }]), /contexts/);
    });
}

test("AP3 measures full completion, not just scanning, for an exactly 64 KiB query", t => {
    const line = "let E = materialize(SecurityEvent | where Computer == 'a;|b');\n";
    const tail = "\nSecurityEvent | where Comp";
    const body = line.repeat(Math.floor((65536 - tail.length) / line.length));
    const query = body + " ".repeat(65536 - body.length - tail.length) + tail;
    assert.equal(query.length, 65536);
    for (let count = 0; count < 5; count++) suggest(query);
    const times = [];
    for (let count = 0; count < 30; count++) {
        const start = performance.now();
        assert.equal(suggest(query)?.suffix, "uter");
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; 64 KiB full completion; 30 iterations; p95=${times[28].toFixed(2)} ms`);
});

const flowCatalog = { ...catalog, schemas: [{ ...catalog.schemas[0], columns: [
    { name: "Computer", type: "string" }, { name: "EventID", type: "int" },
    { name: "EventLevel", type: "int" }, { name: "CounterValue", type: "real" },
    { name: "Details", type: "dynamic" }, { name: "Event Source", type: "string" }
] }] };
const flow = text => plain(language.pipelineSchema(text, flowCatalog, tables, realEntries));
const column = (name, type) => ({ name, type });
const baseColumns = flowCatalog.schemas[0].columns;

const diagnose = (text, metadata = flowCatalog, cursor = null) =>
    plain(language.diagnostics(text, realEntries, tables, metadata, cursor));
for (const query of [
    "SecurityEvent | where Computer == ')'; // ] }",
    "SecurityEvent | where Computer == @'a' /* ) ] } */",
    "let S = ```(;|)```; SecurityEvent | project Computer",
    "SecurityEvent | project ['Event Source']",
    "let Name = 'x'; SecurityEvent | project Computer, Value = Name",
    "SecurityEvent | extend New = Computer, More = New | project More",
    "SecurityEvent | project Renamed = Computer | where Renamed == 'x'",
    "SecurityEvent | project-rename Host = Computer | project Host",
    "SecurityEvent | summarize count() by Computer | project Computer, count_",
    "let E = materialize(SecurityEvent | project Computer); E | project Computer",
    "let D = 10; SecurityEvent | take D",
    "SecurityEvent | project Away = 1, Other = Away"
]) {
    test(`AP6 valid supported input has no invented diagnostic: ${query}`, () => {
        const report = diagnose(query);
        assert.deepEqual(report.diagnostics, []);
        assert.equal(report.structureStatus, "Balanced");
        assert.equal(report.completeness, "Limited");
    });
}

for (const query of ["SecurityEvent )", "SecurityEvent | where (Computer == 'a']", "SecurityEvent | project Computer }"]) {
    test(`AP6 definite bracket mismatches have exact error ranges: ${query}`, () => {
        const report = diagnose(query);
        const issue = report.diagnostics.find(issue => issue.code === "UnexpectedCloser");
        assert.equal(issue.severity, "error");
        assert.match(query.slice(issue.start, issue.end), /^[)\]}]$/);
        assert.equal(report.structureStatus, "Invalid");
    });
}

for (const [query, code] of [
    ["SecurityEvent | where (", "OpenDelimiter"],
    ["SecurityEvent | project ['Comp", "OpenLiteral"],
    ["SecurityEvent | where Computer == @'x", "OpenLiteral"],
    ["SecurityEvent | where Computer == ```x", "OpenLiteral"],
    ["SecurityEvent /* unfinished", "OpenComment"]
]) {
    test(`AP6 unfinished lexical input is a draft, not an error: ${query}`, () => {
        const report = diagnose(query);
        assert.equal(report.completeness, "Draft");
        assert.equal(report.structureStatus, "Incomplete");
        assert.ok(report.diagnostics.some(issue => issue.code === code));
        assert.ok(report.diagnostics.every(issue => issue.severity === "info"));
    });
}

for (const query of [
    "SecurityEvent | project Missing",
    "SecurityEvent | project New = Missing",
    "SecurityEvent | project-rename New = Missing",
    "SecurityEvent | extend New = Missing",
    "SecurityEvent | where Missing == 1",
    "SecurityEvent | summarize count() by Missing",
    "SecurityEvent | sort by Missing desc",
    "SecurityEvent | distinct Missing",
    "SecurityEvent | project Computer | project EventID",
    "SecurityEvent | project-rename Host = Computer | where Computer == 'x'",
    "let E = materialize(SecurityEvent | project Computer); E | project EventID"
]) {
    test(`AP6 direct unknown references use the operator's input schema: ${query}`, () => {
        const issues = diagnose(query).diagnostics;
        assert.equal(issues.length, 1);
        assert.equal(issues[0].code, "UnknownColumn");
        assert.equal(issues[0].severity, "warning");
        assert.equal(query.slice(issues[0].start, issues[0].end), issues[0].name);
    });
}

for (const query of [
    "SecurityEvent | invoke UnknownFunction() | project Missing",
    "SecurityEvent | join Other on Computer | project Missing",
    "union SecurityEvent, Other | project Missing",
    "SecurityEvent | project Value = unknown_function() | project Missing",
    "SecurityEvent | where custom_function(Missing)",
    "SecurityEvent | where Details.UnknownProperty == 1",
    "SecurityEvent | project-away Missing*",
    "let E = MissingTable; E | project Missing",
    "let E = E; E | project Missing",
    "SecurityEvent | project Missing + 1",
    "SecurityEvent | extend New = UnknownFunction() | where Missing == 1"
]) {
    test(`AP6 unsupported syntax is not evidence of invalid KQL: ${query}`, () => {
        assert.deepEqual(diagnose(query).diagnostics, []);
    });
}

for (const status of ["Loading", "Failed", "Unavailable", "Empty"]) {
    test(`AP6 ${status} metadata is distinguished from a column problem`, () => {
        const metadata = { ...flowCatalog, schemas: [{ ...flowCatalog.schemas[0], status, columns: [] }] };
        const report = diagnose("SecurityEvent | project Missing", metadata);
        assert.deepEqual(report.diagnostics, []);
        assert.equal(report.schemaStatus, status);
    });
}

for (const change of [{ workspaceCount: 2 }, { workspaceCount: 0 }, { catalogVersion: 999 }, { schemas: [] }]) {
    test(`AP6 uncertain metadata never invents columns: ${JSON.stringify(change)}`, () => {
        assert.deepEqual(diagnose("SecurityEvent | project Missing", { ...flowCatalog, ...change }).diagnostics, []);
    });
}

test("AP6 partial words at the caret remain drafts until the reference is left or completed", () => {
    const query = "SecurityEvent | project Miss";
    const report = diagnose(query, flowCatalog, query.length);
    assert.deepEqual(report.diagnostics, []);
    assert.equal(report.completeness, "Draft");
    assert.equal(report.structureStatus, "Balanced");
    assert.equal(diagnose(query).diagnostics[0].code, "UnknownColumn");
    assert.equal(diagnose(`${query} `, flowCatalog, query.length + 1).diagnostics[0].code, "UnknownColumn");
});

test("AP6 checks the active statement with only earlier let bindings, including trailing semicolons", () => {
    const query = "SecurityEvent | project Missing; SecurityEvent | project Computer";
    assert.deepEqual(diagnose(query).diagnostics, []);
    assert.equal(diagnose(query, flowCatalog, query.indexOf("Missing") - 1).diagnostics[0].name, "Missing");
    assert.equal(diagnose("SecurityEvent | project Missing;").diagnostics[0].name, "Missing");
    assert.deepEqual(diagnose("let E = MissingTable; E | project Missing;").diagnostics, []);
});

test("AP6 bounds presentation work, preserves UTF-16 ranges, and does not mutate metadata", () => {
    const before = JSON.stringify(flowCatalog);
    const query = "// \u{1F600}\nSecurityEvent | where Missing == 1";
    const issue = diagnose(query).diagnostics[0];
    assert.equal(issue.start, query.indexOf("Missing"));
    assert.equal(issue.end, issue.start + "Missing".length);
    const many = diagnose(")".repeat(1000));
    assert.equal(many.diagnostics.length, 12);
    assert.equal(many.truncated, true);
    assert.equal(JSON.stringify(flowCatalog), before);
    assert.throws(() => diagnose("x", flowCatalog, -1), /cursor/);
});

test("AP6 full local completion and diagnostics stay within the 64 KiB p95 budget", t => {
    let body = "let E0 = materialize(SecurityEvent | project Computer, EventID);";
    let index = 0;
    while (body.length < 64000) {
        body += `let E${index + 1} = E${index};`;
        index++;
    }
    const tail = ` E${index} | summarize Anzahl = count() by Computer | where Anz`;
    const query = body + " ".repeat(65536 - body.length - tail.length) + tail;
    assert.equal(Buffer.byteLength(query, "utf8"), 65536);
    const run = () => {
        suggest(query, flowCatalog);
        diagnose(query, flowCatalog, query.length);
    };
    for (let count = 0; count < 5; count++) run();
    const times = [];
    for (let count = 0; count < 30; count++) {
        const start = performance.now();
        run();
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; 64 KiB completion + diagnostics; 30 iterations; p95=${times[28].toFixed(2)} ms`);
    assert.ok(times[28] <= 50, `p95 ${times[28].toFixed(2)} ms exceeds 50 ms`);
});

for (const operator of ["where Computer == 'a;|b'", "take 10", "limit 3", "sort by EventID desc", "order by Computer asc"]) {
    test(`AP4 preserves exact source schema through ${operator}`, () => {
        assert.deepEqual(flow(`SecurityEvent | ${operator}`), { status: "Ready", columns: baseColumns });
        assert.equal(suggest(`SecurityEvent | ${operator} | where Comp`, flowCatalog)?.suffix, "uter");
    });
}

for (const [expression, expected, status = "Ready"] of [
    ["project Computer, EventID", [column("Computer", "string"), column("EventID", "int")]],
    ["project Device = Computer, EventID", [column("Device", "string"), column("EventID", "int")]],
    ["project Device = tostring(Computer), EventID", [column("Device", null), column("EventID", "int")]],
    ["project IsTarget = Computer == 'x'", [column("IsTarget", null)]],
    ["project ['Node Name'] = Computer", [column("Node Name", "string")]],
    ["project-keep Event*", baseColumns.filter(item => item.name.startsWith("Event"))],
    ["project-keep *", baseColumns],
    ["project-away Event*", baseColumns.filter(item => !item.name.startsWith("Event"))],
    ["project-away Computer, EventID", baseColumns.filter(item => !["Computer", "EventID"].includes(item.name))],
    ["project-away ['Event Source']", baseColumns.filter(item => item.name !== "Event Source")],
    ["project-keep Computer, Event*", baseColumns.filter(item => item.name === "Computer" || item.name.startsWith("Event"))],
    ["project-reorder Details, Computer", [baseColumns[4], baseColumns[0], ...baseColumns.filter(item => !["Details", "Computer"].includes(item.name))]],
    ["project-rename Device = Computer", baseColumns.map(item => item.name === "Computer" ? column("Device", "string") : item)],
    ["project-rename Device = Computer, ID = EventID", baseColumns.map(item =>
        item.name === "Computer" ? column("Device", "string") : item.name === "EventID" ? column("ID", "int") : item)],
    ["extend Device = Computer", [...baseColumns, column("Device", "string")]],
    ["extend Computer = tostring(EventID)", [...baseColumns.slice(1), column("Computer", null)]],
    ["extend Device = Computer, Copied = Device", [...baseColumns, column("Device", "string"), column("Copied", "string")]],
    ["extend Device = 'x;|,='", [...baseColumns, column("Device", "string")]],
    ["extend IsTarget = Computer == 'x'", [...baseColumns, column("IsTarget", null)]],
    ["extend Enabled = true", [...baseColumns, column("Enabled", "bool")]],
    ["project Disabled = false", [column("Disabled", "bool")]],
    ["distinct Computer, EventID", [column("Computer", "string"), column("EventID", "int")]],
    ["distinct *", baseColumns],
    ["summarize Anzahl = count() by Computer", [column("Computer", "string"), column("Anzahl", "long")]],
    ["summarize Maximum = max(EventID) by Device = Computer", [column("Device", "string"), column("Maximum", null)]],
    ["summarize Total = sum(CounterValue), Nodes = dcount(Computer) by EventID",
        [column("EventID", "int"), column("Total", null), column("Nodes", null)]],
    ["summarize count()", [column("count_", "long")]],
    ["summarize by Computer", [column("Computer", "string")]],
    ["summarize avg(CounterValue) by Computer", [column("Computer", "string")], "Partial"],
    ["project Computer, unknown_function(Details)", [column("Computer", "string")], "Partial"],
    ["project Computer, UnknownColumn", [column("Computer", "string")], "Partial"],
    ["project-keep * | project-away Event* | distinct Computer", [column("Computer", "string")]]
]) {
    test(`AP4 output schema matches KQL's supported named shape: ${expression}`, () => {
        assert.deepEqual(flow(`SecurityEvent | ${expression}`), { status, columns: expected });
    });
}

for (const [text, name] of [
    ["project Computer, EventID | where Comp", "Computer"],
    ["project-away Event* | where Comp", "Computer"],
    ["project-rename Device = Computer | where Dev", "Device"],
    ["extend Device = Computer | project Device | where Dev", "Device"],
    ["summarize Anzahl = count() by Computer | where Anz", "Anzahl"],
    ["summarize Maximum = max(EventID) by Computer | where Max", "Maximum"],
    ["summarize count() by Computer | project ['cou", "count_"],
    ["project ['Node Name'] = Computer | where Node", "['Node Name']"],
    ["where true | take 10 | limit 3 | sort by Computer | order by EventID | project Comp", "Computer"],
    ["project Computer, EventID | summarize Anzahl = count() by Comp", "Computer"],
    ["extend Device = Computer, Copied = Dev", "Device"],
    ["extend Device = Computer, Copied = strcat(Dev", "Device"],
    ["project-rename Device = Comp", "Computer"],
    ["top 10 by Comp", "Computer"]
]) {
    test(`AP4 completion uses pipeline/current-expression schema: ${text}`, () => {
        const query = `SecurityEvent | ${text}`;
        const result = suggest(query, flowCatalog);
        assert.ok(result);
        assert.ok(accepted(query, result).endsWith(name));
    });
}

for (const text of [
    "project Computer | where EventI",
    "project Device = Computer | where Comp",
    "project-away Event* | where EventI",
    "project-rename Device = Computer | where Comp",
    "distinct Computer | where EventI",
    "summarize Anzahl = count() by Computer | where EventI",
    "summarize Anzahl = count() by Device = Computer | where Comp",
    "project Device = Computer, Copy = Dev",
    "summarize Anzahl = count(), Copy = Anz",
    "summarize Anzahl = count() by Anz",
    "project-rename Dev",
    "extend Dev",
    "project Computer | evaluate bag_unpack(Computer) | where Comp",
    "join Other on Computer | where Comp",
    "union Other | where Comp"
]) {
    test(`AP4 removed, output-only or unknowable columns are not suggested: ${text}`, () => {
        assert.equal(suggest(`SecurityEvent | ${text}`, flowCatalog), null);
    });
}

for (const expression of [
    "unknownoperator value", "join Other on Computer", "evaluate bag_unpack(Details)",
    "project Computer,", "extend Device =", "extend tostring(Computer)",
    "project-rename Computer = EventID", "project-rename Device = Missing",
    "project-rename Device = Computer, Other = Computer",
    "project Computer, Computer", "project Computer, Copied = (Computer",
    "project (A, B) = unknown_function(Details)", "order Computer", "take",
    "summarize Count = count() by", "summarize Count = count(),",
    "summarize Latest = arg_max(EventID, *)", "summarize Values = percentiles(EventID, 50, 90)",
    "extend Stats = series_stats(Details)",
    "extend NewValue = unknown_function(EventID)",
    "project NewValue = unknown_function(EventID)",
    "summarize NewValue = unknown_aggregate(EventID) by Computer",
    "project *", "project Event*", "distinct Event*",
    "project Device = Missing", "extend Device = Missing",
    "project Computer | unsupported x | project Computer"
]) {
    test(`AP4 reports unsupported flow explicitly rather than assuming schema preservation: ${expression}`, () => {
        assert.deepEqual(flow(`SecurityEvent | ${expression}`), { status: "Unsupported", columns: [] });
    });
}

test("AP4 unknown types are not guessed or displayed as null and known names remain useful", () => {
    const result = suggest("SecurityEvent | extend NewValue = tostring(EventID) | where NewV", flowCatalog);
    assert.equal(result.entry.word, "NewValue");
    assert.equal(result.entry.info, "NewValue");
    assert.equal(flow("SecurityEvent | extend NewValue = tostring(EventID)").columns.at(-1).type, null);
});

test("AP4 current expressions use input schema; extend aliases are available only after their definition", () => {
    assert.equal(suggest("SecurityEvent | project Device = Comp", flowCatalog)?.entry.word, "Computer");
    assert.equal(suggest("SecurityEvent | extend Device = Dev", flowCatalog), null);
    assert.equal(suggest("SecurityEvent | extend Device = Computer, Copied = Dev", flowCatalog)?.entry.word, "Device");
    assert.equal(suggest("SecurityEvent | project Device = Computer, Copied = Dev", flowCatalog), null);
    assert.equal(suggest("SecurityEvent | summarize Count = count() by Comp", flowCatalog)?.entry.word, "Computer");
});

test("AP4 pipeline flow respects source gates and never upgrades unknown metadata to Ready", () => {
    for (const metadata of [
        { ...flowCatalog, workspaceCount: 2 }, { ...flowCatalog, workspaceCount: 0 },
        { ...flowCatalog, workspaceKey: "other" }, { ...flowCatalog, catalogVersion: 8 },
        { ...flowCatalog, schemas: [] },
        ...["Loading", "Empty", "Unavailable", "Failed"].map(status => ({
            ...flowCatalog, schemas: [{ ...flowCatalog.schemas[0], status, columns: [] }]
        }))
    ]) {
        const result = language.pipelineSchema("SecurityEvent | extend New = Computer | project New", metadata, tables, realEntries);
        assert.notEqual(result.status, "Ready");
        assert.deepEqual(plain(result.columns), []);
        assert.equal(suggest("SecurityEvent | extend New = Computer | where New", metadata), null);
        assert.equal(suggest("SecurityEvent | extend New = Computer | where tolow", metadata)?.suffix, "er");
    }
});

test("AP4 uses token boundaries, not pipes, commas, by or equals inside strings/comments", () => {
    const query = "SecurityEvent | extend Text = strcat('x,by=|;', Computer) /* | project Lost */" +
        " | project Text, Computer // | summarize\n | summarize Total = count() by Computer";
    assert.deepEqual(flow(query), { status: "Ready", columns: [column("Computer", "string"), column("Total", "long")] });
});

test("AP4 schemas and dictionaries are not mutated by source or sequential output analysis", () => {
    const original = JSON.stringify(flowCatalog);
    const words = JSON.stringify(realEntries);
    for (const expression of ["extend Computer = Details, Copy = Computer", "project-rename Device = Computer",
        "project-away Event*", "summarize N = count() by Computer"]) {
        flow(`SecurityEvent | ${expression}`);
        suggest(`SecurityEvent | ${expression} | where Comp`, flowCatalog);
    }
    assert.equal(JSON.stringify(flowCatalog), original);
    assert.equal(JSON.stringify(realEntries), words);
});

test("AP4 partial output stays explicit across projections and never restores the old source schema", () => {
    const schema = flow("SecurityEvent | summarize avg(CounterValue) by Computer | project Computer");
    assert.equal(schema.status, "Partial");
    assert.deepEqual(schema.columns, [column("Computer", "string")]);
    assert.equal(suggest("SecurityEvent | summarize avg(CounterValue) by Computer | where EventI", flowCatalog), null);
});

test("AP4 bounds deeply nested expressions instead of overflowing the analysis stack", () => {
    const query = `SecurityEvent | extend X = ${"(".repeat(80)}Computer${")".repeat(80)}`;
    assert.deepEqual(flow(query), { status: "Unsupported", columns: [] });
    assert.equal(suggest(`${query} | where Comp`, flowCatalog), null);
});

for (const value of ["unknown", 2, null]) {
    test(`AP4 JSON result-column metadata rejects unsupported values: ${JSON.stringify(value)}`, () => {
        assert.throws(() => language.normalizeEntries([{ word: "f", resultColumns: value }]), /resultColumns/);
    });
}

test("AP4 benchmark measures real schema propagation across an exactly 64 KiB pipeline", t => {
    const startText = "SecurityEvent";
    const stage = " | where Computer == 'a;|b' /* | summarize Wrong */";
    const tail = " | project Computer, EventID | extend Device = Computer | summarize Anzahl = count() by Device | where Anz";
    const body = startText + stage.repeat(Math.floor((65536 - startText.length - tail.length) / stage.length));
    const query = body + " ".repeat(65536 - body.length - tail.length) + tail;
    assert.equal(query.length, 65536);
    for (let count = 0; count < 5; count++) suggest(query, flowCatalog);
    const times = [];
    for (let count = 0; count < 30; count++) {
        const start = performance.now();
        assert.equal(suggest(query, flowCatalog)?.suffix, "ahl");
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; 64 KiB schema-flow completion; 30 iterations; p95=${times[28].toFixed(2)} ms`);
});

for (const [query, expected] of [
    ["let E = SecurityEvent | project Computer, EventID; E",
        [column("Computer", "string"), column("EventID", "int")]],
    ["let E = materialize(SecurityEvent | project Computer, EventID); E",
        [column("Computer", "string"), column("EventID", "int")]],
    ["let E = materialize(SecurityEvent | project Computer); let F = E | extend Device = Computer; F",
        [column("Computer", "string"), column("Device", "string")]],
    ["let E = SecurityEvent | project Computer; let F = materialize(E | project Device = Computer); F",
        [column("Device", "string")]],
    ["let E = (SecurityEvent | project Computer); materialize(E | summarize Anzahl = count() by Computer)",
        [column("Computer", "string"), column("Anzahl", "long")]],
    ["let ['Recent Events'] = materialize(SecurityEvent | project Computer); ['Recent Events']",
        [column("Computer", "string")]],
    ['let ["Recent Events"] = SecurityEvent | project Computer; ["Recent Events"]',
        [column("Computer", "string")]],
    ["let Limit = 5; let E = SecurityEvent | take Limit | project Computer; E", [column("Computer", "string")]],
    ["let Text = 'test'; let E = SecurityEvent | extend Label = Text | project Label; E", [column("Label", "string")]],
    ["let Limit = 5; let Copy = Limit; SecurityEvent | project Value = Copy", [column("Value", "long")]],
    ["let ['Text Value'] = 'test'; let Copy = ['Text Value']; SecurityEvent | project Value = Copy", [column("Value", "string")]],
    ["let Flag = true; let Copy = (Flag); SecurityEvent | project Value = Copy", [column("Value", "bool")]],
    ["let E = materialize(materialize(SecurityEvent | project Computer)); E", [column("Computer", "string")]],
    ["(SecurityEvent | project Computer) | project Device = Computer", [column("Device", "string")]],
    ["let E = SecurityEvent | project Computer; E | summarize Anzahl = count() by Computer", [column("Computer", "string"), column("Anzahl", "long")]]
]) {
    test(`AP5 binding and materialized output schema: ${query}`, () => {
        assert.deepEqual(flow(query), { status: "Ready", columns: expected });
    });
}

for (const [query, expected] of [
    ["let Ereignisse = SecurityEvent; Ere", "Ereignisse"],
    ["let Ereignisse = SecurityEvent; let Next = Ere", "Ereignisse"],
    ["let Ereignisse = SecurityEvent; materialize(Ere", "Ereignisse"],
    ["let Ereignisse = SecurityEvent; (Ere", "Ereignisse"],
    ["let Ereignisse = SecurityEvent | project Computer; Ereignisse | where Comp", "Computer"],
    ["let Ereignisse = materialize(SecurityEvent | project Computer); Ereignisse | where Comp", "Computer"],
    ["let E = SecurityEvent; let F = E | project Device = Computer; F | where Dev", "Device"],
    ["let E = SecurityEvent | project Computer; let F = materialize(E | project Comp", "Computer"],
    ["let E = SecurityEvent | project Computer; materialize(E) | where Comp", "Computer"],
    ["let Threshold = 5; SecurityEvent | where EventID > Thres", "Threshold"],
    ["let Threshold = 5; SecurityEvent | take Thres", "Threshold"],
    ["let Threshold = 5; let Copy = Thres", "Threshold"],
    ["let Threshold = 5; let Copy = (Thres", "Threshold"],
    ["let Threshold = 5; SecurityEvent | extend New = Thres", "Threshold"],
    ["let Threshold = 5; SecurityEvent | where EventID > (Thres", "Threshold"],
    ["let Threshold = 5; SecurityEvent | where EventID > tolong(Thres", "Threshold"],
    ["let Window = ago(1h); SecurityEvent | where EventID > Win", "Window"],
    ["let Mask = dynamic([1, 2]); SecurityEvent | where EventID in (Mas", "Mask"],
    ["let Text = tostring('x'); SecurityEvent | where Computer == Tex", "Text"],
    ["let ['Upper Bound'] = 5; SecurityEvent | where EventID > Upp", "['Upper Bound']"],
    ["let ['Recent Events'] = SecurityEvent; Rece", "['Recent Events']"],
    ["let ['Recent Events'] = SecurityEvent; ['Rece", "['Recent Events']"],
    ["let Threshold = 5 /* ; let Future = SecurityEvent; */; SecurityEvent | take Thres", "Threshold"],
    ["let E = SecurityEvent | project Computer; let F = materialize(E | where (Comp", "Computer"]
]) {
    test(`AP5 typed variable/context completion: ${query}`, () => {
        const result = suggest(query, flowCatalog);
        assert.ok(result);
        assert.ok(accepted(query, result).endsWith(expected), accepted(query, result));
    });
}

for (const query of [
    "let Threshold = 5; Thres",
    "let Threshold = 5; materialize(Thres",
    "let E = SecurityEvent; SecurityEvent | where EventID > E",
    "let E = SecurityEvent | project Computer; E | where EventI",
    "let E = SecurityEvent | project Device = Computer; E | where Comp",
    "let E = materialize(SecurityEvent | summarize N = count() by Computer); E | where EventI",
    "let E = Future; let Future = SecurityEvent; E | where Comp",
    "let E = E; E | where Comp",
    "let E = F; let F = E; E | where Comp",
    "let SecurityEvent = 5; SecurityE",
    "let SecurityEvent = 5; SecurityEvent | where Comp",
    "let SecurityEvent = UnknownFunction(); SecurityE",
    "let SecurityEvent = UnknownFunction(); SecurityEvent | where Comp",
    "let F = () { SecurityEvent | project Computer }; F",
    "let F = () { SecurityEvent | project Computer }; F() | where Comp",
    "let F = (x:long) { let Local = SecurityEvent; Local }; Loc",
    "let F = (x:long) { let Local = SecurityEvent; SecurityE",
    "let E = unknown_function(); E | where Comp",
    "let E = table('SecurityEvent'); E | where Comp",
    "let Threshold = unknown_function(); SecurityEvent | take Thres",
    "let materialize = 5; materialize(SecurityE",
    "let Threshold = tostring; SecurityEvent | take Thres",
    "let Threshold = ['Unknown']; SecurityEvent | take Thres",
    "let E = SecurityEvent; let E = Perf; E | where Comp",
    "let E = materialize(5); E | where Comp",
    "let E = materialize(SecurityEvent | where true",
    "let E = SecurityEvent; let Threshold = 5; SecurityEvent | extend Thres",
    "let Threshold = 5; SecurityEvent | project-rename New = Thres"
]) {
    test(`AP5 no future, cyclic, scalar-as-source or function-result assumptions: ${query}`, () => {
        assert.equal(suggest(query, flowCatalog), null);
    });
}

test("AP5 uses only declarations before the cursor and requires a terminating semicolon", () => {
    const value = "let E = SecurityEvent;\nEre\n; let Ereignisse = SecurityEvent;";
    const cursor = value.indexOf("\n; let");
    assert.equal(language.completion(snapshot(value, { selectionStart: cursor, selectionEnd: cursor }),
        realEntries, tables, flowCatalog), null);
    assert.equal(suggest("let Ereignisse = SecurityEvent Ere", flowCatalog), null);
    const text = "let Ereignisse = SecurityEvent; Ere";
    assert.equal(suggest(text, flowCatalog)?.entry.kind, "tableVariable");
});

test("AP5 columns take precedence over same-name scalar variables, without duplicate ambiguous candidates", () => {
    const result = suggest("let Computer = 5; SecurityEvent | where Comp", flowCatalog);
    assert.equal(result.entry.kind, "column");
    assert.equal(result.entry.info, "Computer: string");
    assert.equal(flow("let Computer = 5; SecurityEvent | project Copy = Computer").columns[0].type, "string");
    const scalar = suggest("let Threshold = 5; SecurityEvent | take Thres", flowCatalog);
    assert.equal(scalar.entry.kind, "scalarVariable");
    assert.equal(scalar.entry.info, "Threshold: long");
});

test("AP5 root bindings do not leak from function bodies or consume later redefinitions retroactively", () => {
    const query = "let E = SecurityEvent | project Computer; let Saved = E; " +
        "let Function = () { let E = Perf; E }; Saved";
    assert.deepEqual(flow(query), { status: "Ready", columns: [column("Computer", "string")] });
    assert.deepEqual(flow("let E = SecurityEvent | project Computer; let Saved = E; let E = Perf; Saved"),
        { status: "Ready", columns: [column("Computer", "string")] });
});

test("AP5 scoped expressions inherit but never export their local definitions", () => {
    const query = "let E = SecurityEvent | project Computer; " +
        "let Result = materialize(let E = SecurityEvent | project EventID; E); Result";
    assert.deepEqual(flow(query), { status: "Ready", columns: [column("EventID", "int")] });
    assert.deepEqual(flow(`${query} | project EventID`), { status: "Ready", columns: [column("EventID", "int")] });
    assert.deepEqual(flow(`${query}; E`), { status: "Ready", columns: [column("Computer", "string")] });
    assert.equal(suggest("let Result = materialize(let Local = SecurityEvent; Local); Loc", flowCatalog), null);
});

test("AP5 function or unresolved bindings shadow built-in names instead of assuming their results", () => {
    assert.equal(suggest("let tostring = () { SecurityEvent }; let E = SecurityEvent | extend Device = tostring(Computer); E | where Dev",
        flowCatalog), null);
    assert.equal(suggest("let materialize = 5; let E = materialize(SecurityEvent); E | where Comp", flowCatalog), null);
});

test("AP5 metadata provenance remains mandatory through materialized dependency chains", () => {
    const query = "let E = materialize(SecurityEvent | project Computer); let Saved = E; Saved | where Comp";
    for (const metadata of [
        { ...flowCatalog, workspaceCount: 2 }, { ...flowCatalog, workspaceCount: 0 },
        { ...flowCatalog, workspaceKey: "other" }, { ...flowCatalog, catalogVersion: 8 },
        { ...flowCatalog, schemas: [] },
        ...["Loading", "Empty", "Unavailable", "Failed"].map(status => ({
            ...flowCatalog, schemas: [{ ...flowCatalog.schemas[0], status, columns: [] }]
        }))
    ]) {
        assert.equal(suggest(query, metadata), null);
        assert.equal(suggest("let E = materialize(SecurityEvent); Ere", metadata), null);
        assert.equal(suggest("let Events = materialize(SecurityEvent); Even", metadata)?.entry.word, "Events");
    }
});

test("AP5 long binding chains are resolved in definition order without recursive expansion or mutation", () => {
    const original = JSON.stringify(flowCatalog);
    const chain = "let E0 = SecurityEvent | project Computer;" +
        Array.from({ length: 600 }, (_, index) => `let E${index + 1} = E${index};`).join("") + "E600";
    assert.deepEqual(flow(chain), { status: "Ready", columns: [column("Computer", "string")] });
    assert.equal(suggest(`${chain} | where Comp`, flowCatalog)?.suffix, "uter");
    assert.equal(JSON.stringify(flowCatalog), original);
});

test("AP5 scalar/binding keyword collisions preserve unquoted booleans and quoted identifiers", () => {
    const metadata = { ...flowCatalog, schemas: [...flowCatalog.schemas,
        { ...flowCatalog.schemas[0], tableName: "true" }] };
    const names = [...tables, { word: "true" }];
    const result = language.pipelineSchema("let Flag = true; SecurityEvent | project Value = Flag",
        metadata, names, realEntries);
    assert.deepEqual(plain(result.columns), [column("Value", "bool")]);
    assert.equal(suggest("let tostring = 'value'; SecurityEvent | where Computer == tostr", flowCatalog)?.entry.kind, "scalarVariable");
});

test("AP5 benchmark covers exactly 64 KiB of unique ordered binding dependencies", t => {
    let definitions = "let E0 = materialize(SecurityEvent | project Computer, EventID);";
    let index = 0;
    while (definitions.length < 64000) {
        definitions += `let E${index + 1} = E${index};`;
        index++;
    }
    const tail = ` E${index} | summarize Anzahl = count() by Computer | where Anz`;
    const query = definitions + " ".repeat(65536 - definitions.length - tail.length) + tail;
    assert.equal(query.length, 65536);
    for (let count = 0; count < 5; count++) suggest(query, flowCatalog);
    const times = [];
    for (let count = 0; count < 30; count++) {
        const start = performance.now();
        assert.equal(suggest(query, flowCatalog)?.suffix, "ahl");
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; 64 KiB ordered-binding completion; 30 iterations; p95=${times[28].toFixed(2)} ms`);
});

const ap8Tables = ["LeftEvents", "RightEvents", "ThirdEvents"].map(word => ({ word }));
const ap8Catalog = { ...flowCatalog, schemas: [
    { ...readySchema, tableName: "LeftEvents", columns: [
        column("Key", "string"), column("Value", "long"), column("LeftOnly", "string")
    ] },
    { ...readySchema, tableName: "RightEvents", columns: [
        column("Key", "string"), column("Value", "int"), column("RemoteOnly", "string")
    ] },
    { ...readySchema, tableName: "ThirdEvents", columns: [column("Value_long", "long"), column("Third", "bool")] }
] };
const ap8Flow = (query, metadata = ap8Catalog) => plain(language.pipelineSchema(query, metadata, ap8Tables, realEntries));
const ap8Suggest = (query, metadata = ap8Catalog) => language.completion(snapshot(query), realEntries, ap8Tables, metadata);
const ap8Diagnose = (query, metadata = ap8Catalog) => plain(language.diagnostics(query, realEntries, ap8Tables, metadata));
const bothJoinColumns = [
    ...ap8Catalog.schemas[0].columns, column("Key1", "string"), column("Value1", "int"), column("RemoteOnly", "string")
];

for (const kind of ["", "innerunique", "inner", "leftouter", "rightouter", "fullouter",
    "leftsemi", "leftanti", "anti", "leftantisemi", "rightsemi", "rightanti", "rightantisemi"]) {
    test(`AP8 join output respects ${kind || "default innerunique"} schema and preserves both keys when appropriate`, () => {
        const leftOnly = ["leftsemi", "leftanti", "anti", "leftantisemi"].includes(kind);
        const rightOnly = ["rightsemi", "rightanti", "rightantisemi"].includes(kind);
        const result = ap8Flow(`LeftEvents | join ${kind ? `kind=${kind} ` : ""}(RightEvents) on Key`);
        assert.deepEqual(result, { status: "Ready", columns: leftOnly ? ap8Catalog.schemas[0].columns :
            rightOnly ? ap8Catalog.schemas[1].columns : bothJoinColumns });
    });
}

for (const keys of ["Key", "$left.Key == $right.Key", "$right.Key == $left.Key",
    "Key, $left.LeftOnly == $right.RemoteOnly", "Key and $left.LeftOnly == $right.RemoteOnly"]) {
    test(`AP8 join validates supported key expressions: ${keys}`, () => {
        assert.deepEqual(ap8Flow(`LeftEvents | join RightEvents on ${keys}`), { status: "Ready", columns: bothJoinColumns });
    });
}

test("AP8 join collision naming reserves original names before generating suffixes", () => {
    const metadata = { ...ap8Catalog, schemas: [
        { ...ap8Catalog.schemas[0], columns: [column("Key", "string"), column("Key1", "string")] },
        { ...ap8Catalog.schemas[1], columns: [column("Key", "string"), column("Key1", "string"), column("Key2", "string")] }
    ] };
    assert.deepEqual(ap8Flow("LeftEvents | join RightEvents on Key", metadata).columns.map(c => c.name),
        ["Key", "Key1", "Key3", "Key11", "Key2"]);
});

for (const suffix of ["kind=invalid RightEvents on Key", "kind=inner kind=leftouter RightEvents on Key",
    "RightEvents on Missing", "RightEvents on Value", "RightEvents on $left.Key == $left.Key",
    "RightEvents on $left.Key = $right.Key", "RightEvents on Key or Key",
    "RightEvents on Key,", "RightEvents", "RightEvents on", "(UnknownFunction()) on Key",
    "hint.strategy=broadcast RightEvents on Key"]) {
    test(`AP8 does not invent a join schema for invalid or unresolved input: ${suffix}`, () => {
        assert.deepEqual(ap8Flow(`LeftEvents | join ${suffix}`), { status: "Unsupported", columns: [] });
    });
}

for (const query of ["union LeftEvents, RightEvents", "LeftEvents | union RightEvents",
    "union kind=outer (LeftEvents), (RightEvents)", "union isfuzzy=false LeftEvents, RightEvents"]) {
    test(`AP8 union outer preserves names and splits actual conflicting types: ${query}`, () => {
        assert.deepEqual(ap8Flow(query), { status: "Ready", columns: [
            column("Key", "string"), column("Value_long", "long"), column("Value_int", "int"),
            column("LeftOnly", "string"), column("RemoteOnly", "string")
        ] });
    });
}

test("AP8 union type suffixes do not overwrite existing columns", () => {
    assert.deepEqual(ap8Flow("union LeftEvents, RightEvents, ThirdEvents"), { status: "Ready", columns: [
        column("Key", "string"), column("Value_long1", "long"), column("Value_int", "int"),
        column("LeftOnly", "string"), column("RemoteOnly", "string"), column("Value_long", "long"), column("Third", "bool")
    ] });
});

test("AP8 union inner intersects names and keeps differing/unknown types explicitly partial", () => {
    assert.deepEqual(ap8Flow("union kind=inner LeftEvents, RightEvents"),
        { status: "Partial", columns: [column("Key", "string")] });
    const sameType = { ...ap8Catalog, schemas: ap8Catalog.schemas.map(schema => ({
        ...schema, columns: schema.columns.map(c => c.name === "Value" ? column("Value", "long") : c)
    })) };
    assert.deepEqual(ap8Flow("union kind=inner LeftEvents, RightEvents", sameType),
        { status: "Ready", columns: [column("Key", "string"), column("Value", "long")] });
    assert.deepEqual(ap8Flow("union kind=inner LeftEvents, ThirdEvents"), { status: "Ready", columns: [] });
    assert.equal(ap8Diagnose("union kind=inner LeftEvents, RightEvents | project Missing").diagnostics.length, 0);
});

test("AP8 union withsource adds a quoted-capable string column without changing input schemas", () => {
    const original = JSON.stringify(ap8Catalog);
    const result = ap8Flow("union withsource=['Source Table'] LeftEvents, RightEvents");
    assert.equal(result.status, "Ready");
    assert.deepEqual(result.columns[0], column("Source Table", "string"));
    const query = "union withsource=['Source Table'] LeftEvents, RightEvents | project Sou";
    assert.equal(accepted(query, ap8Suggest(query)), query.slice(0, -3) + "['Source Table']");
    assert.equal(JSON.stringify(ap8Catalog), original);
    assert.deepEqual(ap8Flow("union withsource=Key LeftEvents, RightEvents"), { status: "Unsupported", columns: [] });
});

for (const query of ["union isfuzzy=true LeftEvents, Missing", "union LeftEvents, Missing",
    "union LeftEvents,", "union LeftEvents, UnknownFunction()", "union Left*", "union kind=invalid LeftEvents",
    "union kind=outer kind=inner LeftEvents", "union hint.concurrency=4 LeftEvents",
    "let RightEvents = 5; union LeftEvents, RightEvents"]) {
    test(`AP8 union unresolved sources/options never masquerade as a complete schema: ${query}`, () => {
        assert.deepEqual(ap8Flow(query), { status: "Unsupported", columns: [] });
    });
}

for (const [query, expected] of [
    ["LeftEvents | join (RightE", "LeftEvents | join (RightEvents"],
    ["LeftEvents | join kind=inner RightE", "LeftEvents | join kind=inner RightEvents"],
    ["LeftEvents | join RightEvents ", "LeftEvents | join RightEvents on"],
    ["LeftEvents | join RightEvents on K", "LeftEvents | join RightEvents on Key"],
    ["LeftEvents | join RightEvents on $left.Lef", "LeftEvents | join RightEvents on $left.LeftOnly"],
    ["LeftEvents | join RightEvents on $left.LeftOnly == $right.Rem", "LeftEvents | join RightEvents on $left.LeftOnly == $right.RemoteOnly"],
    ["LeftEvents | join (RightEvents | project Rem", "LeftEvents | join (RightEvents | project RemoteOnly"],
    ["union LeftE", "union LeftEvents"],
    ["union kind=inner LeftEvents, RightE", "union kind=inner LeftEvents, RightEvents"],
    ["union (LeftEvents | project LeftOnly), (RightE", "union (LeftEvents | project LeftOnly), (RightEvents"],
    ["LeftEvents | union (RightEvents | project Rem", "LeftEvents | union (RightEvents | project RemoteOnly"],
    ["LeftEvents | join RightEvents on Key | project Rem", "LeftEvents | join RightEvents on Key | project RemoteOnly"],
    ["let R = materialize(RightEvents | project Key, RemoteOnly); LeftEvents | join R on Key | project Rem",
        "let R = materialize(RightEvents | project Key, RemoteOnly); LeftEvents | join R on Key | project RemoteOnly"],
    ["let U = union LeftEvents, RightEvents; U | project Rem", "let U = union LeftEvents, RightEvents; U | project RemoteOnly"],
    ["union (LeftEvents), (let R = RightEvents; R | project Rem", "union (LeftEvents), (let R = RightEvents; R | project RemoteOnly"]
]) {
    test(`AP8 source/key/subquery/output completion uses the exact scope: ${query}`, () => {
        const suggestion = ap8Suggest(query);
        assert.ok(suggestion, "expected a unique completion");
        assert.equal(accepted(query, suggestion), expected);
    });
}

test("AP8 nested join/union preserves local let scopes, materialize and projected schemas", () => {
    const query = "let R = materialize(RightEvents | project Key, RemoteOnly); " +
        "LeftEvents | join (union R, (let Local = R; Local)) on Key | project RemoteOnly";
    assert.deepEqual(ap8Flow(query), { status: "Ready", columns: [column("RemoteOnly", "string")] });
    assert.equal(ap8Suggest(query + "; Local | project Rem"), null);
    assert.deepEqual(ap8Flow("let U = union U, LeftEvents; U"), { status: "Unsupported", columns: [] });
});

test("AP8 diagnostics validate post-join output but never flag uncertain inner union columns", () => {
    const report = ap8Diagnose("LeftEvents | join RightEvents on Key | project Missing");
    assert.equal(report.schemaStatus, "Partial");
    assert.deepEqual(report.diagnostics.map(d => d.name), ["Missing"]);
    assert.equal(ap8Diagnose("LeftEvents | join RightEvents on Key | project Key1").diagnostics.length, 0);
    assert.equal(ap8Diagnose("union kind=inner LeftEvents, RightEvents | project Value").diagnostics.length, 0);
});

for (const change of [
    { workspaceCount: 2 }, { workspaceCount: 0 }, { catalogVersion: 999 }, { workspaceKey: "wrong" },
    ...["Loading", "Failed", "Unavailable", "Empty"].map(status => ({
        schemas: [ap8Catalog.schemas[0], { ...ap8Catalog.schemas[1], status, columns: [] }]
    }))
]) {
    test(`AP8 metadata provenance never upgrades unknown sources: ${JSON.stringify(change)}`, () => {
        const metadata = { ...ap8Catalog, ...change };
        assert.equal(ap8Suggest("LeftEvents | join RightEvents on Key | project Rem", metadata), null);
        assert.equal(ap8Suggest("union LeftEvents, RightEvents | project Rem", metadata), null);
        assert.equal(ap8Diagnose("union LeftEvents, RightEvents | project Missing", metadata).diagnostics.length, 0);
        assert.equal(ap8Suggest("LeftEvents | join RightE", metadata)?.suffix, "vents");
    });
}

test("AP8 schema growth is exact at 1024 columns and fails closed above the bound", () => {
    for (const [size, status] of [[512, "Ready"], [513, "Unsupported"]]) {
        const columns = [column("Key", "string"), ...Array.from({ length: size - 1 }, (_, i) => column(`C${i}`, "long"))];
        const metadata = { ...ap8Catalog, schemas: ap8Catalog.schemas.slice(0, 2).map(s => ({ ...s, columns })) };
        const result = ap8Flow("LeftEvents | join RightEvents on Key", metadata);
        assert.equal(result.status, status);
        assert.equal(result.columns.length, status === "Ready" ? 1024 : 0);
    }
    let query = "let T0 = LeftEvents;";
    for (let i = 1; i < 15; i++) query += `let T${i} = T${i - 1} | join T${i - 1} on Key;`;
    assert.deepEqual(ap8Flow(query + "T14"), { status: "Unsupported", columns: [] });
});

test("AP8 join keys never suggest known incompatible types or same-side equality", () => {
    for (const prefix of ["Val", "$left.Key == $right.Val", "$left.Key == $left.K",
        "$left.Missing == $right.K", "$left.Key = $right.K"])
        assert.equal(ap8Suggest(`LeftEvents | join RightEvents on ${prefix}`), null, prefix);
});

test("AP8 quoted join keys survive nested rename and right-side output completion", () => {
    const query = "LeftEvents | project-rename ['Event Key']=Key | join " +
        "(RightEvents | project-rename ['Remote Key']=Key) on $left.['Event Key'] == $right.['Remote Key'] | project RemoteO";
    assert.equal(accepted(query, ap8Suggest(query)), query + "nly");
    assert.equal(ap8Suggest(query.slice(0, -4)), null);
});

test("AP8 union refuses to guess conflicting null or unrecognized types", () => {
    for (const type of [null, "custom"]) {
        const query = type === null
            ? "union (LeftEvents | extend Value = Value + 1), (RightEvents | extend Value = Value + 1)"
            : "union LeftEvents, RightEvents";
        const metadata = { ...ap8Catalog, schemas: ap8Catalog.schemas.map(schema => ({
            ...schema, columns: schema.columns.map(c => c.name === "Value" && type !== null ? column("Value", type) : c)
        })) };
        if (type === "custom") metadata.schemas[1].columns[1] = column("Value", "string");
        const result = ap8Flow(query, metadata);
        assert.equal(result.status, "Partial");
        assert.ok(!result.columns.some(c => c.name.startsWith("Value")));
    }
});

test("AP8 union limit includes withsource and counts merged columns rather than input sizes", () => {
    const columns = Array.from({ length: 1024 }, (_, i) => column(`C${i}`, "long"));
    const metadata = { ...ap8Catalog, schemas: ap8Catalog.schemas.slice(0, 2).map(s => ({ ...s, columns })) };
    assert.equal(ap8Flow("union LeftEvents, RightEvents", metadata).columns.length, 1024);
    assert.equal(ap8Flow("union LeftEvents, RightEvents", metadata).status, "Ready");
    assert.deepEqual(ap8Flow("union withsource=Source LeftEvents, RightEvents", metadata),
        { status: "Unsupported", columns: [] });
});

test("AP8 shared lexical work cannot leak schemas or mutable public analysis across editors", () => {
    const query = "LeftEvents | join RightEvents on Key | project Rem";
    assert.equal(ap8Suggest(query).suffix, "oteOnly");
    const publicAnalysis = language.analyze(query);
    publicAnalysis.tokens.length = 0;
    assert.equal(ap8Diagnose(query).structureStatus, "Balanced");
    assert.equal(ap8Suggest(query, { ...ap8Catalog, workspaceKey: "another-workspace" }), null);
    assert.equal(ap8Suggest(query).suffix, "oteOnly");
    const changed = { ...ap8Catalog, schemas: ap8Catalog.schemas.map(schema => ({
        ...schema, columns: schema.columns.filter(c => c.name !== "RemoteOnly")
    })) };
    assert.equal(ap8Suggest(query, changed), null);
    assert.equal(ap8Suggest(query).suffix, "oteOnly");
});

test("AP8 changing exact 64 KiB multi-source drafts meet the unchanged 50 ms p95 budget", t => {
    let body = "let T0 = union LeftEvents, RightEvents;";
    let index = 0;
    while (body.length < 64000) body += `let T${++index} = T${index - 1};`;
    const tail = ` T${index} | join (RightEvents | project Key, RemoteOnly) on Key | project Lef`;
    const run = number => {
        const header = `//${String(number).padStart(4, "0")}\n`;
        const query = header + body + " ".repeat(65536 - header.length - body.length - tail.length) + tail;
        assert.equal(Buffer.byteLength(query, "utf8"), 65536);
        const suggestion = ap8Suggest(query);
        const report = language.diagnostics(query, realEntries, ap8Tables, ap8Catalog, query.length);
        assert.equal(suggestion.suffix, "tOnly");
        assert.equal(report.diagnostics.length, 0);
    };
    for (let count = 0; count < 5; count++) run(count);
    const times = [];
    for (let count = 5; count < 35; count++) {
        const start = performance.now();
        run(count);
        times.push(performance.now() - start);
    }
    times.sort((a, b) => a - b);
    t.diagnostic(`Node ${process.version}; changing 64 KiB join/union completion + diagnostics; 30 iterations; p95=${times[28].toFixed(2)} ms`);
    assert.ok(times[28] <= 50, `p95 ${times[28].toFixed(2)} ms exceeds 50 ms`);
});
