// Мост «Сделать источником данных» на живом экране таблицы (задача G3b, issue #1096). Своим модулем, а
// не строками в table-view-smoke.mjs: тот стоит у порога храповика размера. Вход — того прогона: лимит
// входов за прогон занят целиком.

import { callApi } from './harness.mjs';
import { TABLE_SEED } from './seed-invoices.mjs';

/**
 * Источник получает отбор ЭКРАНА, а диалог до нажатия называет колонку, которая в источник не войдёт.
 * Число строк источника сверяется с посевом: мост без отбора отдал бы всю таблицу, и это видно сразу.
 * Созданный источник удаляется — стенд не копит пробы.
 */
export async function bridgeCopiesTheScreen(page) {
  await page.getByRole('button', { name: 'Выгрузить' }).click();
  await page.getByRole('menuitem', { name: 'Сделать источником данных…' }).click();

  const dialog = page.getByRole('dialog');
  const words = (await dialog.innerText()).replace(/\s+/g, ' ');
  if (!/Не войдут: .*«Сумма»/.test(words)) throw new Error(`диалог не называет колонку доли: «${words}»`);

  const name = `Проба моста ${Date.now()}`;
  await dialog.getByLabel('Название источника').fill(name);
  const [response] = await Promise.all([
    page.waitForResponse(r => r.url().endsWith('/dataset') && r.request().method() === 'POST'),
    dialog.getByRole('button', { name: 'Создать источник' }).click(),
  ]);
  const created = await response.json();
  if (!response.ok()) throw new Error(`мост отказал: ${JSON.stringify(created)}`);

  try {
    const preview = await callApi(page, 'GET', `/datasets/sources/${created.sourceId}/preview?maxRows=1`);
    if (preview.body?.totalRows !== TABLE_SEED.count)
      throw new Error(`в источнике ${preview.body?.totalRows} строк, а в отборе экрана ${TABLE_SEED.count}`);
    if (!created.dropped?.includes('Сумма')) throw new Error(`ответ не называет непереданную «Сумму»: ${JSON.stringify(created)}`);
  } finally {
    await callApi(page, 'DELETE', `/datasets/sources/${created.sourceId}`);
  }
}
