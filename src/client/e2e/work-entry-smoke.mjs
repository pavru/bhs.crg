import assert from 'node:assert/strict';
import { BASE, callApiOk, createChecks, launchBrowser, login, underRoute } from './harness.mjs';
import { ASSIGNMENT_PM, ENTRY_EMPLOYEE, INSTALLER } from './seed-assignments.mjs';
import { REVIEW_READER_EMAIL } from './seed-review.mjs';
import { seedWorkEntryCatalog, WORK_ENTRY_MATERIAL, WORK_ENTRY_TYPE, WORK_ENTRY_VARIANT } from './seed-work-entry.mjs';

const password = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const browser = await launchBrowser();
const { check, summarize } = createChecks();
const heading = (page, name) => page.getByRole('heading', { name, exact: typeof name === 'string' });
const button = (page, name) => page.getByRole('button', { name, exact: true });
const shot = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

async function pickFromCatalog(page, name) {
  const dialog = page.getByRole('dialog');
  const search = dialog.getByRole('combobox', { name: 'Поиск по справочнику' });
  await search.fill(name);
  const option = dialog.getByRole('option', { name, exact: true });
  await option.waitFor();
  await search.press('ArrowDown');
  assert.equal(await search.getAttribute('aria-activedescendant'), await option.getAttribute('id'));
  await search.press('Enter');
}

async function createOrOpen(page, path) {
  const response = page.waitForResponse(reply => new URL(reply.url()).pathname === `/api${path}`
    && reply.request().method() === 'POST');
  await button(page, 'Создать черновик').click();
  const status = (await response).status();
  if (status === 409) {
    await page.getByText('На эту смену отчёт уже есть.').waitFor();
    await button(page, 'Открыть').click();
  } else {
    assert.equal(status, 201);
  }
}

async function submitWhilePhotosUpload(page, path) {
  const urls = new Set();
  const upload = /\/api\/work\/reports\/[^/]+\/lines\/[^/]+\/photos\/[^/]+$/;
  const names = ['Смена-1.png', 'Смена-2.png'];
  const release = Promise.withResolvers();
  await underRoute(page, upload, async route => {
    if (route.request().method() !== 'PUT') { await route.continue(); return; }
    urls.add(route.request().url());
    await release.promise;
    await route.continue();
  }, async () => {
    try {
      await page.getByText(/До \d+ фото на строку/).waitFor();
      await page.getByLabel('Файлы фото строки').setInputFiles(names.map(name => ({ name, mimeType: 'image/png', buffer: shot })));
      await button(page, 'Сохранить').click();
      await page.getByText('Загружается', { exact: true }).last().waitFor();
      await button(page, 'Подать отчёт').click();
      await page.getByText('Подан', { exact: true }).waitFor();
      const pending = await callApiOk(page, 'GET', path);
      assert.equal(pending.photosPending, 2);
      assert.equal(pending.status, 'submitted');
      assert.equal(await page.getByText('Загружается', { exact: true }).count(), 2);

      release.resolve();
      const first = button(page, `Открыть фото: ${names[0]}`);
      const second = button(page, `Открыть фото: ${names[1]}`);
      await first.locator('img').evaluate(image => image.decode());
      await second.locator('img').evaluate(image => image.decode());
      assert.equal(urls.size, 2);
      const stored = await callApiOk(page, 'GET', path);
      assert.equal(stored.photosPending, 0);
      assert.equal(stored.version, pending.version);
      await first.click();
      const gallery = page.getByRole('dialog', { name: 'Фото строки' });
      await gallery.getByRole('img', { name: /^Фото / }).waitFor();
      const position = gallery.getByRole('status').filter({ hasText: /^[12] из 2$/ });
      const before = await position.textContent();
      await page.keyboard.press(before === '1 из 2' ? 'ArrowRight' : 'ArrowLeft');
      assert.notEqual(await position.textContent(), before);
      await page.keyboard.press('Escape');
      await gallery.waitFor({ state: 'hidden' });
    } finally {
      release.resolve();
    }
  });
}

async function fillAndSubmit(page, report, fixture) {
  const path = `/work/reports/${report.id}`;
  const materials = await callApiOk(page, 'GET', `/work/entry/sites/${report.constructionId}/catalog/materials`
    + `?reportId=${report.id}&q=${encodeURIComponent(WORK_ENTRY_MATERIAL)}`);
  const material = materials.items.find(item => item.displayName === WORK_ENTRY_MATERIAL);
  assert.ok(material, 'Позиция посева должна быть видна монтажнику через справочник Work.');
  if (report.status === 'draft' || report.status === 'returned') {
    const index = report.lines.length + 1;
    await button(page, 'Добавить строку').click();
    await button(page, `Вид работы, строка ${index}`).click();
    await pickFromCatalog(page, WORK_ENTRY_TYPE);
    await page.getByRole('textbox', { name: `Объём, строка ${index}` }).fill('12');
    await page.getByRole('combobox', { name: 'Вариант' }).click();
    await page.getByRole('option', { name: WORK_ENTRY_VARIANT, exact: true }).click();
    await page.getByRole('textbox', { name: 'Часы за смену' }).fill('8');
    await button(page, 'Сохранить').click();
    await button(page, 'Подать отчёт').click();
    await page.getByText('Добавьте материал для подачи отчёта.', { exact: true }).waitFor();
    assert.ok(await button(page, 'Добавить материал').evaluate(element => element === document.activeElement));
    await button(page, 'Добавить материал').click();
    await pickFromCatalog(page, WORK_ENTRY_MATERIAL);
    await submitWhilePhotosUpload(page, path);
  }
  const submitted = await callApiOk(page, 'GET', path);
  assert.equal(submitted.status, 'submitted');
  assert.equal(submitted.hours, 8);
  const line = submitted.lines.find(item => item.workTypeId === fixture.typeId);
  assert.equal(line?.variantId, fixture.variantId);
  assert.equal(line.unitId, fixture.unitId);
  assert.equal(line.volume, 12);
  assert.deepEqual(line.materials.map(item => item.nomenclatureId), [material.id]);
  assert.equal(line.photos.filter(photo => photo.state === 'stored').length, 2);
}

try {
  let fixture;
  await check('seed-work-entry-catalog', async () => {
    const page = await browser.newPage();
    try {
      await login(page);
      fixture = await seedWorkEntryCatalog(page);
    } finally { await page.close(); }
  });

  await check('installer-creates-opens-and-submits-report-with-lines', async () => {
    assert.ok(fixture, 'Справочники для веб-ввода должны быть подготовлены.');
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    try {
      await login(page, INSTALLER.email, password);
      await heading(page, 'Мои отчёты').waitFor();
      const sites = await callApiOk(page, 'GET', '/work/sites/mine');
      const site = sites.items[0];
      assert.ok(site?.suggested.label);
      await button(page, site.suggested.label).click();
      await heading(page, 'Новый отчёт').waitFor();
      await page.getByText(sites.boundary.text, { exact: true }).waitFor();
      assert.equal(await page.getByRole('tab', { name: site.suggested.shift === 'night' ? 'Ночь' : 'День' })
        .getAttribute('aria-selected'), 'true');
      await createOrOpen(page, '/work/reports');
      await page.waitForURL(/\/my-reports\/[0-9a-f-]{36}$/);
      const opened = page.url();
      const saved = await callApiOk(page, 'GET', `/work/reports/${opened.split('/').at(-1)}`);
      assert.equal(saved.date, site.suggested.date);
      assert.equal(saved.shift, site.suggested.shift);
      await button(page, 'К отчётам').click();
      await heading(page, 'Мои отчёты').waitFor();
      if (saved.status === 'draft')
        await page.getByRole('row').filter({ hasText: site.name }).getByText(/Подастся .* по времени стройки/).first().waitFor();
      await page.keyboard.press('Control+k');
      const search = page.getByRole('textbox', { name: 'Поиск и действия' });
      await search.fill('Новый отчёт');
      await page.getByRole('option', { name: new RegExp(`^${RegExp.escape(site.suggested.label)}`) }).waitFor();
      await search.press('Enter');
      await button(page, 'Создать черновик').click();
      await page.getByText('На эту смену отчёт уже есть.').waitFor();
      await button(page, 'Открыть').click();
      await page.waitForURL(opened);
      await fillAndSubmit(page, saved, fixture);
    } finally { await page.close(); }
  });

  await check('manager-creates-for-assigned-employee', async () => {
    const page = await browser.newPage();
    try {
      await login(page, ASSIGNMENT_PM.email, password);
      await page.goto(`${BASE}/work-review`);
      await button(page, 'Ввести за сотрудника').click();
      await page.getByRole('checkbox', { name: `Отметить: ${ENTRY_EMPLOYEE.name}` }).check();
      await button(page, 'Продолжить').click();
      await page.getByText(`Вводит ${ASSIGNMENT_PM.name} за сотрудника: ${ENTRY_EMPLOYEE.name}`).waitFor();
      await page.getByText('стройки сотрудника в вашем охвате (1)').waitFor();
      await createOrOpen(page, '/work/entry/reports');
      await page.waitForURL(/\/work-review\/entry\/[0-9a-f-]{36}$/);
      const id = page.url().split('/').at(-1);
      const report = await callApiOk(page, 'GET', `/work/entry/reports/${id}`);
      assert.equal(report.employeeName, ENTRY_EMPLOYEE.name);
      assert.equal(report.enteredBy.name, ASSIGNMENT_PM.name);
    } finally { await page.close(); }
  });

  await check('executive-has-review-without-report-entry', async () => {
    const page = await browser.newPage();
    try {
      await login(page, REVIEW_READER_EMAIL, password);
      await page.goto(`${BASE}/work-review`);
      await heading(page, 'Приёмка').waitFor();
      assert.equal(await page.getByRole('link', { name: 'Мои отчёты', exact: true }).count(), 0);
      assert.equal(await button(page, 'Ввести за сотрудника').count(), 0);
    } finally { await page.close(); }
  });
} finally {
  await browser.close();
}
process.exitCode = summarize('Веб-ввод отчётов');
