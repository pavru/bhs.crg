import assert from 'node:assert/strict';
import { BASE, settled, underRoute } from './harness.mjs';
import { inStatusSheet } from './invoice-status.mjs';

const fields = ['Поставщик', 'Номер', 'Дата', 'Итого'];
const group = sheet => sheet.getByTestId('status-item-payment-requisites');
const row = (sheet, field) => group(sheet).getByTestId(`status-requisite-${field}`);
const fieldAction = (sheet, field) => row(sheet, field).locator('xpath=..')
  .getByRole('button', { name: 'К полю', exact: true });
const entry = page => page.getByRole('button', { name: 'Внести оплату…', exact: true });
const dialog = page => page.getByRole('dialog', { name: /^Оплата:/ });

async function openById(page, id) {
  await page.goto(`${BASE}/invoices?invoice=${id}`, { waitUntil: 'networkidle' });
  await page.getByTestId('invoice-header').waitFor();
  await settled(page);
}

async function checkFields(page, expected) {
  await inStatusSheet(page, async sheet => {
    await group(sheet).getByRole('heading', { name: 'Заполните реквизиты оплаты', exact: true }).waitFor();
    const rows = await group(sheet).getByRole('listitem').all();
    assert.equal(rows.length, expected.length);
    for (const field of expected) {
      await row(sheet, field).getByText(/мешает:.*оплата/).waitFor();
      assert.equal(await fieldAction(sheet, field).count(), 1);
    }
    assert.equal(await sheet.getByTestId('status-item-pay-refusal').count(), 0);
  });
}

async function goToField(page, field) {
  await inStatusSheet(page, sheet => fieldAction(sheet, field).click());
  await page.waitForFunction(key => document.activeElement?.closest('[data-readiness]')?.getAttribute('data-readiness')
    === `field:${key}`, field, { timeout: 5_000 });
}

export async function paymentRequisitesCheck(page, { api, organization, number }) {
  const { id } = await api('POST', '/costs/invoices', { requisites: {} });
  const path = `/costs/invoices/${id}`;
  const narrow = { width: 390, height: 844 };
  const original = page.viewportSize();
  await api('PUT', `${path}/lines`, { lines: [{ supplierText: 'Работы без сопоставления', amount: 100 }] });
  try {
    // Старое состояние нельзя создать штатной записью. Берём реальные препятствия из ответа этого счёта
    // и воспроизводим сохранённую до обновления ось документа на входе сводки.
    const cardRoute = `**/api${path}`;
    await underRoute(page, cardRoute, async route => {
      const response = await route.fetch();
      const view = await response.json();
      view.requisites.Состояние = 'Разобран';
      view.readiness.gaps = null;
      await route.fulfill({ response, json: view });
    }, async () => {
      await openById(page, id);
      const summary = page.getByTestId('invoice-header');
      await entry(summary).waitFor();
      assert.equal(await entry(summary).isDisabled(), true);
      await checkFields(page, fields);
      await goToField(page, 'Номер');
    });

    await page.setViewportSize(narrow);
    await openById(page, id);
    await entry(page).waitFor();
    assert.equal(await entry(page).isDisabled(), true);
    await checkFields(page, fields);
    for (const field of fields) await goToField(page, field);

    const saved = await api('GET', path);
    await api('PUT', path, {
      requisites: {
        ...saved.requisites, Номер: number, Дата: '2026-09-01',
        Поставщик: { $ref: 'catalog', entryId: organization.id },
      },
    });
    await openById(page, id);
    await checkFields(page, ['Итого']);
    await goToField(page, 'Итого');
    await page.locator('[data-readiness="field:Итого"]').getByRole('textbox').fill('100');
    await page.locator('[data-readiness="field:Итого"]').getByRole('textbox').press('Tab');
    await entry(page).click({ trial: true });
    await inStatusSheet(page, async sheet => {
      assert.equal(await group(sheet).count(), 0);
      const payer = sheet.getByTestId('status-item-gap:field:Плательщик');
      await payer.getByText('мешает: Разобран', { exact: true }).waitFor();
      assert.doesNotMatch(await payer.innerText(), /оплата/i);
    });
    await entry(page).click();
    await dialog(page).getByRole('button', { name: 'Внести оплату', exact: true }).click();
    await dialog(page).waitFor({ state: 'detached' });
    const paid = await api('GET', path);
    assert.equal(paid.payment.paid, true);
    assert.equal(paid.requisites.Состояние, 'Черновик');
    assert.equal(paid.requisites.Плательщик ?? null, null);
    assert.equal(paid.lines[0].nomenclatureId, null);
  } finally {
    await page.setViewportSize(original);
  }
}

export async function paymentRequisitesRace(page, { api, invoice, open, number, expectedStatus }) {
  const id = await invoice(number, 100, 100);
  await open(number);
  await entry(page).click();
  const payment = dialog(page);
  const submit = payment.getByRole('button', { name: 'Внести оплату', exact: true });
  await submit.click({ trial: true });
  const path = `/costs/invoices/${id}`;
  const card = await api('GET', path);
  const current = await api('PUT', path, { requisites: { ...card.requisites, Номер: null, Итого: null } });
  const paidRoute = `**/api${path}/paid`;
  const submitAndCheck = async () => {
    const [response] = await Promise.all([
      page.waitForResponse(r => r.url().endsWith(`${path}/paid`) && r.request().method() === 'POST'),
      submit.click(),
    ]);
    assert.equal(response.status(), expectedStatus);
    for (const gap of current.payment.requisiteGaps)
      await payment.getByText(gap.label, { exact: true }).waitFor();
    assert.equal(await submit.count(), 0);
    await payment.getByRole('button', { name: 'Понятно' }).click();
    await payment.waitFor({ state: 'detached' });
    await checkFields(page, ['Номер', 'Итого']);
    assert.equal(await entry(page).isDisabled(), true);
    assert.equal((await api('GET', path)).payment.paid, false);
  };
  // Для 400 передаём актуальный If-Match в запросе теста. Сервер получает старый расклад,
  // а реквизиты проверяет при актуальной версии счёта.
  if (expectedStatus === 400) {
    await underRoute(page, paidRoute, route => route.continue({
      headers: { ...route.request().headers(), 'if-match': current.version },
    }), submitAndCheck);
  } else await submitAndCheck();
}
