#!/usr/bin/env node
// Храповик интерфейса (issue #1349, эпик #1347).
//
// В features/ растут конструкции в обход дизайн-системы: <table> вместо DataGrid, нативный
// <select> вместо Select, произвольный размер текста text-[11px], сырая палитра Tailwind,
// браузерная подсказка title вместо Tooltip (#1394), нативный флажок вместо Checkbox (#1414), свои
// чипы, пометки и вкладки вместо Chip, StatusBadge и Tabs (#1416), кнопка из одной иконки вместо
// IconButton (#1417), белый текст на сплошной заливке вместо text-on-* (#1462, смотрит и shared/ui/ —
// каталоги правила называет его `dirs`), портал слоя в свой контейнер (#1521, тоже shared/ui/),
// ручной выпадающий список вместо слоя Radix (#1537, тоже shared/ui/),
// нативное поле даты и времени вместо DateField / DateInput (#1459, весь src/). С #1469 то же
// растёт и в обвязке shared/ (отбор реестров) — она под присмотром наравне с features/.
// Переписать накопленное разом нельзя, а остановить рост можно — тем же приёмом, что храповик
// линта (#854) и размера файлов (#1041): накопленное не чинить, новое не добавлять.
//
// Почему не ESLint: `no-restricted-syntax` уже занят сторожем randomUUID (#848), а храповик линта
// считает ПО ПРАВИЛУ — новые запреты смешались бы с ним в один счётчик. И text-[11px] — строка
// внутри className, селектор AST её ловит плохо.
//
// Сравнение по файлам (почему — в ui-ratchet-lib.mjs). Уровень опускается тем же PR: локально
// файл переписывается сам, в CI шаг падает с просьбой закоммитить — как у храповика линта.
//
//   node scripts/ui-ratchet.mjs            — проверить
//   node scripts/ui-ratchet.mjs --update   — переписать базовый уровень по факту

import { readFileSync, writeFileSync, existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { RULES, countViolations, compare, normalize, dirsToScan, scopeNote, defaultScopeNote, ROOT } from './ui-ratchet-lib.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const clientRoot = join(here, '..');
const srcRoot = join(clientRoot, 'src');
const baselinePath = join(clientRoot, 'ui-baseline.json');
const update = process.argv.includes('--update');

// Под присмотром — экраны и обвязка shared/, кроме shared/ui/ (#1469): там живут сами DataGrid и
// Select, которые эти элементы и заворачивают; граница — по пути файла (`watches`). Другие каталоги —
// только правилами, которые их назвали (`dirs`). Тесты не считаются: разметка в фикстурах — не интерфейс. Правило со всем src/ (`everywhere`) добавляет каждый каталог и корень
// (`ROOT`: App.tsx, main.tsx) — список каталогов берётся с диска (`dirsToScan`). Файлы стилей
// читаются тоже, но считают их только правила со `styles` (legacy-token): см. `watches`.
const code = f => /\.(tsx?|jsx?|css)$/.test(f) && !/\.test\.[tj]sx?$/.test(f);
const sources = dir => dir === ROOT
  ? readdirSync(srcRoot, { withFileTypes: true }).filter(e => e.isFile() && code(e.name)).map(e => e.name)
  : readdirSync(join(srcRoot, dir), { recursive: true })
    .map(f => dir + '/' + String(f).replaceAll('\\', '/')).filter(code);
const dirs = dirsToScan(RULES, readdirSync(srcRoot, { withFileTypes: true }).filter(e => e.isDirectory()).map(e => e.name));

const actual = {};
for (const dir of dirs)
  for (const file of sources(dir))
    for (const [rule, n] of Object.entries(countViolations(readFileSync(join(srcRoot, file), 'utf8'), { dir, file })))
      (actual[rule] ??= {})[file] = n;

const sum = level => Object.fromEntries(RULES.map(r =>
  [r.id, Object.values(level[r.id] ?? {}).reduce((a, b) => a + b, 0)]));
const summary = level => Object.entries(sum(level)).map(([k, v]) => `${k} ${v}`).join(', ');

if (update) {
  writeFileSync(baselinePath, JSON.stringify(normalize(actual), null, 2) + '\n', 'utf8');
  console.log(`Базовый уровень переписан: ${summary(actual)}.`);
  process.exit(0);
}

if (!existsSync(baselinePath)) {
  console.error(`Нет файла базового уровня ${baselinePath}. Создайте его: npm run ui:ratchet:update`);
  process.exit(2);
}

const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
const { worse, better } = compare(baseline, actual);
const line = d => `  ${d.file} — ${d.rule}: было ${d.was}, стало ${d.now}`;

const wider = RULES.map(scopeNote).filter(Boolean);
console.log(`Нарушений интерфейса в ${defaultScopeNote()}${wider.length ? ` (${wider.join('; ')})` : ''}: ${summary(actual)}.`);

if (worse.length > 0) {
  console.error('\nНарушений интерфейса стало БОЛЬШЕ:\n' + worse.map(line).join('\n'));
  console.error('\nКуда вместо:');
  for (const rule of RULES.filter(r => worse.some(d => d.rule === r.id)))
    console.error(`  ${rule.id}: ${rule.instead}`);
  console.error('\nНакопленное чинить не требуется — требуется не добавлять. Если исключение законно и'
    + '\nсогласовано, поднимите уровень (npm run ui:ratchet:update) и скажите в PR, почему.');
  process.exit(1);
}

if (better.length > 0) {
  console.log('\nСтало ЛУЧШЕ:\n' + better.map(line).join('\n'));

  if (process.env.CI) {
    console.error('\nУровень выше достигнутого — опустите его и закоммитьте, иначе те же нарушения вернутся молча:'
      + '\n\n  npm run ui:ratchet:update\n');
    process.exit(1);
  }

  writeFileSync(baselinePath, JSON.stringify(normalize(actual), null, 2) + '\n', 'utf8');
  console.log('\nБазовый уровень опущен по факту — закоммитьте ui-baseline.json вместе с правкой.');
}
