import assert from 'node:assert/strict';
import { BASE, callApiOk } from './harness.mjs';

const date = '2020-01-01';
const roleTitle = 'Редактор прайс-листа без справочника (посев)';
export const PRICE_EDITOR_EMAIL = 'plan-price-editor@bhs.local';
const button = (page, name) => page.getByRole('button', { name, exact: true });
const apiPath = (request, path) => new URL(request.url()).pathname === `/api${path}`;

export async function preparePriceChecks(admin, password) {
  const roles = await callApiOk(admin, 'GET', '/roles');
  const role = roles.find(row => row.title === roleTitle) ?? await callApiOk(admin, 'POST', '/roles', {
    title: roleTitle, summary: 'Синтетическая роль прогона Plan-17',
    permissions: ['plan.offer.read', 'plan.offer.edit', 'plan.pricelist.edit', 'core.constructions.read'],
  });
  const users = await callApiOk(admin, 'GET', '/users');
  if (!(Array.isArray(users) ? users : users.items).some(row => row.email === PRICE_EDITOR_EMAIL)) {
    await callApiOk(admin, 'POST', '/users', { email: PRICE_EDITOR_EMAIL, displayName: 'Редактор цен (посев)', password, roles: [role.name] });
  }
  const types = await callApiOk(admin, 'GET', '/document-types');
  const unitType = types.find(row => row.code === 'ЕдиницаИзмерения');
  assert(unitType, 'Посев единиц сметы обязателен.');
  const units = await callApiOk(admin, 'GET', `/common-data?purpose=choice&typeId=${unitType.id}`);
  const unit = units.find(row => row.displayName === 'шт');
  assert(unit, 'В посеве нет единицы «шт».');
  const suffix = Date.now().toString(36).toUpperCase();
  async function work(code, name) {
    return callApiOk(admin, 'POST', '/work-types', {
      displayName: name,
      data: JSON.stringify({ Код: code, Наименование: name, ЕдиницаИзмерения: { $ref: 'catalog', entryId: unit.id, displayName: 'шт' } }),
    });
  }
  return {
    priced: await work(`17A${suffix}`, `Монтаж лотка ${suffix} (посев)`),
    unpriced: await work(`17B${suffix}`, `Наладка ${suffix} (посев)`),
  };
}

async function choose(page, label, item) {
  await page.getByRole('combobox', { name: label, exact: true }).click();
  await page.getByRole('option', { name: item }).click();
}

async function addLine(page, name, index) {
  await choose(page, 'Вид работы', name);
  await button(page, 'Добавить строку').click();
  await page.getByRole('textbox', { name: new RegExp(`^Объём строки ${index}:`) }).fill('2');
}

export async function checkPriceList({ check, manager, editor, fixtures, constructionId, concurrentWrite }) {
  const { priced, unpriced } = fixtures;
  const pricedName = new RegExp(priced.displayName.replace(/\(|\)/g, '\\$&'));
  const unpricedName = new RegExp(unpriced.displayName.replace(/\(|\)/g, '\\$&'));
  const site = (await callApiOk(manager, 'GET', '/constructions/directory')).find(row => row.id === constructionId);
  assert(site, 'Менеджер не видит стройку посева.');
  const path = `/plan/pricelist/${priced.id}`;
  let savedPrice;

  await check('manager-chooses-new-work-type-and-adds-price', async () => {
    await manager.goto(`${BASE}/price-list`);
    await button(manager, 'Добавить вид в прайс-лист…').click();
    await choose(manager, 'Вид работы', pricedName);
    await manager.getByRole('textbox', { name: 'Цена за единицу' }).fill('100');
    const [response] = await Promise.all([manager.waitForResponse(r => apiPath(r.request(), path) && r.request().method() === 'PUT'),
      button(manager, 'Сохранить цену').click()]);
    assert.equal(response.status(), 200, await response.text());
    assert.equal(await response.request().headerValue('if-match'), null);
    savedPrice = await response.json();
    await manager.getByRole('rowheader', { name: pricedName }).waitFor();
  });

  await check('custom-price-editor-keeps-existing-actions-and-does-not-request-catalog', async () => {
    assert(savedPrice, 'Цена не заведена.');
    const catalog = [];
    const record = request => { if (apiPath(request, '/work-types')) catalog.push(request.url()); };
    editor.on('request', record);
    try {
      await editor.goto(`${BASE}/price-list`);
      await editor.getByText(/core.catalog.read/).waitFor();
      assert.equal(await button(editor, 'Добавить вид в прайс-лист…').count(), 0);
      await editor.getByRole('rowheader', { name: pricedName }).click();
      await editor.getByRole('button', { name: /Изменить цену с/ }).first().click();
      const field = editor.getByRole('textbox', { name: 'Цена за единицу' });
      await field.fill('140');
      const race = await concurrentWrite(manager, 'PUT', path, savedPrice.version, { validFrom: savedPrice.validFrom, price: 130 });
      assert.equal(race.status, 200, JSON.stringify(race.data));
      const [stale] = await Promise.all([editor.waitForResponse(r => apiPath(r.request(), path) && r.request().method() === 'PUT'),
        button(editor, 'Сохранить цену').click()]);
      assert.equal(stale.status(), 409);
      assert.equal(await stale.request().headerValue('if-match'), savedPrice.version);
      assert.equal(await field.inputValue(), '140');
      await button(editor, 'Перечитать цену').click();
      await editor.getByText(/Цена в прайс-листе теперь/).waitFor();
      assert.equal(await field.inputValue(), '140');
      const [updated] = await Promise.all([editor.waitForResponse(r => apiPath(r.request(), path) && r.request().method() === 'PUT'),
        button(editor, 'Сохранить цену').click()]);
      assert.equal(updated.status(), 200, await updated.text());
      assert.equal(await updated.request().headerValue('if-match'), race.data.version);
      await button(editor, 'Закрыть').click();
      await button(editor, 'КП по прайс-листу…').click();
      await editor.getByText(/Можно выбрать позицию из прайс-листа/).waitFor();
      await editor.getByRole('combobox', { name: 'Вид работы', exact: true }).click();
      await editor.getByRole('option', { name: pricedName }).waitFor();
      assert.equal(await editor.getByRole('option', { name: unpricedName }).count(), 0);
      assert.deepEqual(catalog, []);
    } finally { editor.off('request', record); }
  });

  await check('manager-creates-offer-with-unpriced-line-and-confirms-repricing', async () => {
    await manager.goto(`${BASE}/offers`);
    await button(manager, 'КП по прайс-листу…').click();
    await choose(manager, 'Стройка', site.name);
    await addLine(manager, pricedName, 1);
    await addLine(manager, unpricedName, 2);
    const [created] = await Promise.all([manager.waitForResponse(r => apiPath(r.request(), '/plan/offers/from-pricelist')),
      button(manager, 'Создать черновик').click()]);
    assert.equal(created.status(), 201, await created.text());
    const offer = await created.json();
    assert.equal(offer.source, 'pricelist');
    assert(offer.money.incomplete);
    const line = offer.lines.find(row => row.workTypeId === unpriced.id);
    assert.equal(line.price, null);
    assert.equal(line.unit, 'шт');
    assert(line.marks.some(mark => mark.code === 'no-price'));
    const result = manager.getByRole('dialog', { name: `Создан черновик ${offer.number}` });
    await result.getByText('Нет цены в прайс-листе', { exact: true }).waitFor();
    await result.getByText('Неполный итог', { exact: true }).waitFor();
    await button(result, 'Открыть КП').click();
    await manager.waitForURL(`**/offers/${offer.id}`);
    const added = await callApiOk(manager, 'PUT', `/plan/pricelist/${unpriced.id}`, { validFrom: date, price: 80 });
    const original = await callApiOk(manager, 'GET', `/plan/offers/${offer.id}`);
    assert(original.money.incomplete);
    await button(manager, 'Пересчитать по прайс-листу…').click();
    await button(manager, 'Показать изменения').click();
    const confirm = button(manager, 'Подтвердить пересчёт');
    await confirm.waitFor();
    assert.equal((await concurrentWrite(manager, 'PUT', `/plan/pricelist/${unpriced.id}`, added.version,
      { validFrom: date, price: 90 })).status, 200);
    await confirm.click();
    await manager.getByText(/изменились после предпросмотра/).waitFor();
    await button(manager, 'Перечитать КП').click();
    await button(manager, 'Показать изменения').click();
    const writePath = `/plan/offers/${offer.id}/reprice-from-pricelist`;
    const [repriced] = await Promise.all([manager.waitForResponse(r => apiPath(r.request(), writePath) && !r.request().postDataJSON().preview),
      confirm.click()]);
    assert.equal(repriced.status(), 200, await repriced.text());
    const { offer: updated } = await repriced.json();
    assert.equal(updated.money.incomplete, false);
    assert.equal(updated.lines.find(row => row.workTypeId === unpriced.id).price, 90);
  });
}
