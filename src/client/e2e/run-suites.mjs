// Запускает несколько живых прогонов подряд и падает, если упал ХОТЬ ОДИН (issue #872).
//
// Почему не `a && b && c` в package.json. Обвязка прогонов намеренно устроена так, что провалившая
// проверка НЕ роняет прогон: `createChecks` досматривает набор до конца и печатает итог. Цепочка
// через `&&` отменяла бы это на уровень выше — первый красный прогон прятал бы три остальных, и
// каждая починка стоила бы полного круга CI, чтобы узнать следующую поломку. Здесь тот же принцип,
// что внутри прогона: сначала посмотреть всё, потом сообщить итог.
//
// Запуск:  node e2e/run-suites.mjs keyboard routing types settings

import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const suites = process.argv.slice(2);
if (!suites.length) {
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
