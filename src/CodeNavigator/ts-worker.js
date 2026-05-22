const path = require('path');
const fs = require('fs');
const readline = require('readline');

let ts = null;

function findTypescript() {
    const nodePath = process.env.NODE_PATH;
    if (nodePath) {
        const probe = path.join(nodePath, 'typescript');
        if (fs.existsSync(probe)) return require(probe);
        const probe2 = path.join(nodePath, 'typescript', 'typescript.js');
        if (fs.existsSync(probe2)) return require(probe2);
    }
    let dir = process.cwd();
    while (true) {
        const probe = path.join(dir, 'node_modules', 'typescript');
        if (fs.existsSync(probe)) return require(probe);
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    try {
        return require(require.resolve('typescript'));
    } catch {}
    return null;
}

function ensureTs() {
    if (!ts) {
        ts = findTypescript();
        if (!ts) throw new Error('TypeScript module not found. Set NODE_PATH or cd to a project with node_modules/typescript.');
    }
    return ts;
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
    let req;
    try { req = JSON.parse(line); }
    catch { writeError(null, 'Parse error'); return; }
    try {
        ensureTs();
        handle(req);
    } catch (e) { writeError(req.id, e.message); }
});

function handle(req) {
    const { id, method, args } = req;
    let result;
    switch (method) {
        case 'symbols': result = symbols(args); break;
        case 'definition': result = definition(args); break;
        case 'hover': result = hover(args); break;
        case 'references': result = references(args); break;
        default: writeError(id, 'Unknown method: ' + method); return;
    }
    writeResult(id, result);
}

function getScriptKind(p) {
    if (p.endsWith('.tsx')) return ts.ScriptKind.TSX;
    if (p.endsWith('.jsx')) return ts.ScriptKind.JSX;
    if (p.endsWith('.mts') || p.endsWith('.cts')) return ts.ScriptKind.TS;
    if (p.endsWith('.mjs') || p.endsWith('.cjs')) return ts.ScriptKind.JS;
    if (p.endsWith('.ts')) return ts.ScriptKind.TS;
    if (p.endsWith('.js')) return ts.ScriptKind.JS;
    return ts.ScriptKind.TS;
}

function parseFile(filePath, content) {
    const sk = getScriptKind(filePath);
    return ts.createSourceFile(filePath, content, ts.ScriptTarget.Latest, true, sk);
}

function lineNum(sf, pos) {
    return ts.getLineAndCharacterOfPosition(sf, pos).line + 1;
}

// ── symbols ─────────────────────────────────────────────────────────────────

function symbols(args) {
    const { path, content } = args;
    const sf = parseFile(path, content);
    const result = [];

    function visit(node, depth) {
        const l = lineNum(sf, node.getStart());
        const endL = lineNum(sf, node.getEnd());

        if (ts.isInterfaceDeclaration(node)) {
            const members = [];
            for (const m of node.members) {
                const ml = lineNum(sf, m.getStart());
                const mend = lineNum(sf, m.getEnd());
                if (ts.isMethodSignature(m)) {
                    members.push({ kind: 'method', name: m.name.getText(), line: ml, lineEnd: mend });
                } else if (ts.isPropertySignature(m)) {
                    members.push({ kind: 'property', name: m.name.getText(), type: m.type ? m.type.getText() : 'any', line: ml, lineEnd: mend });
                }
            }
            result.push({
                kind: 'interface',
                name: node.name.text,
                line: l,
                lineEnd: endL,
                typeParams: node.typeParameters ? node.typeParameters.map(tp => tp.getText()).join(', ') : undefined,
                members
            });
        } else if (ts.isClassDeclaration(node) && node.name) {
            const members = [];
            for (const m of node.members) {
                const ml = lineNum(sf, m.getStart());
                const mend = lineNum(sf, m.getEnd());
                const mods = m.modifiers ? m.modifiers.map(mo => mo.getText()).join(' ') : '';
                if (ts.isMethodDeclaration(m) || ts.isGetAccessorDeclaration(m) || ts.isSetAccessorDeclaration(m)) {
                    const sig = m.name ? m.name.getText() + '(' + (m.parameters || []).map(p => p.name.getText() + (p.type ? ': ' + p.type.getText() : '')).join(', ') + ')' : '(anonymous)';
                    members.push({ kind: 'method', name: m.name ? m.name.getText() : '(anon)', signature: sig, modifiers: mods, line: ml, lineEnd: mend });
                } else if (ts.isPropertyDeclaration(m)) {
                    members.push({ kind: 'property', name: m.name.getText(), type: m.type ? m.type.getText() : 'any', modifiers: mods, line: ml, lineEnd: mend });
                } else if (ts.isConstructorDeclaration(m)) {
                    members.push({ kind: 'constructor', line: ml, lineEnd: mend });
                }
            }
            result.push({
                kind: 'class',
                name: node.name.text,
                line: l,
                lineEnd: endL,
                typeParams: node.typeParameters ? node.typeParameters.map(tp => tp.getText()).join(', ') : undefined,
                members
            });
        } else if (ts.isFunctionDeclaration(node) && node.name) {
            result.push({
                kind: 'function',
                name: node.name.text,
                line: l,
                lineEnd: endL,
                signature: buildSig(node)
            });
        } else if (ts.isEnumDeclaration(node)) {
            const members = [];
            for (const m of node.members) {
                const ml = lineNum(sf, m.getStart());
                members.push({ name: m.name.getText(), line: ml });
            }
            result.push({ kind: 'enum', name: node.name.text, line: l, lineEnd: endL, members });
        } else if (ts.isTypeAliasDeclaration(node)) {
            result.push({ kind: 'type', name: node.name.text, line: l, lineEnd: endL, typeDef: node.type ? node.type.getText().substring(0, 100) : 'unknown' });
        } else if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && (ts.isArrowFunction(node.initializer) || ts.isFunctionExpression(node.initializer))) {
            result.push({ kind: 'variable_fn', name: node.name.text, line: l, lineEnd: endL });
        }

        ts.forEachChild(node, n => visit(n, depth + 1));
    }

    ts.forEachChild(sf, n => visit(n, 0));
    return { _symbols: result };
}

function buildSig(node) {
    const params = (node.parameters || []).map(p =>
        p.name.getText() + (p.type ? ': ' + p.type.getText() : '')
    ).join(', ');
    const ret = node.type ? node.type.getText() : 'void';
    return '(' + params + '): ' + ret;
}

// ── definition ──────────────────────────────────────────────────────────────

function definition(args) {
    const { path, content, symbol } = args;
    const sf = parseFile(path, content);
    const search = symbol;

    function searchNode(node) {
        if (ts.isInterfaceDeclaration(node) && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isClassDeclaration(node) && node.name && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isFunctionDeclaration(node) && node.name && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isEnumDeclaration(node) && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isTypeAliasDeclaration(node) && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }

        if (ts.isMethodDeclaration(node) && node.name && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isPropertyDeclaration(node) && node.name && (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name)) && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isGetAccessorDeclaration(node) && node.name && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }
        if (ts.isSetAccessorDeclaration(node) && node.name && node.name.text === search) {
            return { file: path, line: lineNum(sf, node.getStart()) };
        }

        let result = null;
        ts.forEachChild(node, child => {
            const r = searchNode(child);
            if (r) result = r;
        });
        return result;
    }

    const found = searchNode(sf);
    return { _def: found || null };
}

// ── hover ───────────────────────────────────────────────────────────────────

function hover(args) {
    const { path, content, symbol } = args;
    const sf = parseFile(path, content);
    const search = symbol;

    function searchNode(node) {
        if (ts.isInterfaceDeclaration(node) && node.name.text === search) {
            const sig = 'interface ' + node.name.text;
            const typeParams = node.typeParameters ? '<' + node.typeParameters.map(tp => tp.getText()).join(', ') + '>' : '';
            return { signature: sig + typeParams, doc: getJsDoc(node) };
        }
        if (ts.isClassDeclaration(node) && node.name && node.name.text === search) {
            const heritage = node.heritageClauses ? node.heritageClauses.map(h => h.getText()).join(' ') : '';
            const sig = 'class ' + node.name.text + (heritage ? ' ' + heritage : '');
            return { signature: sig, doc: getJsDoc(node) };
        }
        if (ts.isFunctionDeclaration(node) && node.name && node.name.text === search) {
            return { signature: 'function ' + buildSig(node), doc: getJsDoc(node) };
        }
        if (ts.isEnumDeclaration(node) && node.name.text === search) {
            return { signature: 'enum ' + node.name.text, doc: getJsDoc(node) };
        }
        if (ts.isTypeAliasDeclaration(node) && node.name.text === search) {
            const sig = 'type ' + node.name.text + (node.type ? ' = ' + node.type.getText().substring(0, 120) : '');
            return { signature: sig, doc: getJsDoc(node) };
        }
        if (ts.isMethodDeclaration(node) && node.name && node.name.text === search) {
            return { signature: buildSig(node), doc: getJsDoc(node) };
        }
        if (ts.isPropertyDeclaration(node) && node.name && (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name)) && node.name.text === search) {
            const sig = (node.type ? node.type.getText() : 'any') + ' ' + node.name.text;
            return { signature: sig, doc: getJsDoc(node) };
        }
        if (ts.isGetAccessorDeclaration(node) && node.name && node.name.text === search) {
            return { signature: 'get ' + node.name.text + (node.type ? ': ' + node.type.getText() : ''), doc: getJsDoc(node) };
        }
        if (ts.isSetAccessorDeclaration(node) && node.name && node.name.text === search) {
            return { signature: 'set ' + node.name.text, doc: getJsDoc(node) };
        }

        let result = null;
        ts.forEachChild(node, child => {
            const r = searchNode(child);
            if (r) result = r;
        });
        return result;
    }

    const found = searchNode(sf);
    return { _hover: found || null };
}

function getJsDoc(node) {
    const jsDocs = node.jsDoc || [];
    if (jsDocs.length === 0) return undefined;
    return jsDocs.map(d => d.comment || '').filter(Boolean).join('\n') || undefined;
}

// ── references ──────────────────────────────────────────────────────────────

function references(args) {
    const { path, content, symbol } = args;
    const sf = parseFile(path, content);
    const results = [];
    const lower = symbol.toLowerCase();

    function collect(node) {
        if (ts.isIdentifier(node) && node.text.toLowerCase() === lower) {
            const l = lineNum(sf, node.getStart());
            const start = node.getStart();
            const end = node.getEnd();
            const snippet = content.substring(start, Math.min(start + 60, content.length)).replace(/\n/g, ' ');
            results.push({ file: path, line: l, snippet });
        }
        ts.forEachChild(node, collect);
    }

    ts.forEachChild(sf, collect);
    return { symbol, file: path, _references: results };
}

// ── Wire helpers ────────────────────────────────────────────────────────────

function writeResult(id, result) {
    process.stdout.write(JSON.stringify({ id, result }) + '\n');
}

function writeError(id, message) {
    process.stdout.write(JSON.stringify({ id, error: message }) + '\n');
}
