#!/usr/bin/env node
// Перевод старых имён на роли MD3 (эпик sharedui-md3, [UI-58]) — перезапускаемой командой по карте
// md3-class-map.json, а не руками и не серией sed: классы живут в любых строках (className, карты
// вариантов, clsx, шаблоны, константы, ожидания тестов, комментарии), а ветка, начатая до перевода,
// обязана обновляться механически — слить перевод с версией ветки в конфликтах, запустить команду,
// закоммитить (docs/UI.md, «Перевод: как обновить ветку»).
//
// Один проход по исходному тексту: строка собирается из кусков, результат замены повторно не
// заменяется (карта `a → b`, `b → c` даёт из `a b` строку `b c`, а не `c c`). Повторный запуск ничего не
// меняет. Неоднозначное — имя без пары в карте, `classes.skip`, строка отрицательного ожидания теста —
// не трогается и называется отчётом с файлом и строкой.
//
//   npm run md3:migrate            — перевести src/ и напечатать отчёт
//   npm run md3:migrate -- --check — только проверить: код 1, если есть что менять
//
// Вторым проходом — значки lucide → Material Symbols (md3-icons-migrate.mjs, sharedui-md3-03): по уже
// переведённому тексту, потому что оба прохода правят className.

import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { CLASS_AFTER, CLASS_BEFORE, COLOR_UTILITIES, stripComments } from './class-scan.mjs';
import { entries, themeDir } from './md3-theme.mjs';
import theme from '../src/theme/md3-theme.json' with { type: 'json' };
import classMap from './md3-class-map.json' with { type: 'json' };
import { formatIconReport, migrateIcons } from './md3-icons-migrate.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const clientRoot = join(here, '..');
const UTILITY = new RegExp(`^(?:${COLOR_UTILITIES})$`);
// Утилиты линии — обводка, кольцо, контур, разделитель: им `lines` пары карты запрещает роль текста.
const LINE = /^(?:border(?:-[xytblrse])?|ring|ring-offset|inset-ring|outline|divide)$/;

// Переменная (`--f-shadow16`, `--color-brand`) или класс с границей сторожей (`CLASS_BEFORE`,
// `CLASS_AFTER` из class-scan.mjs — одна на обоих: что команда переводит, то храповик legacy-class и
// видит). Класс — любой, не только цветовой: `classes` карты дописывают и радиусы (sharedui-md3-06).
// Группы: 1 — переменная; 2 — `!` и `-` впереди; 3 — класс без вариантов; 4 — прозрачность и `!` в конце.
const TOKEN = new RegExp(
  '(?<![\\w-])(--[a-z][\\w-]*)'
  + `|${CLASS_BEFORE}(!?-?)([a-z][\\w-]*)((?:\\/(?:[\\w.]+|\\[[^\\]\\s]+\\]))?!?)${CLASS_AFTER}`, 'gm');

/**
 * Правила из карты. `aliases` — все старые имена цвета (описание темы): имя без пары в `names` —
 * неоднозначно и называется отчётом.
 */
export function compile(map = classMap, aliases = entries(theme.aliases).map(([k]) => k)) {
  const names = Object.fromEntries(entries(map.names));
  const classes = Object.fromEntries(entries(map.classes));
  const vars = Object.fromEntries(entries(map.vars));
  // Длинные — первыми: `bg-on-brand-subtle` — это `on-brand-subtle`, а не `brand-subtle` или `subtle`.
  const known = [...new Set([...aliases, ...Object.keys(names)])].sort((a, b) => b.length - a.length);

  const colorName = cls => {
    for (const name of known)
      if (cls.endsWith(`-${name}`) && UTILITY.test(cls.slice(0, -name.length - 1)))
        return { utility: cls.slice(0, -name.length - 1), name };
    return null;
  };

  /** Класс без вариантов и прозрачности → `{ to }`, `{ reason }` (не трогать, назвать) или null. */
  const cls = body => {
    const own = classes[body];
    if (own?.to) return { to: own.to, pair: `${body} → ${own.to}` };
    if (own?.skip) return { reason: `${body} — ${own.skip}` };
    const color = colorName(body);
    if (!color) return null;
    const to = names[color.name]?.to;
    if (!to) return { reason: `${color.name} — нет пары в карте (роли того же смысла нет: sharedui-md3-04, -05)` };
    const lines = names[color.name].lines;
    if (lines && LINE.test(color.utility)) return { reason: `${body} — ${lines}` };
    return { to: `${color.utility}-${to}`, pair: `${color.name} → ${to}` };
  };

  // Имя, которое для какой-то утилиты не переводится по смыслу (`text-fg4`: текст цветом обводки), —
  // в `var(--color-…)` неоднозначно: утилиты у переменной нет, и чем она станет, по ней не видно.
  // `notColor` — пропуск не о цвете (`text-base` — размер текста), имя переменной он не задевает.
  // Так же и имя с `lines`: переменная бывает и цветом обводки (`border-l-[var(--color-fg4)]`).
  const ambiguousVar = new Map([
    ...entries(map.names).filter(([, v]) => v.lines).map(([k, v]) => [k, `обводка с ${k} — ${v.lines}`]),
    ...entries(map.classes).filter(([k, v]) => v.skip && !v.notColor && colorName(k))
      .map(([k, v]) => [colorName(k).name, `${k} — ${v.skip}`]),
  ]);

  /** Переменная → `{ to }`, `{ reason }` или null. */
  const variable = name => {
    if (vars[name]) return { to: vars[name], pair: `${name} → ${vars[name]}` };
    const color = name.match(/^--color-(.+)$/)?.[1];
    if (color && ambiguousVar.has(color))
      return { reason: `--color-${color} — переменная без утилиты, а ${ambiguousVar.get(color)}` };
    if (color && names[color]?.to) return { to: `--color-${names[color].to}`, pair: `${color} → ${names[color].to}` };
    if (color && known.includes(color)) return { reason: `${color} — нет пары в карте (роли того же смысла нет: sharedui-md3-04, -05)` };
    if (name.startsWith('--f-')) return { reason: `${name} — нет строки в vars карты: значения без роли MD3` };
    return null;
  };

  return { cls, variable };
}

const isTest = file => /\.test\.[cm]?[tj]sx?$/.test(file);
const NEGATIVE = /\.not\s*\.|\.toBe\(\s*false\s*\)|\.toBeFalsy\(\)|\.toBeNull\(\)|\.toBeUndefined\(\)|\.toHaveLength\(\s*0\s*\)/;

/**
 * Отрицательное ожидание: место внутри цепочки `expect(…)…;`, где есть `.not.`, `toBe(false)`,
 * `toBeFalsy()`, `toBeNull()` и подобное. Цепочка — от последнего `expect(` до `;` после места, если
 * между `expect(` и местом нет `;`: многострочное ожидание узнаётся так же, как однострочное.
 */
function inNegativeExpect(text, index) {
  const start = text.lastIndexOf('expect(', index);
  if (start < 0 || text.slice(start, index).includes(';')) return false;
  const end = text.indexOf(';', index);
  return NEGATIVE.test(text.slice(start, end < 0 ? text.length : end));
}

/**
 * Перевод текста файла. `file` — путь для отчёта (и признак теста: отрицательное ожидание переписывают
 * руками так, чтобы оно снова могло упасть, — см. `inNegativeExpect`).
 * Возвращает новый текст, замены (`{ line, from, to, pair, comment }`) и нетронутое (`{ line, token, reason }`).
 */
export function migrateText(text, { file = '', rules = compile() } = {}) {
  const stripped = stripComments(text);
  const replaced = [];
  const untouched = [];
  let line = 1;
  let at = 0;
  const out = text.replace(TOKEN, (whole, variable, lead, body, tail, index) => {
    for (; at < index; at++) if (text[at] === '\n') line++;
    const hit = variable ? rules.variable(variable) : rules.cls(body);
    if (!hit) return whole;
    const token = variable ?? body;
    if (hit.reason) {
      untouched.push({ line, token, reason: hit.reason });
      return whole;
    }
    if (isTest(file) && inNegativeExpect(text, index)) {
      untouched.push({ line, token, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
      return whole;
    }
    replaced.push({ line, from: token, to: hit.to, pair: hit.pair, comment: stripped[index] !== text[index] });
    return variable ? hit.to : `${lead}${hit.to}${tail}`;
  });
  const onContainer = containerText(out);
  return { text: onContainer.text, replaced: [...replaced, ...onContainer.replaced], untouched };
}

/** Роли с контейнером: текст на `bg-X-container` — парной ролью `on-X-container` ([UI-62]). */
const CONTAINER_ROLES = ['error', 'success', 'warning', 'primary', 'secondary', 'tertiary'];
const LIST_TOKEN = /(?<=^|\s)([\w:[\]&=>*-]*:)?(bg|text)-([a-z]+(?:-container)?)((?:\/[\w.]+)?!?)(?=\s|$)/g;

/**
 * Текст на контейнере состояния в ОДНОМ списке классов (`bg-warning-container text-warning`) →
 * `text-on-warning-container`: цвет самой роли — для текста на поверхности, на своём контейнере у него
 * парная роль. Список — строка в кавычках; у шаблона `…${…}…` смотрятся только постоянные части, иначе
 * условный контейнер (`${on ? 'bg-warning-container' : ''}`) перекрасил бы текст и там, где его нет.
 * Вариант обязан совпасть: `hover:bg-warning-container text-warning` — контейнер только под наведением.
 * Разнесённое по элементам (плашка снаружи, текст внутри) команда не видит — это правится руками.
 */
function containerText(text) {
  const replaced = [];
  const parts = [];
  let last = 0;
  const lineAt = i => text.slice(0, i).split('\n').length;
  for (const span of stringSpans(text)) {
    const tokens = span.flatMap(([from, to]) => [...text.slice(from, to).matchAll(LIST_TOKEN)].map(m => ({ m, at: from + m.index })));
    const containers = new Set(tokens.filter(({ m }) => m[2] === 'bg' && m[3].endsWith('-container'))
      .map(({ m }) => `${m[1] ?? ''}|${m[3].slice(0, -'-container'.length)}`));
    for (const { m, at } of tokens) {
      if (m[2] !== 'text' || !CONTAINER_ROLES.includes(m[3]) || !containers.has(`${m[1] ?? ''}|${m[3]}`)) continue;
      const cls = `${m[1] ?? ''}text-on-${m[3]}-container${m[4]}`;
      parts.push(text.slice(last, at), cls);
      last = at + m[0].length;
      replaced.push({ line: lineAt(at), from: m[0], to: cls, pair: `text-${m[3]} на bg-${m[3]}-container → text-on-${m[3]}-container`, comment: false });
    }
  }
  parts.push(text.slice(last));
  return { text: parts.join(''), replaced };
}

/**
 * Строки кода (не комментарии) — каждая как список отрезков [начало, конец) без кавычек; у шаблона
 * отрезки — его постоянные части, между `${…}`. Однострочные `'…'`, многострочные `"…"` (JSX) и `` `…` ``.
 */
function stringSpans(text) {
  const spans = [];
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '/' && text[i + 1] === '/') { const end = text.indexOf('\n', i); i = end < 0 ? text.length : end; continue; }
    if (c === '/' && text[i + 1] === '*') { const end = text.indexOf('*/', i + 2); i = end < 0 ? text.length : end + 1; continue; }
    if (c !== '"' && c !== "'" && c !== '`') continue;
    const span = [];
    let from = i + 1;
    for (i++; i < text.length && text[i] !== c; i++) {
      if (text[i] === '\\') { i++; continue; }
      if (c === "'" && text[i] === '\n') break;
      if (c === '`' && text[i] === '$' && text[i + 1] === '{') {
        span.push([from, i]);
        let depth = 0;
        for (i += 2; i < text.length && (text[i] !== '}' || depth > 0); i++) depth += text[i] === '{' ? 1 : text[i] === '}' ? -1 : 0;
        from = i + 1;
      }
    }
    span.push([from, i]);
    spans.push(span);
  }
  return spans;
}

const SOURCE = /\.(?:[cm]?[tj]sx?|css)$/;

// Не переводятся при любом корне: src/theme/ — сами псевдонимы и сгенерированная тема (`--color-fg1: …`
// перевёлся бы в роль), scripts/ — сама команда, её карта и фикстуры старых имён у тестов сторожей.
// Регистр сравнивается без учёта на Windows: буква диска у cwd и у import.meta.url бывает разной.
const norm = p => process.platform === 'win32' ? p.toLowerCase() : p;
const NEVER = [themeDir, here].map(norm);
const outside = path => !NEVER.some(dir => norm(path) === dir || norm(path).startsWith(dir + sep));

/** Файлы под `root`, кроме `NEVER`, node_modules и dist. */
export function sourceFiles(root) {
  const abs = resolve(root);
  if (!outside(abs)) return [];
  if (statSync(abs).isFile()) return [abs];
  return readdirSync(abs, { withFileTypes: true }).flatMap(e =>
    e.isDirectory() ? (e.name === 'node_modules' || e.name === 'dist' ? [] : sourceFiles(join(abs, e.name)))
      : SOURCE.test(e.name) ? sourceFiles(join(abs, e.name)) : []);
}

/** Перевод файлов: `write` — записать изменённые. Возвращает отчёт по файлам. */
export function migrateFiles(files, { write = false, rules = compile(), base = clientRoot } = {}) {
  const report = [];
  for (const path of files) {
    const text = readFileSync(path, 'utf8');
    const file = relative(base, path).replaceAll('\\', '/');
    const result = migrateText(text, { file, rules });
    if (!result.replaced.length && !result.untouched.length) continue;
    if (write && result.replaced.length) writeFileSync(path, result.text, 'utf8');
    report.push({ file, replaced: result.replaced, untouched: result.untouched });
  }
  return report;
}

/** Отчёт текстом: пары с числом мест, затем нетронутое по причинам с `файл:строка`. */
export function formatReport(report, { check = false } = {}) {
  const all = report.flatMap(r => r.replaced);
  const files = report.filter(r => r.replaced.length).length;
  const inComments = all.filter(r => r.comment).length;
  const out = [`md3:migrate: замен ${all.length}${check ? ' (не записано, --check)' : ''} в ${files} файлах`
    + (all.length ? ` — в коде ${all.length - inComments}, в комментариях ${inComments}.` : '.')];

  const pairs = new Map();
  for (const r of all) pairs.set(r.pair, (pairs.get(r.pair) ?? 0) + 1);
  if (pairs.size) {
    out.push('', 'Пары:');
    for (const [pair, n] of [...pairs].sort((a, b) => b[1] - a[1])) out.push(`  ${pair}  ${n}`);
  }

  const byReason = new Map();
  for (const r of report)
    for (const u of r.untouched) (byReason.get(u.reason) ?? byReason.set(u.reason, []).get(u.reason)).push(`${r.file}:${u.line} ${u.token}`);
  if (byReason.size) {
    const n = [...byReason.values()].reduce((a, b) => a + b.length, 0);
    out.push('', `Не тронуто — неоднозначное, правится руками или ребёнком эпика (${n}):`);
    for (const [reason, places] of byReason) {
      out.push(`  ${reason}:`);
      for (const p of places) out.push(`    ${p}`);
    }
  }
  return out.join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const args = process.argv.slice(2);
  const check = args.includes('--check');
  const roots = args.filter(a => !a.startsWith('--'));
  const files = (roots.length ? roots : [join(clientRoot, 'src')]).flatMap(r => sourceFiles(r));
  const report = migrateFiles(files, { write: !check });
  console.log(formatReport(report, { check }));
  const icons = migrateIcons(files);
  if (icons.unknown.length) {
    console.error(`\nЗначки lucide вне таблицы scripts/md3-icon-map.json — значки не записаны:\n${
      icons.unknown.map(u => `  ${u.name}: ${u.where.join(', ')}`).join('\n')}\n`
      + 'Допишите их в таблицу (symbol — имя файла @material-symbols/svg-400/outlined) и в manifest.json.');
    process.exit(1);
  }
  if (icons.refused.length) {
    console.error(`\nИмпорт lucide, который команда не переводит, — значки не записаны:\n${
      icons.refused.map(r => `  ${r.file}: ${r.form}`).join('\n')}\n`
      + "Перепишите на именованный импорт (import { Trash2 } from 'lucide-react') и запустите снова.");
    process.exit(1);
  }
  if (!check) for (const f of icons.files) if (f.changes) writeFileSync(f.path, f.text, 'utf8');
  console.log(`\n${formatIconReport(icons, { check })}`);
  if (check && (report.some(r => r.replaced.length) || icons.files.some(f => f.changes))) {
    console.error('\nЕсть старые имена с парой в карте или значки lucide — выполните npm run md3:migrate и закоммитьте.');
    process.exit(1);
  }
}
