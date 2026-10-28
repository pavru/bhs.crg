#!/usr/bin/env node
// Перевод старых имён на роли MD3 (эпик sharedui-md3, [UI-58]) — перезапускаемой командой по карте
// md3-class-map.json, а не руками и не серией sed: классы живут в любых строках (className, карты
// вариантов, clsx, шаблоны, константы, ожидания тестов, комментарии), а ветка, начатая до перевода,
// обязана обновляться механически — принять master в конфликтах, запустить команду, закоммитить.
//
// Один проход по исходному тексту: строка собирается из кусков, результат замены повторно не
// заменяется (карта `a → b`, `b → c` даёт из `a b` строку `b c`, а не `c c`). Повторный запуск ничего не
// меняет. Неоднозначное — имя без пары в карте, `classes.skip`, строка отрицательного ожидания теста —
// не трогается и называется отчётом с файлом и строкой.
//
//   npm run md3:migrate            — перевести src/ и напечатать отчёт
//   npm run md3:migrate -- --check — только проверить: код 1, если есть что менять

import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { COLOR_UTILITIES, stripComments } from './class-scan.mjs';
import theme from '../src/theme/md3-theme.json' with { type: 'json' };
import classMap from './md3-class-map.json' with { type: 'json' };

const here = dirname(fileURLToPath(import.meta.url));
export const clientRoot = join(here, '..');
const UTILITY = new RegExp(`^(?:${COLOR_UTILITIES})$`);

/** Пары «имя — значение» без ключей-комментариев («//»). */
const entries = obj => Object.entries(obj ?? {}).filter(([k]) => !k.startsWith('//'));

// Переменная (`--f-shadow16`, `--color-brand`) или класс с границей сторожа словаря (`CANDIDATE` из
// class-scan.mjs): перед классом — начало, пробел, кавычка, скобка или `:` варианта (`hover:`,
// `data-[…]:`), после — конец, пробел, кавычка, скобка, запятая или `\` (кавычка в строке: `\'bg-brand\'`). Класс — любой, не только цветовой:
// `classes` карты дописывают и радиусы (sharedui-md3-06). Группы: 1 — переменная; 2 — `!` и `-` впереди;
// 3 — класс без вариантов; 4 — прозрачность и `!` в конце.
const TOKEN = new RegExp(
  '(?<![\\w-])(--[a-z][\\w-]*)'
  + `|(?<=^|[\\s'"\`:{(.])(!?-?)([a-z][\\w-]*)((?:\\/(?:[\\w.]+|\\[[^\\]\\s]+\\]))?!?)(?=$|[\\s'"\`}),;\\\\.:])`, 'gm');

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
    return { to: `${color.utility}-${to}`, pair: `${color.name} → ${to}` };
  };

  /** Переменная → `{ to }`, `{ reason }` или null. */
  const variable = name => {
    if (vars[name]) return { to: vars[name], pair: `${name} → ${vars[name]}` };
    const color = name.match(/^--color-(.+)$/)?.[1];
    if (color && names[color]?.to) return { to: `--color-${names[color].to}`, pair: `${color} → ${names[color].to}` };
    if (color && known.includes(color)) return { reason: `${color} — нет пары в карте (роли того же смысла нет: sharedui-md3-04, -05)` };
    if (name.startsWith('--f-')) return { reason: `${name} — нет строки в vars карты: значения без роли MD3` };
    return null;
  };

  return { cls, variable };
}

const isTest = file => /\.test\.[cm]?[tj]sx?$/.test(file);

/**
 * Перевод текста файла. `file` — путь для отчёта (и признак теста: в тесте строка с `.not.` —
 * отрицательное ожидание, её переписывают руками так, чтобы ожидание снова могло упасть).
 * Возвращает новый текст, замены (`{ line, from, to, pair, comment }`) и нетронутое (`{ line, token, reason }`).
 */
export function migrateText(text, { file = '', rules = compile() } = {}) {
  const stripped = stripComments(text);
  const lines = text.split('\n');
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
    if (isTest(file) && lines[line - 1].includes('.not.')) {
      untouched.push({ line, token, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
      return whole;
    }
    replaced.push({ line, from: token, to: hit.to, pair: hit.pair, comment: stripped[index] !== text[index] });
    return variable ? hit.to : `${lead}${hit.to}${tail}`;
  });
  return { text: out, replaced, untouched };
}

const SOURCE = /\.(?:[cm]?[tj]sx?|css)$/;

/** Файлы под `root`. `theme/` — сами псевдонимы и сгенерированная тема: их не переводят. */
export function sourceFiles(root) {
  if (statSync(root).isFile()) return [root];
  return readdirSync(root, { recursive: true })
    .map(f => String(f).replaceAll('\\', '/'))
    .filter(f => SOURCE.test(f) && !f.startsWith('theme/') && !f.includes('node_modules/'))
    .map(f => join(root, f));
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
  if (check && report.some(r => r.replaced.length)) {
    console.error('\nЕсть старые имена с парой в карте — выполните npm run md3:migrate и закоммитьте.');
    process.exit(1);
  }
}
