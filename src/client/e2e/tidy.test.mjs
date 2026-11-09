import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Уборка в живых прогонах не ловит свой отказ, чтобы только его напечатать.
 *
 * Так была устроена уборка выбора подпанели в `invoice-screen-layout-checks.mjs`: на свежем стенде
 * она на каждом прогоне получала 400 и писала «! выбор подпанели не возвращён» — строку посреди
 * сотни «✓», которой не видел никто. Набор оставался зелёным, CI молчал, а настройка оставалась
 * чужой для следующих проверок. Отказ уборки обязан проваливать набор: для этого в обвязке
 * `check.tidy(что, fn)` (`harness.mjs`, `createChecks`).
 *
 * Сверяется текст, а не поведение: запустить уборку из юнит-теста нельзя, а ошибаются именно в
 * записи — `.catch(e => console.log(…))` пишется в одну строку и выглядит заботой.
 */
const here = path.dirname(fileURLToPath(import.meta.url));
const SWALLOW = /\.catch\(\s*(?:\(\s*\w*\s*\)|\w+)\s*=>\s*\{?\s*console\.(?:log|warn|error)\(/;
/** Отказ напечатан, но не проглочен: процесс с ним кончается или он брошен дальше (`seed.mjs`). */
const swallows = line => SWALLOW.test(line) && !/process\.exit|\bthrow\b/.test(line);

describe('уборка в живых прогонах', () => {
  it('отказ не ловится ради одной строки в выводе — уборка идёт через check.tidy', () => {
    const found = readdirSync(here)
      .filter(f => f.endsWith('.mjs') && !f.endsWith('.test.mjs'))
      .flatMap(f => readFileSync(path.join(here, f), 'utf8').split(/\r?\n/)
        .map((line, i) => [`${f}:${i + 1}`, line])
        .filter(([, line]) => swallows(line))
        .map(([at, line]) => `${at}  ${line.trim()}`));
    expect(found, 'уборку — через check.tidy(что, fn): её отказ проваливает набор (harness.mjs)').toEqual([]);
  });

  it('образец, который сторож обязан узнавать, он узнаёт', () => {
    for (const line of [
      "await x().catch(e => console.log('  ! не возвращён:', e.message));",
      '.catch((err) => { console.warn(err) })',
      '.catch(() => console.error("нет"))',
    ]) expect(swallows(line), line).toBe(true);
    expect(swallows("page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));")).toBe(false);
    expect(swallows("main().catch(e => { console.error('ПОСЕВ УПАЛ:', e.message); process.exit(1); });")).toBe(false);
  });
});
