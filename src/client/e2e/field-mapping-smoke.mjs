// Smoke экрана «Сопоставление полей» (issue #1101).
//
// Экран показывает, что модуль счетов читает из справочников ядра, и назначает поле — правкой схемы
// типа, тем же адресом, что редактор типов. Проверяется главное: «назначено» на экране совпадает с
// тем, что отдаёт сервер, а выбор другого поля ДЕЙСТВИТЕЛЬНО переносит тэг — и обратно.
//
// ⚠️ Прогон ПИШЕТ: переносит тэг «ИНН организации» на другое поле и возвращает на место. На время
// между двумя сохранениями поиск поставщика по ИНН смотрит в другое поле. Возврат продублирован в
// `finally` запросом к приложению — на случай, если прогон упал посередине.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и справочника организаций с ролью (посев его
// заводит). Учётка — админская.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/field-mapping-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, until } from './harness.mjs';

const TAX_ID = 'org.inn';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, skip, summarize } = createChecks();

await login(page);

/** Строка «ИНН организации» модуля счетов — как её отдаёт сервер. */
async function taxIdRow() {
  const { status, body } = await callApi(page, 'GET', '/field-mapping');
  if (status !== 200) throw new Error(`адрес сопоставления ответил ${status}`);
  const costs = body.modules.find(m => m.code === 'costs');
  if (!costs) throw new Error('модуля счетов в ответе нет — он выключен?');
  const group = costs.groups.find(g => g.role === 'role.organization');
  const row = group?.rows.find(r => r.tag === TAX_ID);
  if (!row) throw new Error(`справочник организаций не назначен (${group?.state}) — проверять не на чем`);
  return { costs, group, row };
}

/** Поставить тэг ИНН на поле запросом к приложению — возврат на место, если экран не справился. */
async function placeTaxId(field) {
  const type = (await callApi(page, 'GET', `/document-types/${field.declaredIn.id}`)).body;
  const schema = type.schema;
  schema.fields = schema.fields.map(f => {
    const tags = (f.tags ?? []).filter(t => t !== TAX_ID);
    return { ...f, tags: f.key === field.key ? [...tags, TAX_ID] : tags };
  });
  await callApi(page, 'PUT', `/document-types/${type.id}/schema`, { schema: JSON.stringify(schema) });
}

const start = await taxIdRow();
const original = start.row.fields.find(f => f.key === start.row.key);
// Другое подходящее поле ТОГО ЖЕ типа: перенос между типами — отдельный путь, здесь не он.
const other = original && start.row.fields.find(f =>
  f.fits && !f.tagged && f.declaredIn.id === original.declaredIn.id);

const tableRow = page.locator(`[data-tag="${TAX_ID}"]`);
const select = tableRow.getByRole('combobox');

async function pick(field) {
  await select.click();
  await page.getByRole('option').filter({ hasText: field.title }).first().click();
  await settled(page);
}

async function save() {
  await page.locator('[data-role="role.organization"]').getByRole('button', { name: 'Сохранить' }).click();
  await settled(page);
}

try {

await page.goto(`${BASE}/field-mapping`);
await settled(page);

await check('screen-shows-what-server-resolves', async () => {
  if (!original) throw new Error(`ИНН не назначен (${start.row.state}) — посев обязан заводить справочник с тэгами`);
  const text = await page.locator('body').innerText();
  for (const expected of ['Сопоставление полей', start.group.label, `тип «${start.group.type.name}»`,
    'ИНН организации', 'Название организации']) {
    if (!text.includes(expected)) throw new Error(`на экране нет «${expected}»`);
  }
  const shown = (await select.innerText()).trim();
  if (!shown.startsWith(original.title)) throw new Error(`в списке стоит «${shown}», а сервер назвал «${original.title}»`);
  // У назначенной строки состояние пусто и сохранять нечего — кнопки нет вовсе.
  if (await page.getByRole('button', { name: 'Сохранить' }).count())
    throw new Error('кнопка «Сохранить» видна без единой правки');
});

if (!original || !other) {
  skip('pick-moves-the-tag-and-back', 'в справочнике организаций нет второго поля, подходящего для ИНН');
} else {
  await check('pick-moves-the-tag-and-back', async () => {
    await pick(other);
    if (!(await tableRow.innerText()).includes('не сохранено')) throw new Error('правка не помечена «не сохранено»');
    await save();
    await until(async () => {
      const now = (await taxIdRow()).row;
      if (now.key !== other.key) throw new Error(`тэг не переехал: сервер называет «${now.key}»`);
    });

    await pick(original);
    await save();
    await until(async () => {
      const now = (await taxIdRow()).row;
      if (now.key !== original.key || now.state !== 'mapped')
        throw new Error(`тэг не вернулся: «${now.key}», состояние ${now.state}`);
    });
    if (await page.getByRole('button', { name: 'Сохранить' }).count())
      throw new Error('после сохранения кнопка «Сохранить» осталась');
  });
}

await check('settings-carry-the-same-summary', async () => {
  const { costs } = await taxIdRow();
  await page.goto(`${BASE}/settings`);
  await settled(page);
  const section = page.locator('button').filter({ hasText: 'Счета и накладные' }).first();
  if ((await section.getAttribute('aria-expanded')) !== 'true') { await section.click(); await settled(page); }
  const signal = (await page.locator('[data-mapping-signal]').innerText()).trim();
  const expected = costs.unmet === 0
    ? `назначены все ${costs.total}`
    : `не назначено ${costs.unmet} из ${costs.total}`;
  if (!signal.includes(expected)) throw new Error(`в настройках «${signal}», а экран считает «${expected}»`);
  // Ссылка ведёт на экран — у администратора право на типы есть.
  await page.locator('[data-mapping-signal]').getByRole('button', { name: 'Сопоставление полей' }).click();
  await settled(page);
  if (!page.url().endsWith('/field-mapping')) throw new Error(`ссылка увела на ${page.url()}`);
});

} finally {
  // Упали между двумя сохранениями — тэг остался бы на чужом поле, и у соседних прогонов
  // перестал бы находиться поставщик по ИНН.
  if (original) {
    const now = await taxIdRow().catch(() => null);
    if (now && now.row.key !== original.key) {
      console.log('  ! тэг ИНН не на своём поле — возвращаю запросом');
      await placeTaxId(original);
    }
  }
  await browser.close();
}

process.exitCode = summarize('Smoke сопоставления полей');
