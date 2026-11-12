(() => {
    const normalizeEntries = entries => {
        if (!Array.isArray(entries) || entries.length === 0) {
            throw new Error("The KQL dictionary and each next list must be non-empty arrays.");
        }
        const words = new Set();
        return entries.map(value => {
            const entry = typeof value === "string" ? { word: value } : value;
            if (!entry || typeof entry !== "object" || typeof entry.word !== "string" ||
                !/^[a-z][a-z0-9_-]*$/.test(entry.word) || words.has(entry.word)) {
                throw new Error("Each KQL dictionary entry must have a unique, lowercase word.");
            }
            words.add(entry.word);
            if (entry.contexts !== undefined && (!Array.isArray(entry.contexts) || !entry.contexts.length ||
                entry.contexts.some(value => !["statement", "source", "operator", "expression"].includes(value)) ||
                new Set(entry.contexts).size !== entry.contexts.length))
                throw new Error(`Invalid contexts for KQL word '${entry.word}'.`);
            if (entry.resultColumns !== undefined && !["single", "multiple"].includes(entry.resultColumns))
                throw new Error(`Invalid resultColumns for KQL word '${entry.word}'.`);
            if (entry.info !== undefined && (typeof entry.info === "string" ? !entry.info.trim()
                : !entry.info || typeof entry.info !== "object" || Array.isArray(entry.info) ||
                    Object.keys(entry.info).length === 0 ||
                    Object.values(entry.info).some(text => typeof text !== "string" || !text.trim()))) {
                throw new Error(`Invalid info for KQL word '${entry.word}'.`);
            }
            return {
                word: entry.word,
                info: entry.info,
                contexts: entry.contexts ?? ["expression"],
                resultColumns: entry.resultColumns ?? "single",
                next: entry.next === undefined ? undefined : normalizeEntries(entry.next)
            };
        });
    };

    const wordCharacter = character => {
        const code = character.charCodeAt(0);
        return (code >= 65 && code <= 90) || (code >= 97 && code <= 122) || (code >= 48 && code <= 57) ||
            code === 95 || code === 36 || code === 45 || (code > 127 && /[\p{L}\p{N}]/u.test(character));
    };

    const tokenize = text => {
        const tokens = [];
        const trivia = [];
        let state = "code";
        let quote = "";
        let verbatim = false;
        let region;
        for (let index = 0; index < text.length; index++) {
            const character = text[index];
            const next = text[index + 1];
            if (state === "line") {
                region.end = index + 1;
                if (character === "\n") state = "code";
            } else if (state === "block") {
                region.end = index + 1;
                if (character === "*" && next === "/") {
                    region.end = index + 2;
                    state = "code";
                    index++;
                }
            } else if (state === "multiline") {
                region.end = index + 1;
                if (text.startsWith("```", index)) {
                    region.end = index + 3;
                    state = "code";
                    index += 2;
                }
            } else if (state === "string") {
                region.end = index + 1;
                if (!verbatim && character === "\\") {
                    region.end = Math.min(text.length, index + 2);
                    index++;
                } else if (character === quote) {
                    if (verbatim && next === quote) {
                        region.end = index + 2;
                        index++;
                    } else state = "code";
                }
            } else if (character === "/" && (next === "/" || next === "*")) {
                state = next === "/" ? "line" : "block";
                region = { kind: "comment", start: index, end: index + 2 };
                trivia.push(region);
                index++;
            } else if (text.startsWith("```", index)) {
                region = { kind: "literal", start: index, end: index + 3 };
                tokens.push(region);
                state = "multiline";
                index += 2;
            } else if (character === "'" || character === '"') {
                region = { kind: "literal", start: index, end: index + 1 };
                tokens.push(region);
                state = "string";
                quote = character;
                verbatim = text[index - 1] === "@";
            } else if (wordCharacter(character)) {
                const start = index;
                while (index + 1 < text.length && wordCharacter(text[index + 1])) index++;
                tokens.push({ kind: "word", text: text.slice(start, index + 1), start, end: index + 1 });
            } else if (!/\s/.test(character)) {
                tokens.push({ kind: "symbol", text: character, start: index, end: index + 1 });
            }
        }
        return { tokens, trivia, state, isCode: state === "code" };
    };

    const analyze = (text, cursor = text.length) => {
        if (typeof text !== "string" || !Number.isInteger(cursor) || cursor < 0 || cursor > text.length)
            throw new RangeError("KQL analysis requires text and a cursor within that text.");
        const parsed = tokenize(text.slice(0, cursor));
        const groups = [{ opener: null, start: 0, statementStart: 0, pipelineStart: 0 }];
        const mismatchedClosers = [];
        const pairs = { ")": "(", "]": "[", "}": "{" };
        for (const token of parsed.tokens) {
            if (token.kind !== "symbol") continue;
            const current = groups.at(-1);
            if (["(", "[", "{"].includes(token.text)) {
                groups.push({ opener: token.text, start: token.start, statementStart: token.end, pipelineStart: token.end });
            } else if (Object.hasOwn(pairs, token.text)) {
                if (groups.length > 1 && current.opener === pairs[token.text]) groups.pop();
                else mismatchedClosers.push(token);
            } else if (token.text === ";") {
                current.statementStart = current.pipelineStart = token.end;
            } else if (token.text === "|") {
                current.pipelineStart = token.end;
            }
        }
        const current = groups.at(-1);
        return {
            ...parsed, cursor, statementStart: current.statementStart, pipelineStart: current.pipelineStart,
            groups: groups.slice(1), mismatchedClosers
        };
    };

    let editorAnalysis = null;
    const analyzeEditorText = text => {
        if (editorAnalysis?.text === text) return editorAnalysis.parsed;
        const parsed = analyze(text);
        // Share lexical work only; workspace schemas and bindings are always resolved afresh.
        editorAnalysis = text.length <= 65536 ? { text, parsed } : null;
        return parsed;
    };

    const candidates = (tokens, entries) => {
        let scope = entries;
        const start = tokens.findLastIndex(token => token.text === "|" || token.text === ";") + 1;
        for (const token of tokens.slice(start)) {
            const entry = token.kind === "word" ? scope.find(item => item.word === token.text) : undefined;
            if (!entry?.next) return entries;
            scope = entry.next;
        }
        return scope;
    };

    const literalName = (token, text) => {
        const literal = text.slice(token.start, token.end);
        if (!/^(['"])(?:\\.|[^\\])*\1$/s.test(literal)) return null;
        return literal.slice(1, -1).replace(/\\(['"\\nrt])/g,
            (_, value) => ({ n: "\n", r: "\r", t: "\t" })[value] ?? value);
    };

    const identifierTokens = text => {
        const tokens = tokenize(text).tokens;
        const identifiers = [];
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index];
            if (token.kind === "word") identifiers.push(token);
            else if (token.text === "[" && tokens[index + 1]?.kind === "literal" && tokens[index + 2]?.text === "]") {
                const name = literalName(tokens[index + 1], text);
                if (name !== null) identifiers.push({
                    kind: "identifier", name, text: text.slice(token.start, tokens[index + 2].end),
                    start: token.start, end: tokens[index + 2].end
                });
                index += 2;
            }
        }
        return identifiers;
    };

    const sourceExpression = tokens => {
        const start = tokens.findLastIndex(token => token.text === ";") + 1;
        const statement = tokens.slice(start);
        if (statement[0]?.text !== "let") return statement;
        return (statement[1]?.kind === "word" || statement[1]?.opener === "[") &&
            statement[2]?.text === "=" ? statement.slice(3) : null;
    };

    const groupedTokens = parsed => {
        const root = { tokens: [], tabular: true };
        const stack = [root];
        const pairs = { ")": "(", "]": "[", "}": "{" };
        for (const token of parsed.tokens) {
            const frame = stack.at(-1);
            if (token.kind !== "symbol") {
                frame.tokens.push(token);
                continue;
            }
            if (["(", "[", "{"].includes(token.text)) {
                if (stack.length > 64) return null;
                const source = sourceExpression(frame.tokens);
                const current = frame.tokens.slice(frame.tokens.findLastIndex(token => token.text === ";") + 1);
                const child = { kind: "group", opener: token.text, tokens: [],
                    start: token.start, closed: false,
                    callee: frame.tokens.at(-1)?.kind === "word" ? frame.tokens.at(-1).text : null,
                    scalarSource: frame.tokens.at(-1)?.text !== "materialize" && source?.length === 0 &&
                        (frame.scalarSource || current[0]?.text === "let"),
                    tabular: token.text === "(" && (frame.tokens.at(-1)?.text === "materialize" ||
                        tabularArgument(source) ||
                        (frame.tabular && source?.length === 0)) };
                frame.tokens.push(child);
                stack.push(child);
            } else if (Object.hasOwn(pairs, token.text)) {
                if (stack.length === 1 || frame.opener !== pairs[token.text]) return null;
                frame.closed = true;
                frame.end = token.end;
                stack.pop();
            } else frame.tokens.push(token);
        }
        return { root, stack };
    };

    const identifierName = (tokens, text) => {
        if (tokens.length !== 1) return null;
        const token = tokens[0];
        if (token.kind === "word" && /^[\p{L}_][\p{L}\p{N}_]*$/u.test(token.text)) return token.text;
        if (token.opener === "[" && token.closed && token.tokens.length === 1 &&
            token.tokens[0].kind === "literal") return literalName(token.tokens[0], text);
        return null;
    };

    const splitTokens = (tokens, separator) => {
        const result = [[]];
        for (const token of tokens) {
            if (token.text === separator) result.push([]);
            else result.at(-1).push(token);
        }
        return result;
    };
    const unknownSchema = () => ({ status: "Unsupported", columns: [] });
    const knownSchema = schema => ["Ready", "Partial"].includes(schema.status);
    const completeTokens = tokens => tokens.length > 0 && tokens.every(token =>
        token.kind !== "group" || (token.closed && (token.tokens.length === 0 || completeTokens(token.tokens))));

    const tableOptions = (tokens, operator) => {
        const options = { kind: operator === "join" ? "innerunique" : "outer", source: null };
        const seen = new Set();
        let index = 0;
        while (tokens[index + 1]?.text === "=") {
            const name = tokens[index]?.text, value = tokens[index + 2];
            if (!value || seen.has(name)) return null;
            seen.add(name);
            if (name === "kind") {
                const kinds = operator === "join"
                    ? ["innerunique", "inner", "leftouter", "rightouter", "fullouter",
                        "leftsemi", "leftanti", "anti", "leftantisemi", "rightsemi", "rightanti", "rightantisemi"]
                    : ["inner", "outer"];
                if (!kinds.includes(value.text)) return null;
                options.kind = value.text;
            } else if (operator === "union" && name === "withsource") {
                options.source = value;
            } else if (!(operator === "union" && name === "isfuzzy" && value.text === "false")) return null;
            index += 3;
        }
        return { ...options, rest: tokens.slice(index) };
    };
    const tabularArgument = expression => {
        if (!expression) return false;
        const current = expression.slice(expression.findLastIndex(token => token.text === "|") + 1);
        const operator = current[0]?.text;
        if (!["join", "union"].includes(operator)) return false;
        const options = tableOptions(current.slice(1), operator);
        return options !== null && (options.rest.length === 0 ||
            (operator === "union" && options.rest.at(-1)?.text === ","));
    };
    const tableSchema = value => value?.kind === "table" ? value.schema : unknownSchema();
    const joinParts = tokens => {
        const on = tokens.findIndex(token => token.text === "on");
        const options = tableOptions(on < 0 ? tokens : tokens.slice(0, on), "join");
        return options ? { ...options, keys: on < 0 ? null : tokens.slice(on + 1) } : null;
    };
    const qualifiedKey = (tokens, text) => {
        if (!["$left", "$right"].includes(tokens[0]?.text) || tokens[1]?.text !== ".") return null;
        const name = identifierName(tokens.slice(2), text);
        return name === null ? null : { side: tokens[0].text, name };
    };
    const joinSchema = (left, tokens, text, resolveSource) => {
        const parts = joinParts(tokens);
        if (!parts || !parts.keys || !completeTokens(parts.rest) || !completeTokens(parts.keys)) return unknownSchema();
        if (left.status !== "Ready") return { status: left.status, columns: [] };
        const right = tableSchema(resolveSource(parts.rest));
        if (right.status !== "Ready") return { status: right.status, columns: [] };
        let partial = false;
        const conditions = splitTokens(parts.keys, ",").flatMap(item => splitTokens(item, "and"));
        for (const condition of conditions) {
            const same = identifierName(condition, text);
            let leftName = same, rightName = same;
            if (same === null) {
                const equals = condition.findIndex(token => token.text === "=");
                if (equals < 0 || condition[equals + 1]?.text !== "=") return unknownSchema();
                const first = qualifiedKey(condition.slice(0, equals), text);
                const second = qualifiedKey(condition.slice(equals + 2), text);
                if (!first || !second || first.side === second.side) return unknownSchema();
                leftName = (first.side === "$left" ? first : second).name;
                rightName = (first.side === "$right" ? first : second).name;
            }
            const a = left.columns.find(column => column.name === leftName);
            const b = right.columns.find(column => column.name === rightName);
            if (!a || !b || (a.type && b.type && a.type !== b.type)) return unknownSchema();
            if (!a.type || !b.type) partial = true;
        }
        const leftOnly = ["leftsemi", "leftanti", "anti", "leftantisemi"].includes(parts.kind);
        const rightOnly = ["rightsemi", "rightanti", "rightantisemi"].includes(parts.kind);
        const columns = leftOnly ? left.columns : rightOnly ? right.columns : [...left.columns, ...right.columns];
        if (columns.length > 1024) return unknownSchema();
        const reserved = new Set(columns.map(column => column.name)), used = new Set();
        return { status: partial ? "Partial" : "Ready", columns: columns.map(column => {
            let name = column.name;
            if (used.has(name)) {
                let suffix = 1;
                while (reserved.has(`${name}${suffix}`)) suffix++;
                name = `${name}${suffix}`;
                reserved.add(name);
            }
            used.add(name);
            return { ...column, name };
        }) };
    };
    const unionSchema = (left, tokens, text, resolveSource) => {
        const options = tableOptions(tokens, "union");
        if (!options) return unknownSchema();
        const legs = splitTokens(options.rest, ",");
        if (legs.some(leg => !completeTokens(leg))) return unknownSchema();
        const schemas = left ? [left] : [];
        for (const leg of legs) {
            const schema = tableSchema(resolveSource(leg));
            if (schema.status !== "Ready") return { status: schema.status, columns: [] };
            schemas.push(schema);
        }
        if (left && left.status !== "Ready") return { status: left.status, columns: [] };
        const grouped = new Map();
        for (const schema of schemas) {
            for (const column of schema.columns) {
                if (!grouped.has(column.name)) grouped.set(column.name, []);
                grouped.get(column.name).push(column);
            }
        }
        const names = [...grouped.keys()].filter(name => options.kind !== "inner" ||
            schemas.every(schema => schema.columns.some(column => column.name === name)));
        const columns = [], generated = new Set();
        let partial = false;
        for (const name of names) {
            const variants = grouped.get(name);
            const types = [...new Set(variants.map(column => column.type))];
            if (variants.length > 1 && types.includes(null)) { partial = true; continue; }
            if (types.length === 1) columns.push({ name, type: types[0] });
            else if (options.kind === "inner") partial = true;
            else {
                if (types.some(type => !["bool", "datetime", "decimal", "dynamic", "guid", "int",
                    "long", "real", "string", "timespan"].includes(type))) { partial = true; continue; }
                for (const type of types) {
                    const column = { name: `${name}_${type}`, type };
                    columns.push(column);
                    generated.add(column);
                }
            }
        }
        const reserved = new Set(columns.filter(column => !generated.has(column)).map(column => column.name));
        for (let index = 0; index < columns.length; index++) {
            const column = columns[index];
            if (!generated.has(column)) continue;
            let name = column.name, suffix = 1;
            while (reserved.has(name)) name = `${column.name}${suffix++}`;
            columns[index] = { ...column, name };
            reserved.add(name);
        }
        if (options.source) {
            const name = identifierName([options.source], text);
            if (name === null || reserved.has(name)) return unknownSchema();
            columns.unshift({ name, type: "string" });
        }
        if (columns.length > 1024) return unknownSchema();
        return { status: partial ? "Partial" : "Ready", columns };
    };
    const expressionType = (tokens, columns, text, bindings) => {
        if (tokens.length === 1 && ["true", "false"].includes(tokens[0].text)) return "bool";
        const name = identifierName(tokens, text);
        if (name !== null) {
            const column = columns.find(column => column.name === name);
            return column ? column.type : bindings?.get(name)?.type ?? null;
        }
        if (tokens.length === 1 && tokens[0].kind === "literal") return "string";
        return null;
    };
    const assignmentTokens = tokens => {
        const index = tokens.findIndex(token => token.text === "=");
        return index < 0 ? [tokens] : [tokens.slice(0, index), tokens.slice(index + 1)];
    };
    const countExpression = tokens => tokens.length === 2 && tokens[0].text === "count" &&
        tokens[1].opener === "(" && tokens[1].closed && tokens[1].tokens.length === 0;
    const uncertainResult = (tokens, entries, bindings) => tokens.some((token, index) =>
        (token.kind === "word" && tokens[index + 1]?.opener === "(" &&
            (bindings?.has(token.text) || !entries.some(entry => entry.word === token.text &&
                entry.contexts.includes("expression") && entry.resultColumns === "single"))) ||
        (token.kind === "group" && uncertainResult(token.tokens, entries, bindings)));
    const unresolvedExpression = (tokens, columns, text, entries, bindings) => {
        const name = identifierName(tokens, text);
        const boolean = tokens.length === 1 && ["true", "false"].includes(tokens[0].text);
        return uncertainResult(tokens, entries, bindings) ||
            (name !== null && !boolean && !columns.some(column => column.name === name) &&
                bindings?.get(name)?.kind !== "scalar");
    };

    const selectionColumns = (tokens, schema, text) => {
        const name = identifierName(tokens, text);
        if (name !== null) return { columns: schema.columns.filter(column => column.name === name),
            complete: schema.columns.some(column => column.name === name) };
        if (!tokens.length || tokens.some(token => token.kind !== "word" && token.text !== "*") ||
            !tokens.some(token => token.text === "*")) return null;
        if (tokens.some((token, index) => index > 0 && tokens[index - 1].end !== token.start)) return null;
        const pattern = tokens.map(token => token.text === "*" ? ".*" :
            token.text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("");
        const regex = new RegExp(`^${pattern}$`, "u");
        return { columns: schema.columns.filter(column => regex.test(column.name)), complete: schema.status === "Ready" };
    };

    const applyOperator = (schema, tokens, text, entries, bindings, resolveSource) => {
        if (tokens[0]?.text === "join") return joinSchema(schema, tokens.slice(1), text, resolveSource);
        if (tokens[0]?.text === "union") return unionSchema(schema, tokens.slice(1), text, resolveSource);
        if (!knownSchema(schema)) return schema;
        const operator = tokens[0]?.text;
        const args = tokens.slice(1);
        if (!completeTokens(args)) return unknownSchema();
        if (["where", "take", "limit", "sort", "order"].includes(operator)) {
            if (["sort", "order"].includes(operator) && (args[0]?.text !== "by" || args.length < 2))
                return unknownSchema();
            return schema;
        }
        const items = splitTokens(args, ",");
        if (items.some(item => !item.length)) return unknownSchema();
        const result = [];
        let partial = schema.status === "Partial";
        const add = column => {
            if (result.some(existing => existing.name === column.name)) return false;
            result.push({ ...column });
            return true;
        };
        if (["project-away", "project-keep", "project-reorder"].includes(operator)) {
            const selected = [];
            for (const item of items) {
                const match = selectionColumns(item, schema, text);
                if (!match) return unknownSchema();
                selected.push(...match.columns);
            }
            const names = new Set(selected.map(column => column.name));
            const columns = operator === "project-away" ? schema.columns.filter(column => !names.has(column.name)) :
                operator === "project-keep" ? schema.columns.filter(column => names.has(column.name)) :
                    [...new Map(selected.map(column => [column.name, column])).values(),
                        ...schema.columns.filter(column => !names.has(column.name))];
            return { status: schema.status, columns };
        }
        if (operator === "project-rename") {
            const changes = new Map();
            for (const item of items) {
                const assignment = assignmentTokens(item);
                if (assignment.length !== 2) return unknownSchema();
                const target = identifierName(assignment[0], text), source = identifierName(assignment[1], text);
                if (target === null || source === null || changes.has(source) ||
                    !schema.columns.some(column => column.name === source)) return unknownSchema();
                changes.set(source, target);
            }
            for (const column of schema.columns)
                if (!add({ ...column, name: changes.get(column.name) ?? column.name })) return unknownSchema();
        } else if (operator === "extend") {
            const columns = schema.columns.map(column => ({ ...column }));
            for (const item of items) {
                const assignment = assignmentTokens(item);
                if (assignment.length !== 2 || !completeTokens(assignment[1])) return unknownSchema();
                const name = identifierName(assignment[0], text);
                if (name === null || unresolvedExpression(assignment[1], columns, text, entries, bindings)) return unknownSchema();
                const column = { name, type: expressionType(assignment[1], columns, text, bindings) };
                const index = columns.findIndex(column => column.name === name);
                if (index >= 0) columns.splice(index, 1);
                columns.push(column);
            }
            return { status: schema.status, columns };
        } else if (["project", "distinct", "summarize"].includes(operator)) {
            const by = operator === "summarize" ? args.findIndex(token => token.text === "by") : -1;
            const sections = operator === "summarize"
                ? [...(by < 0 ? [] : [{ tokens: args.slice(by + 1), aggregate: false }]),
                    { tokens: by < 0 ? args : args.slice(0, by), aggregate: true }]
                : [{ tokens: args, aggregate: false }];
            for (const section of sections) {
                if (!section.tokens.length) {
                    if (section.aggregate && by >= 0) continue;
                    return unknownSchema();
                }
                for (const item of splitTokens(section.tokens, ",")) {
                    if (!item.length) return unknownSchema();
                    const assignment = assignmentTokens(item);
                    if (assignment.length === 2 && operator !== "distinct") {
                        const name = identifierName(assignment[0], text);
                        if (name === null || !completeTokens(assignment[1]) ||
                            unresolvedExpression(assignment[1], schema.columns, text, entries, bindings))
                            return unknownSchema();
                        const type = section.aggregate ? (countExpression(assignment[1]) ? "long" : null) :
                            expressionType(assignment[1], schema.columns, text, bindings);
                        if (!add({ name, type })) return unknownSchema();
                    } else if (assignment.length !== 1) return unknownSchema();
                    else if (section.aggregate) {
                        if (countExpression(item)) {
                            if (!add({ name: "count_", type: "long" })) return unknownSchema();
                        } else partial = true;
                    } else {
                        if (item.some(token => token.text === "*") &&
                            !(operator === "distinct" && item.length === 1)) return unknownSchema();
                        const match = selectionColumns(item, schema, text);
                        if (!match) { partial = true; continue; }
                        if (!match.complete) partial = true;
                        for (const column of match.columns) if (!add(column)) return unknownSchema();
                    }
                }
            }
        } else return unknownSchema();
        return { status: partial ? "Partial" : "Ready", columns: result };
    };

    const scalarExpression = (tokens, bindings, text, entries) => {
        if (!completeTokens(tokens) || uncertainResult(tokens, entries, bindings)) return false;
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index];
            const name = identifierName([token], text);
            if (name !== null && bindings.get(name)?.kind === "scalar" && tokens[index + 1]?.opener !== "(") continue;
            if (token.kind === "group") {
                if (tokens[index - 1]?.kind === "word" && token.opener === "(") continue;
                if (token.opener !== "(" || !scalarExpression(token.tokens, bindings, text, entries)) {
                    if (token.tokens.length || tokens[index - 1]?.kind !== "word") return false;
                }
            } else if (token.kind === "word") {
                if (tokens[index + 1]?.opener === "(") continue;
                if (/^\d+(?:ms|us|ns|ticks|[dhms])?$/.test(token.text) || ["true", "false"].includes(token.text)) continue;
                if (bindings.get(token.text)?.kind === "scalar") continue;
                if (!entries.some(entry => entry.word === token.text && entry.contexts.includes("expression"))) return false;
            } else if (token.kind === "symbol" && !["+", "-", "*", "/", "%", "=", "!", "<", ">", ".", ","].includes(token.text))
                return false;
        }
        return true;
    };

    const scopeBindings = (tokens, inherited, text, catalog, tableEntries, entries, depth) => {
        const statements = splitTokens(tokens, ";").slice(0, -1);
        if (!statements.length) return inherited;
        const bindings = new Map(inherited);
        const declared = new Set();
        for (const statement of statements) {
            if (statement[0]?.text !== "let" || statement[2]?.text !== "=") continue;
            const name = identifierName(statement.slice(1, 2), text);
            if (name === null) continue;
            const expression = statement.slice(3);
            let binding = { kind: "unknown" };
            if (!declared.has(name) && completeTokens(expression)) {
                const reference = identifierName(expression, text);
                if (depth < 64 && reference !== null && reference !== name && bindings.has(reference) &&
                    !["true", "false"].includes(expression[0].text)) binding = bindings.get(reference);
                else {
                    // A definition sees earlier bindings, never its own unfinished result.
                    const previous = bindings.get(name);
                    if (!previous) bindings.set(name, { kind: "unknown" });
                    binding = resolveExpression(expression, bindings, text, catalog, tableEntries, entries, depth + 1);
                    if (!previous) bindings.delete(name);
                }
            }
            bindings.set(name, binding);
            declared.add(name);
        }
        return bindings;
    };

    const resolveExpression = (tokens, bindings, text, catalog, tableEntries, entries, depth = 0) => {
        if (depth > 64 || !completeTokens(tokens)) return { kind: "unknown" };
        const segments = splitTokens(tokens, "|");
        const source = segments[0];
        const name = identifierName(source, text);
        let value;
        if (source.length === 1 && ["true", "false"].includes(source[0].text)) value = { kind: "scalar", type: "bool" };
        else if (name !== null && bindings.has(name)) value = bindings.get(name);
        else if (name !== null && tableEntries.some(entry => entry.word === name)) value = {
            kind: "table", schema: columnSchema(catalog.schemas?.find(schema => schema.tableName === name), {
                ...catalog, tableName: name
            })
        };
        else if (source[0]?.text === "union" && !bindings.has("union")) value = {
            kind: "table", schema: unionSchema(null, source.slice(1), text,
                leg => resolveExpression(leg, bindings, text, catalog, tableEntries, entries, depth + 1))
        };
        else {
            const materialized = source.length === 2 && source[0].text === "materialize" &&
                !bindings.has("materialize") && source[1].opener === "(";
            const group = materialized ? source[1] : source.length === 1 && source[0].opener === "(" ? source[0] : null;
            if (group) {
                const local = scopeBindings(group.tokens, bindings, text, catalog, tableEntries, entries, depth + 1);
                const expression = sourceExpression(group.tokens);
                value = expression ? resolveExpression(expression, local, text, catalog, tableEntries, entries, depth + 1) :
                    { kind: "unknown" };
                if (materialized && value.kind !== "table") value = { kind: "unknown" };
            } else if (source.some(token => token.opener === "{")) value = { kind: "function" };
            else if (name === null && scalarExpression(source, bindings, text, entries)) value = {
                kind: "scalar", type: source.length === 1 && /^\d+$/.test(source[0].text) ? "long" :
                    expressionType(source, [], text, bindings)
            };
            else value = { kind: "unknown" };
        }
        if (segments.length === 1) return value;
        if (value.kind !== "table") return { kind: "unknown" };
        let schema = value.schema;
        for (const segment of segments.slice(1)) schema = applyOperator(schema, segment, text, entries, bindings,
            leg => resolveExpression(leg, bindings, text, catalog, tableEntries, entries, depth + 1));
        return { kind: "table", schema };
    };

    const pipelineInput = (statement, bindings, text, catalog, tableEntries, entries) => {
        const end = statement.findLastIndex(token => token.text === "|");
        const value = resolveExpression(statement.slice(0, end), bindings, text, catalog, tableEntries, entries);
        return value.kind === "table" ? value.schema : unknownSchema();
    };

    const pipelineSchema = (text, catalog = {}, tableEntries = [], entries = []) => {
        const parsed = analyze(text);
        if (!parsed.isCode) return unknownSchema();
        const grouped = groupedTokens(parsed);
        if (!grouped || grouped.stack.length !== 1) return unknownSchema();
        const bindings = scopeBindings(grouped.root.tokens, new Map(), text, catalog, tableEntries, entries, 0);
        const statement = sourceExpression(grouped.root.tokens);
        return statement ? pipelineInput([...statement, { text: "|" }], bindings, text, catalog, tableEntries, entries) :
            unknownSchema();
    };

    const diagnostics = (text, entries = [], tableEntries = [], catalog = {}, cursor = null) => {
        if (cursor !== null && (!Number.isInteger(cursor) || cursor < 0 || cursor > text.length))
            throw new RangeError("KQL diagnostics require a cursor within the text.");
        const parsed = analyzeEditorText(text);
        const issues = [];
        let truncated = false;
        const add = issue => {
            if (issues.length < 12) issues.push(issue);
            else truncated = true;
        };
        for (const token of parsed.mismatchedClosers)
            add({ code: "UnexpectedCloser", severity: "error", start: token.start, end: token.end });
        for (const group of parsed.groups)
            add({ code: "OpenDelimiter", severity: "info", start: group.start, end: group.start + 1 });
        const openLiteral = ["string", "multiline"].includes(parsed.state);
        const openComment = parsed.state === "block";
        if (openLiteral || openComment) {
            const region = openLiteral ? parsed.tokens.at(-1) : parsed.trivia.at(-1);
            add({ code: openLiteral ? "OpenLiteral" : "OpenComment", severity: "info",
                start: region.start, end: region.end });
        }
        const structuralDraft = parsed.groups.length > 0 || openLiteral || openComment;
        let draft = structuralDraft;
        let schemaStatus = "NotApplicable";
        const result = () => ({
            structureStatus: parsed.mismatchedClosers.length ? "Invalid" : structuralDraft ? "Incomplete" : "Balanced",
            completeness: draft ? "Draft" : "Limited", schemaStatus, diagnostics: issues, truncated
        });
        if (draft || parsed.mismatchedClosers.length) return result();
        const grouped = groupedTokens(parsed);
        if (!grouped) { schemaStatus = "Unsupported"; return result(); }
        const tokens = grouped.root.tokens;
        let start = 0, end = tokens.length;
        for (let index = 0; index < tokens.length; index++) {
            if (tokens[index].text !== ";") continue;
            if (tokens[index].start >= (cursor ?? text.length)) { end = index; break; }
            start = index + 1;
        }
        if (cursor === null) {
            while (start === end && end > 0) {
                end--;
                start = tokens.slice(0, end).findLastIndex(token => token.text === ";") + 1;
            }
        }
        const bindings = scopeBindings(tokens.slice(0, start), new Map(), text, catalog, tableEntries, entries, 0);
        const statement = tokens.slice(start, end);
        if (statement[0]?.text === "let") {
            const name = identifierName(statement.slice(1, 2), text);
            if (name !== null && !bindings.has(name)) bindings.set(name, { kind: "unknown" });
        }
        const expression = sourceExpression(statement);
        if (!expression?.length) return result();
        const segments = splitTokens(expression, "|");
        const source = resolveExpression(segments[0], bindings, text, catalog, tableEntries, entries);
        if (source.kind !== "table") { schemaStatus = "Unsupported"; return result(); }
        let schema = source.schema;
        const check = (reference, allowScalar = true) => {
            const name = identifierName(reference, text);
            if (name === null || (allowScalar && (bindings.get(name)?.kind === "scalar" ||
                (reference[0].kind === "word" && ["true", "false", "null"].includes(name)))) ||
                schema.columns.some(column => column.name === name))
                return;
            const first = reference[0], last = reference.at(-1);
            if (cursor !== null && cursor > first.start && cursor <= last.end) {
                draft = true;
                return;
            }
            add({ code: "UnknownColumn", severity: "warning", start: first.start, end: last.end, name });
        };
        for (const segment of segments.slice(1)) {
            if (schema.status === "Ready" && completeTokens(segment.slice(1))) {
                const operator = segment[0]?.text;
                const args = segment.slice(1);
                if (operator === "extend") {
                    for (const item of splitTokens(args, ",")) {
                        if (schema.status === "Ready") check(assignmentTokens(item).at(-1));
                        schema = applyOperator(schema, [segment[0], ...item], text, entries, bindings);
                    }
                    continue;
                }
                if (["project", "project-rename", "distinct"].includes(operator)) {
                    const targets = new Set(splitTokens(args, ",").map(item => assignmentTokens(item))
                        .filter(assignment => assignment.length === 2).map(assignment => identifierName(assignment[0], text)));
                    for (const item of splitTokens(args, ",")) {
                        const assignment = assignmentTokens(item);
                        if (operator === "project-rename" || !targets.has(identifierName(assignment.at(-1), text)))
                            check(assignment.at(-1), operator !== "project-rename");
                    }
                } else if (operator === "where") {
                    const comparison = args.findIndex(token => ["=", "!", "<", ">"].includes(token.text));
                    check(comparison < 0 ? args : args.slice(0, comparison));
                } else if (operator === "summarize") {
                    const by = args.findIndex(token => token.text === "by");
                    if (by >= 0)
                        for (const item of splitTokens(args.slice(by + 1), ",")) check(assignmentTokens(item).at(-1));
                } else if (["sort", "order"].includes(operator) && args[0]?.text === "by") {
                    for (const item of splitTokens(args.slice(1), ",")) {
                        const modifier = item.findIndex(token => ["asc", "desc", "nulls"].includes(token.text));
                        check(modifier < 0 ? item : item.slice(0, modifier));
                    }
                }
            }
            schema = applyOperator(schema, segment, text, entries, bindings,
                leg => resolveExpression(leg, bindings, text, catalog, tableEntries, entries));
        }
        schemaStatus = schema.status;
        return result();
    };

    const completionContext = (parsed, text, entries, tableEntries, catalog) => {
        const grouped = groupedTokens(parsed);
        if (!grouped) return { kind: "none" };
        const { root, stack } = grouped;
        if (stack.some(frame => frame.opener === "{")) return { kind: "none" };
        if (stack.at(-1).opener && stack.at(-1).opener !== "(") return { kind: "none" };
        const frame = stack.findLast(item => item.tabular);
        let bindings = new Map();
        for (const scope of stack.filter(item => item.tabular)) {
            if (scope.callee === "materialize" && bindings.has("materialize")) return { kind: "none" };
            bindings = scopeBindings(scope.tokens, bindings, text, catalog, tableEntries, entries, 0);
        }
        const statement = sourceExpression(frame.tokens);
        if (!statement) return { kind: "none" };
        const pipe = statement.findLastIndex(token => token.text === "|");
        const current = statement.slice(pipe + 1);
        const sourceContext = () => ({ kind: "source", statement: false, definition: false, bindings });
        if (frame === stack.at(-1) && ((pipe < 0 && current[0]?.text === "union" && !bindings.has("union")) ||
            (pipe >= 0 && current[0]?.text === "union"))) {
            const options = tableOptions(current.slice(1), "union");
            if (options && (options.rest.length === 0 || options.rest.at(-1)?.text === ",")) return sourceContext();
            return { kind: "none" };
        }
        if (frame === stack.at(-1) && pipe >= 0 && current[0]?.text === "join") {
            const parts = joinParts(current.slice(1));
            if (!parts) return { kind: "none" };
            if (parts.keys === null) {
                if (!parts.rest.length) return sourceContext();
                return { kind: "next", scope: entries.filter(entry => entry.word === "on") };
            }
            const left = pipelineInput(statement, bindings, text, catalog, tableEntries, entries);
            const right = tableSchema(resolveExpression(parts.rest, bindings, text, catalog, tableEntries, entries));
            if (left.status !== "Ready" || right.status !== "Ready") return { kind: "none" };
            const key = parts.keys.slice(Math.max(parts.keys.findLastIndex(token => token.text === ","),
                parts.keys.findLastIndex(token => token.text === "and")) + 1);
            const side = key.at(-2)?.text;
            if (key.at(-1)?.text === "." && ["$left", "$right"].includes(side)) {
                let columns = (side === "$left" ? left : right).columns;
                const equals = key.findIndex(token => token.text === "=");
                if (equals >= 0) {
                    if (key[equals + 1]?.text !== "=") return { kind: "none" };
                    const other = qualifiedKey(key.slice(0, equals), text);
                    if (!other || other.side === side) return { kind: "none" };
                    const column = (other.side === "$left" ? left : right).columns.find(item => item.name === other.name);
                    if (!column) return { kind: "none" };
                    columns = columns.filter(item => !column.type || !item.type || column.type === item.type);
                }
                return { kind: "columns", columns };
            }
            if (!key.length) return { kind: "columns", columns: left.columns.filter(column =>
                right.columns.some(other => other.name === column.name &&
                    (!column.type || !other.type || column.type === other.type))) };
            return { kind: "none" };
        }
        if (pipe < 0) {
            if (frame !== stack.at(-1) || statement.length) return { kind: "none" };
            const definition = frame.scalarSource ||
                frame.tokens[frame.tokens.findLastIndex(token => token.text === ";") + 1]?.text === "let";
            return { kind: "source", statement: frame === root && !definition, definition, bindings };
        }
        if (!current.length && frame === stack.at(-1)) return { kind: "operator" };
        const operator = current[0];
        if (operator?.kind !== "word" || !entries.some(entry => entry.word === operator.text &&
            entry.contexts.includes("operator"))) return { kind: "none" };
        if (["order", "sort"].includes(operator.text) && current[1]?.text !== "by")
            return frame === stack.at(-1) ? { kind: "next", scope: candidates(current, entries) } : { kind: "none" };
        const active = frame === stack.at(-1) ? current.slice(1) : stack.at(-1).tokens;
        if (active.at(-1)?.text === ".") return { kind: "none" };
        const supportsColumns = ["where", "project", "project-away", "project-keep", "project-reorder",
            "project-rename", "extend", "distinct", "summarize", "order", "sort", "top"].includes(operator.text);
        let schema = supportsColumns ? pipelineInput(statement, bindings, text, catalog, tableEntries, entries) : unknownSchema();
        if (operator.text === "extend" && current.some(token => token.text === ",")) {
            const completed = current.slice(1, current.findLastIndex(token => token.text === ","));
            schema = applyOperator(schema, [operator, ...completed], text, entries, bindings);
        }
        const currentItem = current.slice(current.findLastIndex(token => token.text === ",") + 1);
        const afterEquals = currentItem.some(token => token.text === "=");
        const showColumns = operator.text === "project-rename" ? afterEquals :
            operator.text === "extend" ? afterEquals :
                operator.text === "top" ? current.some(token => token.text === "by") : true;
        const next = frame === stack.at(-1) ? candidates(current, entries) : entries;
        const last = active.at(-1);
        const member = last && (last.kind === "group" || last.kind === "literal" ||
            (last.kind === "word" && !["by", "and", "or", "not", "in", "between"].includes(last.text)));
        return { kind: "expression", member, scope: next === entries ? [] : next,
            columns: showColumns ? schema.columns : [], schemaStatus: schema.status, bindings,
            scalars: operator.text === "project-rename" || (operator.text === "extend" && !afterEquals) ? [] : [...bindings]
                .filter(([name, value]) => value.kind === "scalar" && !schema.columns.some(column => column.name === name)) };
    };

    const quoteIdentifier = (name, entries) => /^[\p{L}_][\p{L}\p{N}_]*$/u.test(name) &&
        !entries.some(entry => entry.word === name.toLowerCase()) ? name :
        `['${name.replace(/\\/g, "\\\\").replace(/'/g, "\\'").replace(/\n/g, "\\n").replace(/\r/g, "\\r").replace(/\t/g, "\\t")}']`;

    const completion = (input, entries, tableEntries = [], catalog = {}) => {
        const end = input.selectionStart;
        if (input.disabled || input.readOnly || end !== input.selectionEnd) return null;
        if (input.value.slice(end).split("\n", 1)[0].trim()) return null;
        const before = input.value.slice(0, end);
        const protectedPrefix = before.match(/\['((?:\\.|[^'\\])*)$/);
        const prefix = protectedPrefix ? protectedPrefix[1].replace(/\\(['\\])/g, "$1") :
            before.match(/[\p{L}\p{N}_$-]+$/u)?.[0] ?? "";
        const start = end - (protectedPrefix?.[0].length ?? prefix.length);
        let parsed = analyzeEditorText(protectedPrefix ? before.slice(0, start) : before);
        if (!protectedPrefix && prefix) {
            const last = parsed.tokens.at(-1);
            parsed = last?.kind === "word" && last.start === start && last.end === end
                ? { ...parsed, tokens: parsed.tokens.slice(0, -1) }
                : analyzeEditorText(before.slice(0, start));
        }
        if (!parsed.isCode || (!protectedPrefix && prefix && !/^[\p{L}_][\p{L}\p{N}_-]*$/u.test(prefix))) return null;
        const context = completionContext(parsed, before, entries, tableEntries, catalog);
        if (protectedPrefix && context.member) return null;
        const bindingEntry = ([word, value]) => ({
            word, kind: value.kind === "table" ? "tableVariable" : "scalarVariable",
            info: value.type ? `${word}: ${value.type}` : word
        });
        let scope = [];
        if (context.kind === "source") scope = [
            ...entries.filter(entry => !context.bindings.has(entry.word) && (entry.contexts.includes("source") ||
                (context.statement && entry.contexts.includes("statement")) ||
                (context.definition && entry.contexts.includes("expression")))),
            ...tableEntries.filter(entry => !context.bindings.has(entry.word)).map(entry => ({ ...entry, kind: "table" })),
            ...[...context.bindings].filter(([, value]) => value.kind === "table" ||
                (context.definition && value.kind === "scalar")).map(bindingEntry)
        ];
        else if (context.kind === "operator") scope = entries.filter(entry => entry.contexts.includes("operator"));
        else if (context.kind === "next") scope = context.scope;
        else if (context.kind === "columns") scope = context.columns.map(column => ({
            word: column.name, kind: "column", info: column.type ? `${column.name}: ${column.type}` : column.name
        }));
        else if (context.kind === "expression") {
            scope = [...entries.filter(entry => entry.contexts.includes("expression") && !context.bindings.has(entry.word)),
                ...context.scope, ...context.scalars.map(bindingEntry)];
            scope = [...scope, ...context.columns.map(column => ({
                word: column.name, kind: "column", info: column.type ? `${column.name}: ${column.type}` : column.name
            }))];
        }
        if (!prefix && context.kind !== "next") return null;
        const matches = [...new Map(scope.filter(entry => (!protectedPrefix || entry.kind) &&
            entry.word.startsWith(prefix)).map(entry => [`${entry.kind ?? "keyword"}:${entry.word}`, entry])).values()];
        if (matches.length !== 1) return null;
        const entry = matches[0];
        const insertion = entry.kind ? quoteIdentifier(entry.word, entries) : entry.word;
        if (!protectedPrefix && insertion === prefix) return null;
        const replaceStart = !protectedPrefix && insertion.startsWith(prefix) ? end : start;
        const suffix = replaceStart === end ? insertion.slice(prefix.length) : insertion;
        if (input.maxLength >= 0 && input.value.length - (end - replaceStart) + suffix.length > input.maxLength) return null;
        return { entry, suffix, replaceStart };
    };

    const normalizeTableSchema = (schema, expected) => {
        const statuses = ["Ready", "Empty", "Unavailable", "Failed", "Loading"];
        if (!schema || typeof schema !== "object" || typeof schema.workspaceKey !== "string" ||
            !schema.workspaceKey.trim() || schema.workspaceKey.toLowerCase() !== expected.workspaceKey.toLowerCase() ||
            schema.tableName !== expected.tableName || !Number.isSafeInteger(schema.catalogVersion) ||
            schema.catalogVersion !== expected.catalogVersion || !statuses.includes(schema.status) ||
            !Array.isArray(schema.columns)) {
            throw new Error("The KQL schema response has an invalid source, version or status.");
        }
        const names = new Set();
        const columns = schema.columns.map(column => {
            if (!column || typeof column.name !== "string" || !column.name.trim() ||
                typeof column.type !== "string" || !column.type.trim() || names.has(column.name)) {
                throw new Error("The KQL schema response contains invalid or duplicate columns.");
            }
            names.add(column.name);
            return { name: column.name, type: column.type };
        });
        if ((schema.status === "Ready") !== (columns.length > 0))
            throw new Error("The KQL schema response columns do not match its status.");
        return {
            workspaceKey: schema.workspaceKey, tableName: schema.tableName,
            catalogVersion: schema.catalogVersion, status: schema.status, columns
        };
    };

    const columnSchema = (schema, context) => {
        if (context.workspaceCount !== 1)
            return { status: context.workspaceCount > 1 ? "MultipleWorkspaces" : "NoWorkspace", columns: [] };
        if (!schema) return { status: "Unknown", columns: [] };
        if (schema.workspaceKey.toLowerCase() !== context.workspaceKey.toLowerCase() ||
            schema.tableName !== context.tableName || schema.catalogVersion !== context.catalogVersion)
            return { status: "Stale", columns: [] };
        const result = normalizeTableSchema(schema, context);
        return { status: result.status, columns: result.columns };
    };

    globalThis.kqlLanguage = Object.freeze({
        normalizeEntries, tokenize, analyze, candidates, completion, diagnostics, normalizeTableSchema, columnSchema, identifierTokens,
        pipelineSchema
    });
})();
