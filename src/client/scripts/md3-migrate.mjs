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
// Первым проходом — самодельные состояния → слой и отключённое MD3 (md3-states-migrate.mjs,
// sharedui-md3-05): правила о классе с вариантом, до перевода имён. Затем — значки lucide → Material Symbols (md3-icons-migrate.mjs, sharedui-md3-03): по уже
// переведённому тексту, потому что оба прохода правят className. Последним — размер кнопок и знак
// кнопки-иконки (md3-size-migrate.mjs, sharedui-md3-10): ему нужен уже переведённый `<Icon>`.

import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { CLASS_AFTER, CLASS_BEFORE, COLOR_UTILITIES, containerTextHits, stripComments } from './class-scan.mjs';
import { entries, themeDir } from './md3-theme.mjs';
import theme from '../src/theme/md3-theme.json' with { type: 'json' };
import classMap from './md3-class-map.json' with { type: 'json' };
import { formatIconReport, migrateIcons } from './md3-icons-migrate.mjs';
import { formatSizeReport, migrateSizes } from './md3-size-migrate.mjs';
import { compileStates, migrateStates } from './md3-states-migrate.mjs';
import { commitsAfterTransition, compileShape, gitOldLines, hasCommitsBeforeTransition, migrateRadii, migrateShadows } from './md3-shape-migrate.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const clientRoot = join(here, '..');
const UTILITY = new RegExp(`^(?:${COLOR_UTILITIES})$`);
// Запреты пары карты по утилите ([UI-63]): `lines` — роли текста в обводке, кольце, контуре,
// разделителе; `text` — роли обводки в тексте, подчёркивании, каретке. Такой класс не переводится, а
// называется отчётом с причиной из карты.
const RESTRICTED = [
  ['lines', /^(?:border(?:-[xytblrse])?|ring|ring-offset|inset-ring|outline|divide)$/],
  ['text', /^(?:text|decoration|caret)$/],
];

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
    for (const [key, utility] of RESTRICTED)
      if (names[color.name][key] && utility.test(color.utility)) return { reason: `${body} — ${names[color.name][key]}` };
    return { to: `${color.utility}-${to}`, pair: `${color.name} → ${to}` };
  };

  // Имя, которое для какой-то утилиты не переводится по смыслу (`text-fg4`: текст цветом обводки), —
  // в `var(--color-…)` неоднозначно: утилиты у переменной нет, и чем она станет, по ней не видно.
  // Так же и имя с запретом по утилите: переменная бывает и цветом обводки (`border-l-[var(--color-fg4)]`),
  // и цветом текста (`[color:var(--color-stroke-strong)]`).
  const ambiguousVar = new Map([
    ...entries(map.names).filter(([, v]) => v.lines).map(([k, v]) => [k, `обводка с ${k} — ${v.lines}`]),
    ...entries(map.names).filter(([, v]) => v.text).map(([k, v]) => [k, `текст с ${k} — ${v.text}`]),
    ...entries(map.classes).filter(([k, v]) => v.skip && colorName(k))
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
 * руками так, чтобы оно снова могло упасть, — см. `inNegativeExpect`). `isOld(line)` — строка написана
 * до перехода на шкалу формы: только её радиус сдвигается по цепи (md3-shape-migrate.mjs); без него
 * радиусы не сдвигаются.
 * Возвращает новый текст, замены (`{ line, from, to, pair, comment }`) и нетронутое (`{ line, token, reason }`).
 */
export function migrateText(original, { file = '', rules = compile(), states = compileStates(), shape = compileShape(),
  isOld = () => false } = {}) {
  // Радиусы — первыми: номера строк ещё те же, что у git blame.
  const radii = migrateRadii(original, { isOld, rules: shape, skip: at => isTest(file) && inNegativeExpect(original, at) });
  const source = radii.text;
  // Заливку проход состояний узнаёт по имени после перевода: `bg-brand-subtle` — уже заливка контейнера.
  const resolve = c => {
    const slash = c.indexOf('/');
    const body = slash < 0 ? c : c.slice(0, slash);
    return (rules.cls(body)?.to ?? body) + (slash < 0 ? '' : c.slice(slash));
  };
  const pre = migrateStates(source, { rules: states, resolve, skip: at => isTest(file) && inNegativeExpect(source, at) });
  const text = pre.text;
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
  const onContainer = containerText(out, file);
  // Тени — последними: перенос стиля в className убирает строки.
  const shadows = migrateShadows(onContainer.text, { skip: at => isTest(file) && inNegativeExpect(onContainer.text, at) });
  return {
    text: shadows.text,
    replaced: [...radii.replaced, ...pre.replaced, ...replaced, ...onContainer.replaced, ...shadows.replaced],
    untouched: [...radii.untouched, ...pre.untouched, ...untouched, ...onContainer.untouched, ...shadows.untouched],
  };
}

/**
 * Текст на контейнере роли в одном списке классов → парная роль `on-X-container` (`containerTextHits`).
 * Отрицательное ожидание теста не переписывается — как и в основном проходе.
 */
function containerText(text, file) {
  const replaced = [];
  const untouched = [];
  const parts = [];
  let last = 0;
  let line = 1;
  let counted = 0;
  for (const { at, from, to, role } of containerTextHits(text)) {
    for (; counted < at; counted++) if (text[counted] === '\n') line++;
    if (isTest(file) && inNegativeExpect(text, at)) {
      untouched.push({ line, token: from, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
      continue;
    }
    parts.push(text.slice(last, at), to);
    last = at + from.length;
    replaced.push({ line, from, to, pair: `text-${role} на bg-${role}-container → text-on-${role}-container`, comment: false });
  }
  parts.push(text.slice(last));
  return { text: parts.join(''), replaced, untouched };
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

/**
 * Перевод файлов: `write` — записать изменённые. `oldLines(path)` — строки, написанные до перехода на
 * шкалу формы (по умолчанию — git blame в репозитории `base`; зовётся только у файла, где есть радиус
 * цепи, и только если в истории есть коммиты чужой ветки до перехода). Возвращает отчёт по файлам.
 */
export function migrateFiles(files, { write = false, rules = compile(), base = clientRoot, oldLines } = {}) {
  let after;
  let blame;
  oldLines ??= path => {
    after ??= commitsAfterTransition(base);
    blame ??= hasCommitsBeforeTransition(base, after);
    return blame ? gitOldLines(path, base, after) : () => false;
  };
  const report = [];
  for (const path of files) {
    const text = readFileSync(path, 'utf8');
    const file = relative(base, path).replaceAll('\\', '/');
    let old;
    const result = migrateText(text, { file, rules, isOld: line => (old ??= oldLines(path))(line) });
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
  // По тексту после значков: при --check они не записаны, и проход видит их ещё lucide — тогда он
  // промолчит о знаках, но команда и так откажет из-за значков.
  const sizes = files.map(path => {
    const result = migrateSizes(readFileSync(path, 'utf8'), path);
    if (!check && result.replaced.length) writeFileSync(path, result.text, 'utf8');
    return { file: relative(clientRoot, path).replaceAll('\\', '/'), ...result };
  }).filter(r => r.replaced.length || r.untouched.length);
  console.log(`\n${formatSizeReport(sizes, { check })}`);
  if (check && (report.some(r => r.replaced.length) || icons.files.some(f => f.changes) || sizes.some(r => r.replaced.length))) {
    console.error('\nЕсть старые имена с парой в карте, значки lucide или размер у кнопки — выполните npm run md3:migrate и закоммитьте.');
    process.exit(1);
  }
}
