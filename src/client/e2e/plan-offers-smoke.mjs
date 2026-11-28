// Plan-17. Живая дверь КП из первой синтетической редакции: права, версия записи, печать и история.
import assert from 'node:assert/strict';
import { BASE, EMAIL, PASSWORD, callApi, callApiOk, createChecks, launchBrowser, login, settled } from './harness.mjs';
import { PRICE_EDITOR_EMAIL, checkPriceList, preparePriceChecks } from './plan-price-list-checks.mjs';

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

async function concurrentWrite(page, method, path, version, body) {
  return page.evaluate(async ({ method, path, version, body }) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const response = await fetch(`/api${path}`, { method,
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'If-Match': version },
      body: JSON.stringify(body) });
    return { status: response.status, data: await response.json() };
  }, { method, path, version, body });
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
    await check('manager-opens-current-offer-in-table-and-exports-one-row', async () => {
      await manager.getByRole('link', { name: 'Открыть таблицей', exact: true }).click();
      await manager.waitForURL(url => url.pathname === '/tables/plan.offers/registry');
      await manager.getByRole('cell', { name: offer.number, exact: true }).waitFor();
      await manager.getByRole('button', { name: 'Выгрузить', exact: true }).click();
      const exportItem = manager.getByRole('menuitem', { name: 'В XLSX · 1 строка', exact: true });
      const [response, download] = await Promise.all([
        manager.waitForResponse(matches('POST', '/tables/plan.offers/export')),
        manager.waitForEvent('download'),
        exportItem.click(),
      ]);
      assert.equal(response.status(), 200);
      assert.equal((await response.body()).subarray(0, 2).toString(), 'PK');
      assert(download.suggestedFilename().endsWith('.xlsx'));
      await manager.goto(`${BASE}/offers/${offer.id}`);
    });

    await check('manager-recovers-from-if-match-conflict-with-input', async () => {
      await tab(manager, 'Наценки').click();
      const markup = manager.getByRole('textbox', { name: 'Наценка КП', exact: true });
      await markup.fill('17');
      const raced = await concurrentWrite(manager, 'PUT', `${offerPath}/markups`, offer.version, { markup: 11, sections: [], lines: [] });
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

    await check('manager-prints-and-downloads-default-form', async () => {
      await tab(manager, 'Наценки').click();
      const print = button(manager, 'Печать…');
      await manager.getByRole('textbox', { name: 'Наценка КП', exact: true }).fill('18');
      await manager.getByText('Сохраните или отмените правки наценок, чтобы печатать или изменять КП.').waitFor();
      assert(await print.isDisabled(), 'Печать доступна при несохранённых наценках.');
      await tab(manager, 'Строки и итог').click();
      assert(await print.isDisabled(), 'Печать снова доступна после смены вкладки.');
      await button(manager, 'К наценкам').click();
      await button(manager, 'Отменить правки').click();
      const printPath = `/print/${encodeURIComponent('КоммерческоеПредложение')}/${offer.id}/preview`;
      try {
        const pdf = await requestDuring(manager, 'POST', printPath, () => print.click());
        assert.equal(pdf.status(), 200, `Печать КП → ${pdf.status()}`);
        assert.equal((await pdf.body()).subarray(0, 4).toString(), '%PDF');
        const [download] = await Promise.all([
          manager.waitForEvent('download'), button(manager, 'Скачать PDF').click(),
        ]);
        assert.equal(download.suggestedFilename(), `${offer.number}.pdf`);
      } finally {
        const close = button(manager.getByRole('dialog'), 'Закрыть');
        if (await close.isVisible()) await close.click();
      }
    });

    await check('manager-sends-and-opens-frozen-version', async () => {
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

    await check('manager-returns-from-history-without-discarding-input', async () => {
      await button(manager, 'К текущей версии').click();
      await tab(manager, 'Наценки').click();
      const markup = manager.getByRole('textbox', { name: 'Наценка КП', exact: true });
      await markup.fill('19');
      await tab(manager, 'Версии').click();
      await manager.getByRole('link', { name: 'Открыть версию 1', exact: true }).click();
      await manager.getByRole('heading', { name: 'Версия 1 · только чтение', exact: true }).waitFor();
      await button(manager, 'К текущей версии').click();
      await manager.getByRole('link', { name: 'Открыть версию 1', exact: true }).waitFor();
      assert(!await manager.getByRole('dialog').count(), 'Внутри карточки открылся диалог ухода.');
      await tab(manager, 'Наценки').click();
      assert.equal(await markup.inputValue(), '19');
      await button(manager, 'Отменить правки').click();
    });

    await check('manager-rebases-draft-only-after-preview', async () => {
      await button(manager, 'Обновить по смете…').click();
      const rebaseDialog = manager.getByRole('dialog', { name: 'Обновить КП по смете' });
      await rebaseDialog.getByText(/Редакция 1 → редакция/).waitFor();
      const rebased = await requestDuring(manager, 'POST', `${offerPath}/rebase`, () => button(rebaseDialog, 'Обновить черновик').click());
      const reply = await checkedResponse(rebased, 200);
      const { offer: updated } = await reply.json();
      assert.notEqual(updated.revision.id, source.id);
      assert.equal(updated.currentVersion, 2);
      assert.equal(updated.status, 'draft');
      await manager.goto(`${BASE}/estimates/${estimateId}`);
      await manager.getByRole('tab', { name: /^Ред\. 1(?:\s|$)/ }).click();
      const derivatives = manager.getByRole('region', { name: 'КП редакции' });
      const old = derivatives.getByRole('link', { name: `Открыть ${updated.number}, версия 1` });
      assert((await old.getAttribute('href')).endsWith('?version=1'));
      await old.click();
      await manager.getByRole('heading', { name: 'Версия 1 · только чтение', exact: true }).waitFor();
      await manager.getByRole('link', { name: 'К смете', exact: true }).click();
      await old.waitFor();
    });

    await check('manager-must-review-again-if-rebase-preview-is-for-concurrently-sent-version', async () => {
      const draft = await callApiOk(manager, 'POST', '/plan/offers', { revisionId: source.id, date: offer.date, markup: 8 });
      const path = `/plan/offers/${draft.id}`;
      await manager.goto(`${BASE}/offers/${draft.id}`);
      await button(manager, 'Обновить по смете…').waitFor();
      const sent = await concurrentWrite(manager, 'POST', `${path}/send`, draft.version, { sentOn: offer.date });
      assert.equal(sent.status, 200, JSON.stringify(sent.data));
      const preview = await requestDuring(manager, 'GET', `${path}/rebase-preview`, () => button(manager, 'Обновить по смете…').click());
      assert.equal((await (await checkedResponse(preview, 200)).json()).version, sent.data.version);
      const dialog = manager.getByRole('dialog', { name: 'Обновить КП по смете' });
      await dialog.getByText(/КП тем временем изменилось/).waitFor();
      assert(await button(dialog, 'Обновить черновик').isDisabled());
      await button(dialog, 'Перечитать КП и разницу').click();
      await dialog.getByText(/Отправленная версия останется в истории/).waitFor();
      const before = await callApiOk(manager, 'GET', path);
      assert.equal(before.status, 'sent');
      assert.equal(before.currentVersion, draft.currentVersion);
      const rebased = await requestDuring(manager, 'POST', `${path}/rebase`, () =>
        button(dialog, 'Создать черновик по новой редакции').click());
      const reply = await checkedResponse(rebased, 200);
      assert.equal(await reply.request().headerValue('if-match'), sent.data.version);
      const { offer: updated } = await reply.json();
      assert.equal(updated.status, 'draft');
      assert.equal(updated.currentVersion, draft.currentVersion + 1);
    });

    await check('derivative-links-stay-on-the-shown-version-and-return-to-the-same-revision', async () => {
      const draft = await callApiOk(manager, 'POST', '/plan/offers', { revisionId: source.id, date: offer.date, markup: 8 });
      const path = `/plan/offers/${draft.id}`;
      const sent = await concurrentWrite(manager, 'POST', `${path}/send`, draft.version, { sentOn: offer.date });
      assert.equal(sent.status, 200, JSON.stringify(sent.data));
      await manager.goto(`${BASE}/estimates/${estimateId}`);
      const sourceTab = manager.getByRole('tab', { name: /^Ред\. 1(?:\s|$)/ });
      await sourceTab.click();
      const derivatives = manager.getByRole('region', { name: 'КП редакции' });
      const sentLink = derivatives.getByRole('link', { name: `Открыть ${draft.number}, версия 1`, exact: true });
      assert((await sentLink.getAttribute('href')).endsWith('?version=1'));
      const newer = await concurrentWrite(manager, 'POST', `${path}/versions`, sent.data.version);
      assert.equal(newer.status, 200, JSON.stringify(newer.data));
      await sentLink.click();
      await manager.getByRole('heading', { name: 'Версия 1 · только чтение', exact: true }).waitFor();
      await manager.getByRole('link', { name: 'К смете', exact: true }).click();

      // Реестр производных кеширует ответ, полученный до операции из другого окна.
      await manager.waitForURL(`**/estimates/${estimateId}`);
      await manager.reload();
      await sourceTab.click();
      const draftLink = derivatives.getByRole('link', { name: `Открыть ${draft.number}, версия 2`, exact: true });
      assert((await draftLink.getAttribute('href')).endsWith('?version=2'));
      await draftLink.click();
      await manager.getByRole('columnheader', { name: 'Цена продажи', exact: true }).waitFor();
      await tab(manager, 'Наценки').click();
      assert.equal(new URL(manager.url()).searchParams.get('version'), '2');
      await manager.getByRole('textbox', { name: 'Наценка КП', exact: true }).fill('19');
      await manager.getByRole('link', { name: 'К смете', exact: true }).click();
      await button(manager.getByRole('dialog', { name: 'Несохранённые изменения' }), 'Не сохранять').click();
      await draftLink.waitFor();
      assert.equal(await sourceTab.getAttribute('aria-selected'), 'true');
      assert.equal((await callApiOk(manager, 'GET', path)).markup, 8);
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
    const derivatives = estimator.getByRole('region', { name: 'КП редакции' });
    await derivatives.getByText('plan.offer.read', { exact: true }).waitFor();
    assert(!/\d/.test(await derivatives.innerText()), 'В скрытом блоке КП видны числа.');
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

  const admin = await browser.newPage({ viewport });
  await login(admin, EMAIL, PASSWORD);
  const fixtures = await preparePriceChecks(admin, password);
  await admin.close();
  const editor = await browser.newPage({ viewport });
  editor.on('pageerror', error => errors.push(error.message));
  await login(editor, PRICE_EDITOR_EMAIL, password);
  await checkPriceList({ check, manager, editor, fixtures, constructionId: estimate.constructionId, concurrentWrite });

  await check('no-browser-errors', () => assert.deepEqual(errors, []));
  process.exitCode = summarize('Коммерческие предложения');
} finally {
  await browser.close();
}
