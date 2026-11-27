import assert from 'node:assert/strict';
import { BASE, callApi, callApiOk, createChecks, launchBrowser, login } from './harness.mjs';

const siteId = process.env.SMOKE_WORK_SITE_ID;
if (!siteId) throw new Error('Нужна стройка и смета из node e2e/seed.mjs --work (SMOKE_WORK_SITE_ID).');
const supplier = process.env.SMOKE_SUPPLIER_EMAIL || 'snab@bhs.local';
const manager = process.env.SMOKE_PM_EMAIL || 'pm@bhs.local';
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const root = '/costs/requests';
const { check, summarize } = createChecks();
const browser = await launchBrowser();
const viewport = { width: 1480, height: 960 };
const button = (scope, name) => scope.getByRole('button', { name, exact: true });
let request;
const errors = [];

async function responseFor(page, method, path, action, status = 200) {
  const [response] = await Promise.all([
    page.waitForResponse(res => res.request().method() === method && new URL(res.url()).pathname === `/api${path}`),
    action(),
  ]);
  assert.equal(response.status(), status, await response.text());
  return response;
}

async function invoiceFor(page, materialId) {
  const invoice = await callApiOk(page, 'POST', '/costs/invoices', {
    requisites: { Номер: `П6-${Date.now()}`, Итого: 30 },
  });
  const path = `/costs/invoices/${invoice.id}`;
  const lines = await callApiOk(page, 'PUT', `${path}/lines`, {
    lines: [{ supplierText: 'Материал заявки', nomenclature: { $ref: 'catalog', entryId: materialId },
      quantity: 3, unit: request.lines[0].unit, price: 10 }],
  });
  return callApiOk(page, 'PUT', `${path}/lines/${lines.lines[0].id}/allocation`, {
    parts: [{ construction: siteId, quantity: 3 }],
  });
}

try {
  const page = await browser.newPage({ viewport });
  page.setDefaultTimeout(12_000);
  page.on('pageerror', error => errors.push(error.message));
  await login(page, supplier, password);
  await page.goto(`${BASE}/purchase-requests?construction=${siteId}`);
  const site = (await callApiOk(page, 'GET', `${root}/constructions`)).find(row => row.id === siteId);
  const choices = await callApiOk(page, 'GET', `${root}/estimate-items?constructionId=${siteId}`);

  await check('снабженец видит заявки и позиции сметы без доступа к экрану Plan', async () => {
    assert(site);
    assert.equal(choices.state, 'shown');
    assert(choices.items.length > 0);
    assert.equal((await callApi(page, 'GET', `/plan/estimates?constructionId=${siteId}`)).status, 403);
    await page.getByRole('heading', { name: 'Заявки на закупку', exact: true }).waitFor();
    await button(page, 'Новая из сметы').click();
    const dialog = page.getByRole('dialog', { name: 'Новая заявка из сметы' });
    await dialog.getByRole('checkbox', { name: choices.items[0].name }).check();
  });

  await check('предложение не записывает, после ручной правки создаётся черновик', async () => {
    const before = await callApiOk(page, 'GET', `${root}?constructionId=${siteId}`);
    const dialog = page.getByRole('dialog', { name: 'Новая заявка из сметы' });
    await responseFor(page, 'POST', `${root}/draft-from-estimate`, () => button(dialog, 'Рассчитать предложение').click());
    await dialog.getByText(/Другие заявки не резервируют/).waitFor();
    const after = await callApiOk(page, 'GET', `${root}?constructionId=${siteId}`);
    assert.deepEqual(after.items.map(item => item.id), before.items.map(item => item.id));
    await dialog.getByLabel('Количество в заявке').first().fill('42');
    const saved = await responseFor(page, 'POST', root, () => button(dialog, 'Сохранить черновик').click(), 201);
    request = await saved.json();
    assert.equal(request.status, 'draft');
    assert.equal(request.lines[0].quantity, 42);
    await page.getByRole('heading', { name: `Заявка № ${request.number}`, exact: true }).waitFor();
  });

  await check('подача использует версию заявки', async () => {
    assert(request, 'Не создан черновик.');
    const detail = page.getByTestId('purchase-detail');
    await button(detail, 'Подать заявку').click();
    const confirm = page.getByRole('alertdialog').or(page.getByRole('dialog'));
    const response = await responseFor(page, 'POST', `${root}/${request.id}/submit`,
      () => button(confirm, 'Подать заявку').click());
    assert(response.request().headers()['if-match']);
    assert.equal((await response.json()).status, 'submitted');
    await detail.getByText('Подана', { exact: true }).waitFor();
    assert.equal(await button(detail, 'Изменить строки').count(), 0);
  });

  await check('привязка в шапке счёта и количество в карточке', async () => {
    assert(request, 'Не создана заявка.');
    const invoice = await invoiceFor(page, request.lines[0].nomenclatureId);
    await page.goto(`${BASE}/invoices?invoice=${invoice.id}`);
    const picker = page.getByTestId('invoice-purchase-request').getByRole('combobox', { name: 'Заявка на закупку' });
    if (!(await picker.isVisible())) {
      const part = page.getByRole('button', { name: /Кто и сколько/ }).first();
      if (await part.count() && (await part.getAttribute('aria-expanded')) === 'false') await part.click();
    }
    await picker.click();
    await page.getByRole('option', { name: new RegExp(`^№ ${request.number} ·`) }).click();
    assert.equal((await callApiOk(page, 'GET', `/costs/invoices/${invoice.id}`)).purchaseRequestId, null);
    const saved = await responseFor(page, 'PUT', `/costs/invoices/${invoice.id}`,
      () => button(page.getByTestId('invoice-purchase-request'), 'Сохранить связь').click());
    assert.equal((await saved.json()).purchaseRequestId, request.id);
    assert(saved.request().headers()['if-match']);
    const view = await callApiOk(page, 'GET', `${root}/${request.id}`);
    const material = view.quantities.items.find(item => item.nomenclatureId === request.lines[0].nomenclatureId);
    assert.equal(material.invoicedQuantity, 3);
  });

  await check('на узком экране менеджер видит заявку и причину скрытого количества', async () => {
    assert(request, 'Не создана заявка.');
    const phone = await browser.newPage({ viewport: { width: 390, height: 844 } });
    phone.setDefaultTimeout(12_000);
    phone.on('pageerror', error => errors.push(error.message));
    await login(phone, manager, password);
    await phone.goto(`${BASE}/purchase-requests?request=${request.id}`);
    await phone.getByText('«Выставлено» скрыто. Нужно право чтения счетов.', { exact: true }).waitFor();
    await phone.locator('dt').filter({ hasText: 'Отпущено по всей стройке' }).first().waitFor();
    assert.equal(await phone.evaluate(() => document.documentElement.scrollWidth), 390);
    await button(phone, 'К списку').click();
    await phone.getByRole('combobox', { name: 'Стройка', exact: true }).waitFor();
    await phone.close();
  });
  await check('браузер без исключений', () => assert.deepEqual(errors, []));
} finally {
  await browser.close();
}
process.exitCode = summarize('Заявки на закупку П6');
