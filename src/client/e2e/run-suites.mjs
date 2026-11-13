// Запускает несколько живых прогонов подряд и падает, если упал ХОТЬ ОДИН (issue #872).
//
// Почему не `a && b && c` в package.json. Обвязка прогонов намеренно устроена так, что провалившая
// проверка НЕ роняет прогон: `createChecks` досматривает набор до конца и печатает итог. Цепочка
// через `&&` отменяла бы это на уровень выше — первый красный прогон прятал бы три остальных, и
// каждая починка стоила бы полного круга CI, чтобы узнать следующую поломку. Здесь тот же принцип,
// что внутри прогона: сначала посмотреть всё, потом сообщить итог.
//
// Запуск:  node e2e/run-suites.mjs keyboard routing types settings
//          node e2e/run-suites.mjs --shard 2/3 keyboard routing …   — только вторая доля из трёх
//
// Доля (CI гоняет прогоны тремя работами) берёт из названного списка свою часть по весам из
// `shards.mjs`; почему долями, а не одновременно на одном стенде, — там же.

import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SUITE_SECONDS, parseShardArg, splitIntoShards } from './shards.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
let suites;
try {
  const { shard, rest } = parseShardArg(process.argv.slice(2));
  suites = rest;
  if (shard && rest.length) {
    const shards = splitIntoShards(rest, SUITE_SECONDS, shard.count);
    suites = shards[shard.index - 1];
    const weight = list => list.reduce((sum, name) => sum + SUITE_SECONDS[name], 0);
    console.log(`доля ${shard.index} из ${shard.count}: ${suites.join(' ')}`);
    console.log(`по весам, с: ${shards.map((list, i) => `${i + 1} — ${weight(list)}`).join(', ')}`);
  }
} catch (err) {
  console.error(err.message);
  process.exit(2);
}
if (!suites.length) {
  // В том числе пустая доля: долей больше, чем наборов. Зелёная работа, не проверившая ничего,
  // была бы враньём — пусть краснеет.
  console.error('Не названо ни одного прогона: node e2e/run-suites.mjs keyboard routing …');
  process.exit(2);
}

const results = [];
for (const name of suites) {
  console.log(`\n──────── ${name} ────────`);
  const code = await new Promise(resolve => {
    const child = spawn(process.execPath, [path.join(here, `${name}-smoke.mjs`)], {
      stdio: 'inherit',
      env: process.env,
    });
    child.on('close', resolve);
    // Прогон может не запуститься вовсе (нет файла, битый импорт). Молча зачесть это в успех
    // нельзя: набор отчитался бы «всё хорошо», не проверив ничего.
    child.on('error', err => { console.error(`  ! ${name} не запустился: ${err.message}`); resolve(1); });
  });
  results.push([name, code === 0]);
}

console.log('\n════════ итог ════════');
for (const [name, ok] of results) console.log(`  ${ok ? '✓' : '✗'} ${name}`);
const failed = results.filter(([, ok]) => !ok);
if (failed.length) {
  console.error(`\nУПАЛО ПРОГОНОВ: ${failed.length} из ${results.length} — ${failed.map(([n]) => n).join(', ')}`);
  // Код возврата, а не process.exit(): тот обрывает недописанный stdout — потерялась бы как раз
  // эта строка, единственная, где названы упавшие.
  process.exitCode = 1;
} else {
  console.log(`\nВсе ${results.length} прогонов прошли.`);
}
