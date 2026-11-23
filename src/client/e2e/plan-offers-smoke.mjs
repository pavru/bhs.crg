// Plan-17. Живая дверь КП из первой синтетической редакции: права, версия записи, печать и история.
import assert from 'node:assert/strict';
import { BASE, callApi, callApiOk, createChecks, launchBrowser, login, settled } from './harness.mjs';

const estimateId = process.env.SMOKE_ESTIMATE_ID;
const seedId = process.env.SMOKE_OFFER_ID;
const managerEmail = process.env.SMOKE_PM_EMAIL || 'pm@bhs.local';
const estimatorEmail = process.env.SMOKE_ESTIMATOR_EMAIL || 'smeta@bhs.local';
const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
if (!estimateId || !seedId) throw new Error('Нужны SMOKE_ESTIMATE_ID и SMOKE_OFFER_ID из посева (node e2e/seed.mjs).');

const browser = await launchBrowser();
const viewport = { width: 1500, height: 1000 };
const { check, summarize } = createChecks();
const errors = [];
const matches = (method, path) => response => response.request().method() === method && new URL(response.url()).pathname === `/api${path}`;
const button = (page, name) => page.getByRole('button', { name, exact: true });
const tab = (page, name) => page.getByRole('tab', { name, exact: true });

async function checkedResponse(response, expected) {
  const reply = await response;
  assert.equal(reply.status(), expected, `${reply.request().method()} ${reply.url()} → ${reply.status()}: ${await reply.text()}`);
  return reply;
}

async function requestDuring(page, method, path, action) {
  const [response] = await Promise.all([page.waitForResponse(matches(method, path)), action()]);
  return response;
}

async function concurrentMarkups(page, id, version, markup) {
  return page.evaluate(async ({ id, version, markup }) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const response = await fetch(`/api/plan/offers/${id}/markups`, { method: 'PUT',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'If-Match': version },
      body: JSON.stringify({ markup, sections: [], lines: [] }) });
    return { status: response.status, data: await response.json() };
  }, { id, version, markup });
}

try {
  const manager = await browser.newPage({ viewport });
  manager.on('pageerror', error => errors.push(error.message));
  await login(manager, managerEmail, password);
  const estimate = await callApiOk(manager, 'GET', `/plan/estimates/${estimateId}`);
  const source = estimate.revisions.find(row => row.number === 1);
  assert(source, 'В смете посева нужна редакция 1.');
  let offer;

  await check('manager-reads-offers-and-creates-from-selected-revision', async () => {
    const seeded = await callApiOk(manager, 'GET', `/plan/offers/${seedId}`);
    await manager.goto(`${BASE}/offers`);
    await manager.getByRole('heading', { name: 'Коммерческие предложения', exact: true }).waitFor();
    await manager.getByRole('link', { name: `Открыть ${seeded.number}`, exact: true }).waitFor();
    await manager.goto(`${BASE}/estimates/${estimateId}`);
    await manager.getByRole('tab', { name: /^Ред\. 1(?:\s|$)/ }).click();
    await button(manager, 'Сделать КП…').click();
    const dialog = manager.getByRole('dialog', { name: 'Сделать КП из редакции 1' });
    const created = await requestDuring(manager, 'POST', '/plan/offers', () => button(dialog, 'Создать черновик').click());
    const reply = await checkedResponse(created, 201);
    assert.equal(reply.request().postDataJSON().revisionId, source.id);
    offer = await reply.json();
    await manager.waitForURL(`**/offers/${offer.id}`);
    await manager.getByRole('columnheader', { name: 'Цена продажи', exact: true }).waitFor();
    assert(!await manager.getByRole('columnheader', { name: 'Себестоимость', exact: true }).count());
    assert(!offer.cost && offer.lines.every(row => !row.cost), 'У менеджера отсутствуют данные себестоимости.');
    return true;
  });

  if (offer) {
    const offerPath = `/plan/offers/${offer.id}`;
    await check('manager-recovers-from-if-match-conflict-with-input', async () => {
      await tab(manager, 'Наценки').click();
      const markup = manager.getByRole('textbox', { name: 'Наценка КП', exact: true });
      await markup.fill('17');
      const raced = await concurrentMarkups(manager, offer.id, offer.version, 11);
      assert.equal(raced.status, 200, JSON.stringify(raced.data));
      const stale = await requestDuring(manager, 'PUT', `${offerPath}/markups`, () => button(manager, 'Сохранить наценки').click());
      const rejected = await checkedResponse(stale, 409);
      assert.equal(await rejected.request().headerValue('if-match'), offer.version);
      assert.equal(await markup.inputValue(), '17');
      await button(manager, 'Перечитать КП').click();
      await manager.getByText(/КП перечитано/).waitFor();
      assert.equal(await markup.inputValue(), '17');
      const saved = await requestDuring(manager, 'PUT', `${offerPath}/markups`, () => button(manager, 'Сохранить наценки').click());
      const reply = await checkedResponse(saved, 200);
      assert.equal(await reply.request().headerValue('if-match'), raced.data.version);
      offer = await reply.json();
      assert.equal(offer.markup, 17);
    });

    await check('manager-prints-sends-and-opens-frozen-version', async () => {
      await tab(manager, 'Строки и итог').click();
      const printPath = `/print/${encodeURIComponent('КоммерческоеПредложение')}/${offer.id}/preview`;
      const pdf = await requestDuring(manager, 'POST', printPath, () => button(manager, 'Печать…').click());
      assert.equal(pdf.status(), 200, `Печать КП → ${pdf.status()}`);
      assert.equal((await pdf.body()).subarray(0, 4).toString(), '%PDF');
      const [download] = await Promise.all([
        manager.waitForEvent('download'), button(manager, 'Скачать PDF').click(),
      ]);
      assert.equal(download.suggestedFilename(), `${offer.number}.pdf`);
      await manager.getByRole('dialog').getByRole('button', { name: 'Закрыть', exact: true }).click();

      await button(manager, 'Отметить отправленным…').click();
      const send = manager.getByRole('dialog', { name: 'Отметить КП отправленным' });
      await send.getByText(/только новой версией/).waitFor();
      const sending = await requestDuring(manager, 'POST', `${offerPath}/send`, () => button(send, 'Отметить отправленным').click());
      const sent = await (await checkedResponse(sending, 200)).json();
      assert.equal(sent.status, 'sent');
      await button(manager, 'Новая версия…').waitFor();
      await tab(manager, 'Наценки').click();
      assert(!await manager.getByRole('textbox', { name: 'Наценка КП' }).count(), 'У отправленного КП есть поле правки.');

      await button(manager, 'Новая версия…').click();
      const next = await requestDuring(manager, 'POST', `${offerPath}/versions`, () =>
        button(manager.getByRole('dialog', { name: 'Новая версия КП' }), 'Создать версию').click());
      const draft = await (await checkedResponse(next, 200)).json();
      assert.equal(draft.currentVersion, sent.currentVersion + 1);
      await tab(manager, 'Версии').click();
      await manager.getByRole('link', { name: 'Открыть версию 1', exact: true }).click();
      await manager.getByRole('heading', { name: 'Версия 1 · только чтение', exact: true }).waitFor();
      return true;
    });

    await check('manager-rebases-draft-only-after-preview', async () => {
      await manager.getByRole('link', { name: 'К текущей версии', exact: true }).click();
      await button(manager, 'Обновить по смете…').click();
      const rebaseDialog = manager.getByRole('dialog', { name: 'Обновить КП по смете' });
      await rebaseDialog.getByText(/Редакция 1 → редакция/).waitFor();
      const rebased = await requestDuring(manager, 'POST', `${offerPath}/rebase`, () => button(rebaseDialog, 'Обновить черновик').click());
      const reply = await checkedResponse(rebased, 200);
      const { offer: updated } = await reply.json();
      assert.notEqual(updated.revision.id, source.id);
      assert.equal(updated.currentVersion, 2);
      assert.equal(updated.status, 'draft');
    });
  }

  const estimator = await browser.newPage({ viewport });
  estimator.on('pageerror', error => errors.push(error.message));
  await login(estimator, estimatorEmail, password);
  await check('estimator-does-not-see-offers-or-selling-prices', async () => {
    await estimator.goto(`${BASE}/estimates/${estimateId}`);
    await estimator.getByRole('heading', { name: estimate.name, exact: true }).waitFor();
    await settled(estimator);
    await estimator.getByTestId('section-panel').waitFor();
    assert(!await estimator.getByTestId('section-panel').getByRole('link', { name: 'Коммерческие предложения' }).count());
    assert(!await button(estimator, 'Сделать КП…').count());
    assert(!await estimator.getByRole('columnheader', { name: 'Цена продажи' }).count());
    assert.equal((await callApi(estimator, 'GET', '/plan/offers')).status, 403);
    assert.equal((await callApi(estimator, 'GET', `/plan/offers/${seedId}`)).status, 403);
    const requests = [];
    const record = request => { if (new URL(request.url()).pathname.startsWith('/api/plan/offers')) requests.push(request.url()); };
    estimator.on('request', record);
    await estimator.goto(`${BASE}/offers/${seedId}`);
    await estimator.getByRole('heading', { name: 'Раздел «Коммерческие предложения» недоступен', exact: true }).waitFor();
    await estimator.getByText('plan.offer.read', { exact: true }).waitFor();
    estimator.off('request', record);
    assert.deepEqual(requests, []);
    assert(!await estimator.getByRole('columnheader', { name: 'Цена продажи' }).count());
    return true;
  });

  await check('no-browser-errors', () => assert.deepEqual(errors, []));
  process.exitCode = summarize('Коммерческие предложения');
} finally {
  await browser.close();
}
