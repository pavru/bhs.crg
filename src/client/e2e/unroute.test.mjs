import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Перехват в живых прогонах снимают помощники обвязки, а не голый `unroute` (PR #1879).
 *
 * Снятие последнего `page.route` выключает перехват в браузере, и запрос, начатый в этот миг вслед за
 * тем, что маршрут держал, Playwright 1.62 не отпускает никогда. Голое снятие сразу по ответу
 * перехваченного запроса попадает ровно в этот миг: приложение тогда и шлёт следующий. Краснеет это
 * в шести случаях из ста и выглядит поломкой приложения — «не открылось», «не записано» (#1876).
 * `underRoute` / `unrouteQuiet` в `harness.mjs` снимают на затихшей странице.
 *
 * Сверяется текст, а не поведение: гонку одним прогоном не поймать, а пишется голое снятие
 * привычно — парой `page.route` / `page.unroute` в `finally`.
 */
const here = path.dirname(fileURLToPath(import.meta.url));
const RAW = /\.unroute(?:All)?\(/;

/**
 * Голое снятие, оставленное нарочно, — файл и сколько раз. Число точное: и новое голое снятие в
 * том же файле, и исчезнувшее старое краснеют — второе значит, что запись здесь пора убрать.
 */
const ALLOWED = {
  // `unroute('**/api/branding/logo*')` снимает НЕ последний маршрут (`/api/branding` ещё стоит) — перехват
  // не выключается. `unrouteAll({ behavior: 'wait' })` — последний шаг: страница дальше не нужна, а на ней
  // `page.clock.install()`, и `settled()` под подменёнными часами ждать не может.
  'settings-smoke.mjs': 2,
};

/** Строки кода с голым снятием; комментарии не в счёт — в них о нём и пишут. */
const rawUnroutes = file => readFileSync(path.join(here, file), 'utf8').split(/\r?\n/)
  .map((text, i) => ({ text, line: i + 1 }))
  .filter(({ text }) => !/^\s*(\/\/|\*|\/\*)/.test(text) && RAW.test(text.replace(/\/\/.*$/, '')));

describe('снятие перехвата в живых прогонах', () => {
  const files = readdirSync(here)
    // Обвязка — то место, где голое снятие и живёт, внутри помощников.
    .filter(f => f.endsWith('.mjs') && !f.endsWith('.test.mjs') && f !== 'harness.mjs');

  it('перехват снимают underRoute / unrouteQuiet обвязки, а не голый unroute', () => {
    const found = files.flatMap(f => {
      const raw = rawUnroutes(f);
      return raw.length === (ALLOWED[f] ?? 0) ? [] : raw.map(r => `${f}:${r.line}: ${r.text.trim()}`);
    });
    expect(found, 'голое снятие перехвата: оберните перехват в underRoute(page, адрес, обработчик, тело) или '
      + 'снимите посреди проверки unrouteQuiet(page, адрес) из harness.mjs. Нарочно голое — запишите в ALLOWED '
      + 'с причиной').toEqual([]);
  });

  it('записи ALLOWED не устарели', () => {
    const stale = Object.entries(ALLOWED)
      .filter(([f, n]) => !files.includes(f) || rawUnroutes(f).length !== n)
      .map(([f, n]) => `${f}: ждали ${n}, найдено ${files.includes(f) ? rawUnroutes(f).length : 'файла нет'}`);
    expect(stale).toEqual([]);
  });
});
