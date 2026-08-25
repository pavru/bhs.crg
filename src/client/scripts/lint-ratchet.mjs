#!/usr/bin/env node
// Храповик линта (issue #854).
//
// На момент включения CI в клиенте 112 ошибок линта. Требовать «почините всё» значило бы отложить
// проверку до того дня, когда их починят, — то есть навсегда. Требование другое: НЕ ДОБАВЛЯТЬ.
//
// Сравнение идёт ПО ПРАВИЛАМ, а не по общему числу. Общее число пропустило бы ровно тот случай,
// который и есть деградация: одна ошибка `react-refresh` починена, одна `set-state-in-effect`
// добавлена — итог тот же, а хуже стало.
//
// Уровень стал ниже базового — храповик обязан довернуться, иначе он прокручивается назад:
// PR-A починил пять ошибок и уровень не опустил, PR-B вернул те же пять — и проверка молчит,
// пропуская ровно ту деградацию, ради которой заведена. Поэтому локально файл переписывается
// сам (чинить и помнить про ещё одну команду — лишнее), а в CI шаг падает с просьбой сделать это
// и закоммитить: править файл в CI бессмысленно, коммитить его оттуда некому.
//
//   node scripts/lint-ratchet.mjs            — проверить
//   node scripts/lint-ratchet.mjs --update   — переписать базовый уровень по факту

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const clientRoot = join(here, '..');
const baselinePath = join(clientRoot, 'eslint-baseline.json');
const update = process.argv.includes('--update');

function runEslint() {
  // eslint выходит с ненулевым кодом при любой ошибке — это норма, разбираем вывод, а не код.
  // Большой maxBuffer: json на полсотни файлов не влезает в дефолтную мегабайтную границу.
  //
  // Запускаем сам скрипт через node, а не обёртку из .bin: на Windows это .cmd, и execFileSync
  // отказывается его запускать (EINVAL) — Node с 20-й версии не исполняет батники без shell.
  const bin = join(clientRoot, 'node_modules', 'eslint', 'bin', 'eslint.js');
  try {
    return execFileSync(process.execPath, [bin, '.', '-f', 'json'], { cwd: clientRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  } catch (e) {
    if (typeof e.stdout === 'string' && e.stdout.trim().startsWith('[')) return e.stdout;
    // Сам eslint не запустился (нет зависимостей, сломан конфиг) — это отказ проверки, а не её итог.
    console.error('eslint не отработал:\n' + (e.stderr || e.message));
    process.exit(2);
  }
}

const results = JSON.parse(runEslint());
const counts = {};
for (const file of results)
  for (const m of file.messages)
    if (m.severity === 2) {
      const rule = m.ruleId ?? '(без правила: ошибка разбора)';
      counts[rule] = (counts[rule] ?? 0) + 1;
    }

const total = Object.values(counts).reduce((a, b) => a + b, 0);
const sorted = Object.fromEntries(Object.entries(counts).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])));

if (update) {
  writeFileSync(baselinePath, JSON.stringify(sorted, null, 2) + '\n', 'utf8');
  console.log(`Базовый уровень переписан: ${total} ошибок по ${Object.keys(sorted).length} правилам.`);
  process.exit(0);
}

if (!existsSync(baselinePath)) {
  console.error(`Нет файла базового уровня ${baselinePath}. Создайте его: node scripts/lint-ratchet.mjs --update`);
  process.exit(2);
}

const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
const worse = [];
const better = [];

for (const [rule, count] of Object.entries(counts)) {
  const allowed = baseline[rule] ?? 0;
  if (count > allowed) worse.push(`  ${rule}: было ${allowed}, стало ${count}`);
}
for (const [rule, allowed] of Object.entries(baseline)) {
  const count = counts[rule] ?? 0;
  if (count < allowed) better.push(`  ${rule}: было ${allowed}, стало ${count}`);
}

console.log(`Ошибок линта: ${total} (базовый уровень: ${Object.values(baseline).reduce((a, b) => a + b, 0)}).`);

const baselineTotal = Object.values(baseline).reduce((a, b) => a + b, 0);

if (worse.length > 0) {
  console.error('\nОшибок линта стало БОЛЬШЕ:\n' + worse.join('\n'));
  console.error(`\nПочините добавленное. Накопленные ${baselineTotal} чинить не требуется — требуется не добавлять новых.`);
  console.error('Если рост осознан и согласован, поднимите уровень: npm run lint:ratchet:update');
  process.exit(1);
}

if (better.length > 0) {
  console.log('\nСтало ЛУЧШЕ:\n' + better.join('\n'));

  // process.env.CI выставляют все известные раннеры, GitHub Actions в том числе.
  if (process.env.CI) {
    console.error('\nУровень выше достигнутого — опустите его и закоммитьте, иначе те же ошибки вернутся молча:'
      + '\n\n  npm run lint:ratchet:update\n');
    process.exit(1);
  }

  writeFileSync(baselinePath, JSON.stringify(sorted, null, 2) + '\n', 'utf8');
  console.log('\nБазовый уровень опущен по факту — закоммитьте eslint-baseline.json вместе с правкой.');
}
