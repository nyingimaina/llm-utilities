const path = require('path');
const fs = require('fs');
const readline = require('readline');

let ts = null;

function findTypescript() {
    const nodePath = process.env.NODE_PATH;
    if (nodePath) {
        const probe = path.join(nodePath, 'typescript');
        if (fs.existsSync(probe)) {
            return require(probe);
        }
        const probe2 = path.join(nodePath, 'typescript', 'typescript.js');
        if (fs.existsSync(probe2)) {
            return require(probe2);
        }
    }

    let dir = process.cwd();
    while (true) {
        const probe = path.join(dir, 'node_modules', 'typescript');
        if (fs.existsSync(probe)) {
            return require(probe);
        }
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }

    try {
        const resolved = require.resolve('typescript');
        return require(resolved);
    } catch {}

    return null;
}

function ensureTs() {
    if (!ts) {
        ts = findTypescript();
        if (!ts) {
            throw new Error('TypeScript module not found. Set NODE_PATH or cd to a project with node_modules/typescript.');
        }
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
        case 'listFunctions': result = listFunctions(args); break;
        case 'getFunction': result = getFunction(args); break;
        case 'getFunctions': result = getFunctions(args); break;
        case 'summarize': result = summarize(args); break;
        default: writeError(id, 'Unknown method: ' + method); return;
    }
    writeResult(id, result);
}

function getScriptKind(path) {
    if (path.endsWith('.tsx')) return ts.ScriptKind.TSX;
    if (path.endsWith('.jsx')) return ts.ScriptKind.JSX;
    if (path.endsWith('.mts') || path.endsWith('.cts')) return ts.ScriptKind.TS;
    if (path.endsWith('.mjs') || path.endsWith('.cjs')) return ts.ScriptKind.JS;
    if (path.endsWith('.ts')) return ts.ScriptKind.TS;
    if (path.endsWith('.js')) return ts.ScriptKind.JS;
    return ts.ScriptKind.TS;
}

function parseFile(filePath, content) {
    const scriptKind = getScriptKind(filePath);
    return ts.createSourceFile(filePath, content, ts.ScriptTarget.Latest, true, scriptKind);
}

function lineNum(sf, pos) {
    return ts.getLineAndCharacterOfPosition(sf, pos).line + 1;
}

// ── listFunctions ──────────────────────────────────────────────────────────

function listFunctions(args) {
    const { path, content } = args;
    const sf = parseFile(path, content);
    const funcs = [];

    function collect(node) {
        if (ts.isFunctionDeclaration(node) && node.name) {
            funcs.push(makeFuncInfo(sf, node, node.name.text));
        } else if (ts.isConstructorDeclaration(node)) {
            funcs.push(makeFuncInfo(sf, node, "constructor"));
        } else if ((ts.isMethodDeclaration(node) || ts.isGetAccessorDeclaration(node) ||
                     ts.isSetAccessorDeclaration(node)) && node.name) {
            funcs.push(makeFuncInfo(sf, node, node.name.text));
        } else if (ts.isVariableDeclaration(node) && node.initializer) {
            const init = node.initializer;
            if ((ts.isArrowFunction(init) || ts.isFunctionExpression(init)) && node.name &&
                (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name) || ts.isNumericLiteral(node.name))) {
                funcs.push(makeFuncInfo(sf, init, node.name.text));
            }
        }
        ts.forEachChild(node, collect);
    }

    ts.forEachChild(sf, collect);
    return funcs;
}

function makeFuncInfo(sf, node, name) {
    const startL = lineNum(sf, node.getStart());
    const endL = node.body ? lineNum(sf, node.body.getEnd()) : lineNum(sf, node.getEnd());
    const sig = buildSignature(node);
    return { name, lineStart: startL, lineEnd: endL, signature: sig };
}

function buildSignature(node) {
    const params = node.parameters || [];
    const paramStrs = params.map(p => {
        const n = p.name.getText();
        const t = p.type ? p.type.getText() : 'any';
        const opt = p.questionToken ? '?' : '';
        const dflt = p.initializer ? ' = ' + p.initializer.getText() : '';
        return `${n}${opt}: ${t}${dflt}`;
    });
    const retType = node.type ? node.type.getText() : 'void';

    const typeParams = node.typeParameters
        ? '<' + node.typeParameters.map(tp => tp.getText()).join(', ') + '>'
        : '';
    const nameText = node.name ? node.name.text + typeParams : '';

    return nameText + '(' + paramStrs.join(', ') + '): ' + retType;
}

// ── getFunction ────────────────────────────────────────────────────────────

function getFunction(args) {
    const { path, content, name } = args;
    const lower = name.toLowerCase();
    const all = listFunctions({ path, content });
    return all.find(f => f.name.toLowerCase() === lower) || null;
}

// ── getFunctions ───────────────────────────────────────────────────────────

function getFunctions(args) {
    const { path, content, name } = args;
    const lower = name.toLowerCase();
    const all = listFunctions({ path, content });
    return all.filter(f => f.name.toLowerCase() === lower);
}

// ── summarize ──────────────────────────────────────────────────────────────

function summarize(args) {
    const { path, content } = args;
    const sf = parseFile(path, content);
    const lines = [];
    const fileName = path.split(/[/\\]/).pop();
    const totalLines = content.split('\n').length;

    lines.push(`// File: ${fileName} (${totalLines} lines)`);

    const imports = [];

    for (const stmt of sf.statements) {
        if (ts.isImportDeclaration(stmt)) {
            const moduleSpec = stmt.moduleSpecifier.getText();
            let names = [];
            if (stmt.importClause) {
                const ic = stmt.importClause;
                if (ic.name) names.push(ic.name.text);
                if (ic.namedBindings) {
                    if (ts.isNamedImports(ic.namedBindings)) {
                        names.push(...ic.namedBindings.elements.map(e => {
                            return e.propertyName ? e.propertyName.text + ' as ' + e.name.text : e.name.text;
                        }));
                    } else if (ts.isNamespaceImport(ic.namedBindings)) {
                        names.push('* as ' + ic.namedBindings.name.text);
                    }
                }
            }
            imports.push({ module: moduleSpec, names });
            continue;
        }
    }

    if (imports.length > 0) {
        lines.push('');
        lines.push('// Imports:');
        for (const imp of imports) {
            for (const n of imp.names) {
                lines.push(`//   ${n} from ${imp.module}`);
            }
            if (imp.names.length === 0) {
                lines.push(`//   ${imp.module}`);
            }
        }
    }

    let hasDeclarations = false;
    for (const stmt of sf.statements) {
        if (ts.isInterfaceDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeInterface(stmt, sf, lines, '');
        } else if (ts.isClassDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeClass(stmt, sf, lines, '');
        } else if (ts.isFunctionDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeFunction(stmt, sf, lines, '');
        } else if (ts.isEnumDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeEnum(stmt, sf, lines, '');
        } else if (ts.isTypeAliasDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeTypeAlias(stmt, sf, lines, '');
        } else if (ts.isVariableStatement(stmt)) {
            hasDeclarations = true;
            summarizeVariable(stmt, sf, lines, '');
        } else if (ts.isModuleDeclaration(stmt)) {
            hasDeclarations = true;
            summarizeModule(stmt, sf, lines, '');
        }
    }

    if (!hasDeclarations && imports.length === 0) {
        lines.push('// (no top-level declarations)');
    }

    return { text: lines.join('\n') };
}

function modifiers(node) {
    if (!node.modifiers) return '';
    return node.modifiers.map(m => m.getText()).join(' ');
}

function summarizeInterface(node, sf, lines, indent) {
    const pad = indent;
    const mods = modifiers(node);
    const typeParams = node.typeParameters
        ? '<' + node.typeParameters.map(tp => tp.getText()).join(', ') + '>'
        : '';
    const extendsClause = node.heritageClauses
        ? node.heritageClauses.map(h => h.getText()).join(' ')
        : '';
    const decl = `${mods}${mods ? ' ' : ''}interface ${node.name.text}${typeParams}${extendsClause ? ' ' + extendsClause : ''}`;
    const l = lineNum(sf, node.getStart());
    lines.push(`${pad}${decl} @${l}`);

    for (const member of node.members) {
        summarizeMember(member, sf, lines, pad + '  ');
    }
}

function summarizeClass(node, sf, lines, indent) {
    const pad = indent;
    const mods = modifiers(node);
    const typeParams = node.typeParameters
        ? '<' + node.typeParameters.map(tp => tp.getText()).join(', ') + '>'
        : '';
    const heritage = node.heritageClauses
        ? node.heritageClauses.map(h => h.getText()).join(' ')
        : '';
    const decl = `${mods}${mods ? ' ' : ''}class ${node.name ? node.name.text : '(anonymous)'}${typeParams}${heritage ? ' ' + heritage : ''}`;
    const l = lineNum(sf, node.getStart());
    lines.push(`${pad}${decl} @${l}`);

    for (const member of node.members) {
        summarizeMember(member, sf, lines, pad + '  ');
    }
}

function summarizeMember(node, sf, lines, indent) {
    const pad = indent;
    const l = lineNum(sf, node.getStart());
    const mods = modifiers(node);

    if (ts.isMethodDeclaration(node) || ts.isGetAccessorDeclaration(node) || ts.isSetAccessorDeclaration(node)) {
        const sig = buildSignature(node);
        lines.push(`${pad}${mods}${mods ? ' ' : ''}${sig} @${l}`);
    } else if (ts.isPropertyDeclaration(node)) {
        const name = node.name ? node.name.getText() : '(unnamed)';
        const type = node.type ? ': ' + node.type.getText() : '';
        const init = node.initializer ? ' = ' + node.initializer.getText() : '';
        lines.push(`${pad}${mods}${mods ? ' ' : ''}${name}${type}${init} @${l}`);
    } else if (ts.isConstructorDeclaration(node)) {
        const sig = buildSignature(node);
        lines.push(`${pad}${mods}${mods ? ' ' : ''}constructor${sig} @${l}`);
    }
}

function summarizeFunction(node, sf, lines, indent) {
    const pad = indent;
    const l = lineNum(sf, node.getStart());
    const mods = modifiers(node);
    const isExport = node.modifiers && node.modifiers.some(m => m.kind === ts.SyntaxKind.ExportKeyword);
    const isDefault = node.modifiers && node.modifiers.some(m => m.kind === ts.SyntaxKind.DefaultKeyword);
    const prefix = isExport ? (isDefault ? 'export default ' : 'export ') : '';
    const sig = buildSignature(node);
    if (node.name) {
        lines.push(`${pad}${prefix}function ${sig} @${l}`);
    }
}

function summarizeEnum(node, sf, lines, indent) {
    const pad = indent;
    const l = lineNum(sf, node.getStart());
    const mods = modifiers(node);
    const decl = `${mods}${mods ? ' ' : ''}enum ${node.name.text}`;
    lines.push(`${pad}${decl} @${l}`);
    for (const member of node.members) {
        const ml = lineNum(sf, member.getStart());
        const val = member.initializer ? ' = ' + member.initializer.getText() : '';
        lines.push(`${pad}  ${member.name.getText()}${val} @${ml}`);
    }
}

function summarizeTypeAlias(node, sf, lines, indent) {
    const pad = indent;
    const l = lineNum(sf, node.getStart());
    const typeParams = node.typeParameters
        ? '<' + node.typeParameters.map(tp => tp.getText()).join(', ') + '>'
        : '';
    const typeText = node.type ? node.type.getText() : 'unknown';
    lines.push(`${pad}type ${node.name.text}${typeParams} = ${truncateType(typeText)} @${l}`);
}

function summarizeVariable(node, sf, lines, indent) {
    const pad = indent;
    for (const decl of node.declarationList.declarations) {
        const l = lineNum(sf, decl.getStart());
        const name = decl.name.getText();
        const type = decl.type ? ': ' + decl.type.getText() : '';
        const init = decl.initializer ? ' = ' + truncateExpr(decl.initializer) : '';
        const isConst = node.declarationList.flags & ts.NodeFlags.Const;
        lines.push(`${pad}${isConst ? 'const ' : 'let '}${name}${type}${init} @${l}`);
    }
}

function summarizeModule(node, sf, lines, indent) {
    const pad = indent;
    const l = lineNum(sf, node.getStart());
    const name = node.name.text;
    lines.push(`${pad}module ${name} @${l}`);
    if (node.body) {
        for (const stmt of node.body.statements) {
            declDump(stmt, sf, lines, pad + '  ');
        }
    }
}

function declDump(node, sf, lines, indent) {
    if (ts.isInterfaceDeclaration(node)) summarizeInterface(node, sf, lines, indent);
    else if (ts.isClassDeclaration(node)) summarizeClass(node, sf, lines, indent);
    else if (ts.isFunctionDeclaration(node)) summarizeFunction(node, sf, lines, indent);
    else if (ts.isEnumDeclaration(node)) summarizeEnum(node, sf, lines, indent);
    else if (ts.isTypeAliasDeclaration(node)) summarizeTypeAlias(node, sf, lines, indent);
    else if (ts.isVariableStatement(node)) summarizeVariable(node, sf, lines, indent);
}

function truncateType(text) {
    if (text.length > 80) return text.substring(0, 77) + '...';
    return text;
}

function truncateExpr(node) {
    const text = node.getText();
    if (text.length > 60) return text.substring(0, 57) + '...';
    return text;
}

// ── Wire helpers ───────────────────────────────────────────────────────────

function writeResult(id, result) {
    process.stdout.write(JSON.stringify({ id, result }) + '\n');
}

function writeError(id, message) {
    process.stdout.write(JSON.stringify({ id, error: message }) + '\n');
}
