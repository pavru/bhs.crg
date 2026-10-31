#!/usr/bin/env node
// Переименование тоста в snackbar ([UI-71], sharedui-md3-12-feedback, PR 0) — перезапускаемой командой,
// как переводы md3-*: ветка, начатая до переименования, обновляется механически, а не руками.
//
//   node scripts/snackbar-rename.mjs           — переименовать (файлы — git mv, затем ссылки)
//   node scripts/snackbar-rename.mjs --check   — только отчёт; код 1, если есть что менять
//
// Что меняет — по синтаксическому дереву, а не по тексту:
//   идентификаторы из IDENTIFIERS (импорт, вызов, JSX-тег, ключ в фабрике vi.mock);
//   имена модулей из FILES в import/export, import() и vi.mock/doMock/importActual — по РАЗРЕШЁННОМУ
//   пути, так что './Toast' в shared/ui и '@/shared/ui/Toast' — одно и то же, а чужой './Toast' — нет.
// Локальная переменная `toast`, тексты и комментарии не трогаются: смысл слов правит человек.
// Значение `origin: 'toast'` отчёта об ошибке — данные (его читает BugReportsPage), а не имя.

import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const clientRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

/** Старое имя → новое. Без прототипа: иначе `toString` и `constructor` нашлись бы «в таблице». */
export const IDENTIFIERS = Object.assign(Object.create(null), {
  useToast: 'useSnackbar',
  ToastProvider: 'SnackbarProvider',
  useSavedHiddenToast: 'useSavedHiddenSnackbar',
});

/** Файлы от src/, без расширения: старый → новый. */
export const FILES = Object.assign(Object.create(null), {
  'shared/ui/Toast': 'shared/ui/Snackbar',
  'shared/ui/ToastProvider': 'shared/ui/SnackbarProvider',
  'shared/siteFilter/useSavedHiddenToast': 'shared/siteFilter/useSavedHiddenSnackbar',
});

const MOCK_CALL = /^vi\.(mock|doMock|unmock|importActual|importMock)$/;

/** Новый спецификатор модуля, если он указывает на файл из FILES; иначе null. */
function renameSpecifier(spec, file, srcRoot) {
  let target;
  if (spec.startsWith('@/')) target = spec.slice(2);
  else if (spec.startsWith('.')) target = relative(srcRoot, resolve(dirname(file), spec));
  else return null;
  const to = FILES[target.replaceAll('\\', '/')];
  if (!to) return null;
  const base = to.slice(to.lastIndexOf('/') + 1);
  return spec.slice(0, spec.lastIndexOf('/') + 1) + base;
}

function isModuleString(node) {
  const p = node.parent;
  if ((ts.isImportDeclaration(p) || ts.isExportDeclaration(p)) && p.moduleSpecifier === node) return true;
  return ts.isCallExpression(p) && p.arguments[0] === node
    && (p.expression.kind === ts.SyntaxKind.ImportKeyword || MOCK_CALL.test(p.expression.getText()));
}

/**
 * Переименование в одном тексте. `file` — абсолютный путь файла (для относительных импортов).
 * @returns новый текст и число правок
 */
export function renameSnackbar(text, file, { srcRoot = join(clientRoot, 'src') } = {}) {
  const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true,
    file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  const edits = [];
  const visit = node => {
    if (ts.isIdentifier(node) && IDENTIFIERS[node.text]) {
      edits.push([node.getStart(sf), node.getEnd(), IDENTIFIERS[node.text]]);
    } else if (ts.isStringLiteral(node) && isModuleString(node)) {
      const to = renameSpecifier(node.text, file, srcRoot);
      if (to) edits.push([node.getStart(sf) + 1, node.getEnd() - 1, to]);
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  let out = text;
  for (const [start, end, to] of edits.sort((a, b) => b[0] - a[0])) out = out.slice(0, start) + to + out.slice(end);
  return { text: out, changes: edits.length };
}

function* sourceFiles(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, e.name);
    if (e.isDirectory()) yield* sourceFiles(path);
    else if (/\.(ts|tsx|mts)$/.test(e.name)) yield path;
  }
}

function main(check) {
  const srcRoot = join(clientRoot, 'src');
  const moves = [];
  for (const [from, to] of Object.entries(FILES)) {
    for (const ext of ['.ts', '.tsx']) {
      if (existsSync(join(srcRoot, from + ext))) moves.push([from + ext, to + ext]);
    }
  }
  if (!check) {
    for (const [from, to] of moves) execFileSync('git', ['mv', join(srcRoot, from), join(srcRoot, to)], { cwd: clientRoot });
  }
  const changed = [];
  for (const file of sourceFiles(srcRoot)) {
    const text = readFileSync(file, 'utf8');
    const result = renameSnackbar(text, file, { srcRoot });
    if (!result.changes) continue;
    changed.push([relative(srcRoot, file).replaceAll('\\', '/'), result.changes]);
    if (!check) writeFileSync(file, result.text);
  }
  const total = changed.reduce((n, [, c]) => n + c, 0);
  console.log(`snackbar-rename: файлов перенесено ${moves.length}, правок ${total} в ${changed.length} файлах`
    + (check ? ' (не записано, --check)' : '') + '.');
  for (const [from, to] of moves) console.log(`  ${from} → ${to}`);
  if (check && (moves.length || total)) process.exitCode = 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.includes('--check'));
