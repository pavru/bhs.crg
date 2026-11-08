// Бросок файла на всё окно и повтор до выгрузки (costs-invoice-screen-14, [COST-6.7], [COST-31]) — живым браузером.
//
// Едет внутри `invoices-smoke`, под его входом и с его помощниками (`h`), как `invoice-scan-checks`. Бросок —
// настоящим событием `drop` с `DataTransfer`: путь тот же, что у мыши, — захват окна в `shared/ui/fileDrop`.
//
// Прогон по HTTP (задача 1.10): тот же набор с `SMOKE_BASE=http://<IP машины>:<порт клиента>` — не localhost,
// где `crypto.subtle` есть. Там отпечаток считает свой SHA-256 браузера; проверка «файл уже приложен» печатает
// контекст (`isSecureContext`), отпечаток из вопроса и счёт — чтобы сверить его с `costs.invoices.scan_fingerprint`:
//   SMOKE_BASE=http://192.168.1.34:5187 node e2e/invoices-smoke.mjs
// Файлы нарочно нечитаемы (`%PDF-1.4 …`): исход распознавания `BadFile` без запроса к движку (#1344).

import { createHash } from 'node:crypto';
import { invoiceMenu } from './harness.mjs';

const WIDTHS = [{ width: 1366, height: 768 }, { width: 1280, height: 720 }];
const WIDE = { width: 1500, height: 1000 };
const COVER_ONE = 'Отпустите — заведём черновик из файла';

const bytesOf = text => Buffer.from(`%PDF-1.4 не настоящий ${text}`);
const sha256 = buffer => createHash('sha256').update(buffer).digest('hex');

/** Набор файлов в странице — `DataTransfer` броска. */
const transferOf = (page, files) => page.evaluateHandle(files => {
  const data = new DataTransfer();
  for (const f of files) data.items.add(new File([new Uint8Array(f.bytes)], f.name, { type: 'application/pdf' }));
  return data;
}, files.map(f => ({ name: f.name, bytes: [...f.buffer] })));

/** Тащить над элементом и бросить. До броска — покрытие: оно обязано сказать, что будет. */
async function dropOn(page, target, files, h, coverText = files.length > 1 ? /Отпустите — заведём \d+ черновик/ : COVER_ONE) {
  const data = await transferOf(page, files);
  await target.dispatchEvent('dragenter', { dataTransfer: data });
  await target.dispatchEvent('dragover', { dataTransfer: data });
  if (coverText)
    await h.named('покрытие броска не показалось', () => page.getByTestId('invoice-drop-cover')
      .filter({ hasText: coverText }).waitFor({ timeout: 5_000 }));
  await target.dispatchEvent('drop', { dataTransfer: data });
}

/** Место элемента на экране; `null` — его нет (или он нулевой) и за шесть секунд не появился. */
async function boxOf(page, target) {
  for (let i = 0; i < 24; i++) {
    const box = await target.boundingBox({ timeout: 500 }).catch(() => null);
    if (box && box.width > 0 && box.height > 0) return box;
    await page.waitForTimeout(250);
  }
  return null;
}

const openedId = page => new URL(page.url()).searchParams.get('invoice');

/** Сколько запросов «счёт из файла» ушло за время `act`. */
async function countFromScan(page, act) {
  let sent = 0;
  const listen = r => { if (r.method() === 'POST' && /\/api\/costs\/invoices\/from-scan$/.test(r.url())) sent++; };
  page.on('request', listen);
  try { await act(); } finally { page.off('request', listen); }
  return () => sent;
}

/** Счёт из файла запросом — мимо вопроса: так повтор находит только оговорка после заведения. */
export const fromScanApi = (page, name, buffer) => page.evaluate(async ([name, bytes]) => {
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(bytes)], { type: 'application/pdf' }), name);
  const auth = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
  const res = await fetch('/api/costs/invoices/from-scan', { method: 'POST', headers: { Authorization: `Bearer ${auth}` }, body: form });
  if (!res.ok) throw new Error(`счёт из файла не заведён: ${res.status} ${await res.text()}`);
  return (await res.json()).invoice;
}, [name, [...buffer]]);

/**
 * Счёт из файла ДРУГИМ человеком — «Семён Снабженцев» посева (`seed-invoices.mjs`). Вход — из прогона, а не из
 * страницы: вход страницы сменил бы её учётную запись (обновляющий токен — кука), и следующие проверки шли бы
 * от чужого имени.
 */
async function fromScanAs(base, email, password, name, buffer) {
  const login = await fetch(`${base}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
  });
  if (!login.ok) throw new Error(`${email} не вошёл: ${login.status} — двойника другого человека не завести`);
  const { accessToken } = await login.json();
  const form = new FormData();
  form.append('file', new Blob([buffer], { type: 'application/pdf' }), name);
  const res = await fetch(`${base}/api/costs/invoices/from-scan`, { method: 'POST', headers: { Authorization: `Bearer ${accessToken}` }, body: form });
  if (!res.ok) throw new Error(`счёт из файла от ${email} не заведён: ${res.status} ${await res.text()}`);
  return (await res.json()).invoice;
}

export async function invoiceFileDropChecks(page, check, h) {
  const repeatDialog = () => page.getByRole('dialog', { name: /^Этот файл уже приложен к/ });

  // Цель — всё окно (D1): у каждого броска черновик или видимая причина, бросков без исхода нет. Точек шесть, на
  // каждой ширине — те, что на ней есть: на 1280 списка нет вовсе (D2 зонтика), это и есть случай, ради которого
  // цель стала окном. Первый бросок — на открытый счёт: его файл, версия и строки обязаны остаться прежними.
  await check('бросок в 6 точек окна на 1366 и 1280: у каждого черновик, открытый счёт не меняется', async () => {
    const points = [
      ['панель файла', () => page.getByRole('complementary', { name: 'Файл счёта' })],
      ['таблица строк', () => page.getByRole('table', { name: 'Строки счёта' })],
      ['сводка', () => page.getByTestId('invoice-header')],
      ['шапка', () => page.locator('main > header').first()],
      ['рейл', () => page.getByTestId('rail')],
      ['список', () => page.getByTestId('ld-nav')],
    ];
    const missed = [];
    const absent = [];
    let n = 0;
    for (const size of WIDTHS) {
      await page.setViewportSize(size);
      const first = await fromScanApi(page, `Окно-${h.stamp}-${size.width}.pdf`, bytesOf(`окно ${h.stamp} ${size.width}`));
      await page.goto(`${h.base}/invoices?invoice=${first.id}`, { waitUntil: 'networkidle' });
      await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
      const before = await h.api('GET', `/costs/invoices/${first.id}`);
      for (const [name, at] of points) {
        const target = at();
        // Части формы дорисовываются после сводки, а форма нового черновика перемонтируется: место ищем с
        // повтором, а не по первому кадру.
        const box = await boxOf(page, target);
        if (!box) { absent.push(`${name} @${size.width}`); continue; }
        const was = openedId(page);
        const file = `Бросок-${h.stamp}-${++n}.pdf`;
        await dropOn(page, target, [{ name: file, buffer: bytesOf(`${h.stamp} ${n}`) }], h);
        const moved = await page.waitForFunction(was => new URL(location.href).searchParams.get('invoice') !== was, was, { timeout: 15_000 })
          .then(() => true, () => false);
        if (!moved) missed.push(`${name} @${size.width}`);
        else await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
      }
      const after = await h.api('GET', `/costs/invoices/${first.id}`);
      if (after.version !== before.version || JSON.stringify(after.requisites['Скан']) !== JSON.stringify(before.requisites['Скан'])
        || JSON.stringify(after.lines) !== JSON.stringify(before.lines))
        throw new Error(`бросок изменил открытый счёт @${size.width}: версия ${before.version} → ${after.version}`);
    }
    await page.setViewportSize(WIDE);
    if (missed.length) throw new Error(`бросок без исхода: ${missed.join(', ')}`);
    // Нет на ширине — это не «бросок без исхода»: туда не дотянуться ни мышью, ни прогоном. Но на 1366 нет
    // только списка (файл рядом), а на 1280 — списка и панели файла; другое значит, что раскладка сменилась.
    const expected = ['список @1366', 'панель файла @1280', 'список @1280'];
    if (absent.some(a => !expected.includes(a))) throw new Error(`точек нет там, где ждали: ${absent.join(', ')}`);
  });

  // Повтор до выгрузки (D3, D4): черновика нет до решения; Enter и Esc оставляют на месте; «Завести ещё один» —
  // ровно один черновик, сознательный повтор. Здесь же — прогон по HTTP: отпечаток браузера равен серверному
  // (иначе окна не было бы), а сам он равен SHA-256 байтов.
  await check('файл уже приложен: окно до заведения, Enter и Esc — «Не заводить», «Завести ещё один» — один черновик', async () => {
    const buffer = bytesOf(`повтор ${h.stamp}`);
    const twin = await fromScanApi(page, `Прежний-${h.stamp}.pdf`, buffer);
    await page.goto(`${h.base}/invoices?invoice=${twin.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
    const files = [{ name: `Повтор-${h.stamp}.pdf`, buffer }];
    let asked = null;
    const sniff = r => { const m = /same-file\?fingerprint=([0-9a-f]{64})/.exec(r.url()); if (m) asked = m[1]; };
    page.on('request', sniff);
    try {
      for (const key of ['Enter', 'Escape']) {
        const sent = await countFromScan(page, async () => {
          await dropOn(page, page.getByTestId('invoice-header'), files, h);
          await h.named('окно повтора не появилось', () => repeatDialog().waitFor({ timeout: 15_000 }));
          await page.keyboard.press(key);
          await h.named(`${key} не закрыл окно`, () => repeatDialog().waitFor({ state: 'detached', timeout: 5_000 }));
        });
        if (sent() !== 0) throw new Error(`${key} завёл черновик`);
        if (openedId(page) !== twin.id) throw new Error(`${key} увёл со счёта`);
      }
    } finally { page.off('request', sniff); }
    const secure = await page.evaluate(() => isSecureContext);
    console.log(`  · отпечаток браузера ${asked} (isSecureContext=${secure}), счёт-двойник ${twin.id}`);
    if (asked !== sha256(buffer)) throw new Error(`отпечаток браузера ${asked} ≠ SHA-256 байтов ${sha256(buffer)}`);

    const created = page.waitForResponse(r => /\/from-scan$/.test(r.url()) && r.request().method() === 'POST', { timeout: 15_000 });
    const sent = await countFromScan(page, async () => {
      await dropOn(page, page.getByTestId('invoice-header'), files, h);
      await repeatDialog().getByRole('button', { name: 'Завести ещё один черновик' }).click();
      await created;
      await page.waitForFunction(was => new URL(location.href).searchParams.get('invoice') !== was, twin.id, { timeout: 15_000 });
    });
    if (sent() !== 1) throw new Error(`«Завести ещё один» отправил ${sent()} запросов`);
    const made = await h.api('GET', `/costs/invoices/${openedId(page)}`);
    if (made.scanRepeatAccepted !== true) throw new Error('черновик заведён не как сознательный повтор (scanRepeatAccepted)');
  });

  // Пакет без окна (D5): повтор придержан, «выбран дважды» назван, экран не двигается. Двойник — счёт ДРУГОГО
  // человека («Семён Снабженцев» посева): вопрос смотрит все счета, а не свои.
  await check('пакет из 5: окна нет, итог называет придержанный и «выбран дважды», экран на месте', async () => {
    const theirs = bytesOf(`чужой ${h.stamp}`);
    const other = await fromScanAs(h.base, process.env.SMOKE_SUPPLIER_EMAIL || 'snab@bhs.local',
      process.env.SMOKE_USER_PASSWORD || 'Demo12345!', `Чужой-${h.stamp}.pdf`, theirs);
    const opened = openedId(page);
    const same = bytesOf(`дважды ${h.stamp}`);
    const files = [
      { name: `Пакет5-${h.stamp}-1.pdf`, buffer: bytesOf(`п1 ${h.stamp}`) },
      { name: `Пакет5-${h.stamp}-2.pdf`, buffer: bytesOf(`п2 ${h.stamp}`) },
      { name: `Пакет5-${h.stamp}-3.pdf`, buffer: same },
      { name: `Пакет5-${h.stamp}-4 копия.pdf`, buffer: same },
      { name: `Пакет5-${h.stamp}-5.pdf`, buffer: theirs },
    ];
    await dropOn(page, page.getByTestId('rail'), files, h);
    const tally = page.getByTestId('upload-tally').filter({ hasText: 'Заведено 3 · придержано 1 (тот же файл) · 1 выбран дважды' });
    await h.named('итог пакета не назвал придержанный и выбранный дважды', () => tally.waitFor({ timeout: 30_000 }));
    if (await repeatDialog().count()) throw new Error('пакет открыл окно повтора');
    const held = await page.getByTestId('upload-held').innerText();
    if (!held.includes(files[4].name) || !held.includes('уже у'))
      throw new Error(`придержанный назван не так: «${held}»`);
    await h.named('«выбран дважды» не назван', () => page.getByText(`${files[3].name} выбран дважды — заведён один черновик`)
      .or(page.getByText(`${files[2].name} выбран дважды — заведён один черновик`)).first().waitFor({ timeout: 5_000 }));
    if (openedId(page) !== opened) throw new Error('пакет сменил открытый счёт');
    console.log(`  · двойник другого человека: ${other.id}`);
    // Итог сам не исчезает — закрываем: над формой он отнимает высоту, а проверки раскладки дальше меряют форму.
    await page.getByRole('status').filter({ has: page.getByTestId('upload-tally') }).getByRole('button', { name: 'Закрыть' }).click();
  });

  // Проверка не ответила (D3): заводим и говорим «повтор не проверен»; пакет не останавливается.
  await check('вопрос отвечает 503: черновики заведены, «повтор не проверен»', async () => {
    await page.route('**/api/costs/invoices/same-file?**', route => route.fulfill({ status: 503, body: '{"error":"недоступно"}' }));
    try {
      const was = openedId(page);
      await dropOn(page, page.getByTestId('invoice-header'), [{ name: `Без-проверки-${h.stamp}.pdf`, buffer: bytesOf(`503 ${h.stamp}`) }], h);
      await h.named('черновик не заведён, когда проверка не ответила', () => page.waitForFunction(
        was => new URL(location.href).searchParams.get('invoice') !== was, was, { timeout: 15_000 }));
      await h.named('«повтор не проверен» не сказано', () => page.getByText('Повтор не проверен — если файл уже был, счёт скажет об этом.')
        .first().waitFor({ timeout: 10_000 }));
    } finally { await page.unroute('**/api/costs/invoices/same-file?**'); }
  });

  // Пустой ответ — «двойников среди проверяемых нет», а не «файл новый» (счёт до #1295 и недоверенная печать в
  // вопросе не участвуют). Сервер подменён пустым ответом про файл, у которого двойник есть: окна нет, а слов
  // «файл новый» нет нигде.
  await check('счёт до отпечатка: окна нет, слов «файл новый» нет нигде', async () => {
    const buffer = bytesOf(`старый ${h.stamp}`);
    await fromScanApi(page, `Старый-${h.stamp}.pdf`, buffer);
    await page.route('**/api/costs/invoices/same-file?**', route => {
      const prints = new URL(route.request().url()).searchParams.getAll('fingerprint');
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(prints.map(fingerprint => ({ fingerprint, twins: [] }))) });
    });
    try {
      const was = openedId(page);
      await dropOn(page, page.getByTestId('invoice-header'), [{ name: `Старый-повтор-${h.stamp}.pdf`, buffer }], h);
      await h.named('черновик не заведён', () => page.waitForFunction(
        was => new URL(location.href).searchParams.get('invoice') !== was, was, { timeout: 15_000 }));
      if (await repeatDialog().count()) throw new Error('пустой ответ открыл окно повтора');
      if (/файл новый/i.test(await page.locator('body').innerText())) throw new Error('экран сказал «файл новый»');
    } finally { await page.unroute('**/api/costs/invoices/same-file?**'); }
  });

  // Файл к счёту — только явным действием (D6): окно с площадкой, у замены — вопрос. Без согласия файл и версия
  // прежние; после «Заменить файл» — новый файл.
  await check('«⋮ → Заменить файл…»: без согласия файл и версия прежние, после согласия — новый файл', async () => {
    const created = await fromScanApi(page, `Заменяемый-${h.stamp}.pdf`, bytesOf(`заменяемый ${h.stamp}`));
    await page.goto(`${h.base}/invoices?invoice=${created.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
    const before = await h.api('GET', `/costs/invoices/${created.id}`);
    const dialog = page.getByRole('dialog', { name: /^Заменить файл счёта/ });
    const next = { name: `Замена-${h.stamp}.pdf`, mimeType: 'application/pdf', buffer: bytesOf(`замена ${h.stamp}`) };

    await invoiceMenu(page, 'Заменить файл…');
    await dialog.getByLabel('Файл счёта').setInputFiles(next);
    await dialog.getByRole('button', { name: 'Заменить', exact: true }).click();
    await h.named('вопрос о прежнем файле не задан', () => page.getByText('Прежний файл будет удалён без возврата').waitFor({ timeout: 5_000 }));
    await page.getByRole('dialog').getByRole('button', { name: 'Отмена' }).click(); // с шага — назад к окну
    await page.getByRole('dialog').getByRole('button', { name: 'Отмена' }).click(); // окно — закрыть
    await h.named('«Отмена» не закрыла окно замены', () => dialog.waitFor({ state: 'detached', timeout: 5_000 }));
    const kept = await h.api('GET', `/costs/invoices/${created.id}`);
    if (kept.version !== before.version || kept.requisites['Скан']?.fileName !== before.requisites['Скан']?.fileName)
      throw new Error('без согласия файл или версия счёта изменились');

    await invoiceMenu(page, 'Заменить файл…');
    await dialog.getByLabel('Файл счёта').setInputFiles(next);
    await dialog.getByRole('button', { name: 'Заменить', exact: true }).click();
    const answered = page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${created.id}/scan`) && r.request().method() === 'POST', { timeout: 15_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Заменить файл' }).click();
    if (!(await answered).ok()) throw new Error('замена отказала');
    const after = await h.api('GET', `/costs/invoices/${created.id}`);
    if (after.requisites['Скан']?.fileName !== next.name) throw new Error(`файл не заменён: ${after.requisites['Скан']?.fileName}`);
  });

  await check('«Приложить файл…» у счёта без файла: окно с площадкой прикладывает к нему', async () => {
    const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(`СЧ-П${h.stamp}`, 'Без файла') });
    await page.goto(`${h.base}/invoices?invoice=${created.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
    await invoiceMenu(page, 'Приложить файл…');
    const dialog = page.getByRole('dialog', { name: /^Приложить файл к счёту/ });
    // Бросок на площадку — тем же событием, что мышью.
    const data = await transferOf(page, [{ name: `Приложенный-${h.stamp}.pdf`, buffer: bytesOf(`приложенный ${h.stamp}`) }]);
    await dialog.getByText('Перетащите файл сюда').dispatchEvent('drop', { dataTransfer: data });
    await dialog.getByRole('button', { name: 'Приложить' }).click();
    await h.named('окно не закрылось после приложения', () => dialog.waitFor({ state: 'detached', timeout: 15_000 }));
    const after = await h.api('GET', `/costs/invoices/${created.id}`);
    if (after.requisites['Скан']?.fileName !== `Приложенный-${h.stamp}.pdf`) throw new Error('файл не приложен к ручному счёту');
    if (openedId(page) !== created.id) throw new Error('приложение увело с счёта');
  });

  // Пакет там, где списка нет (D5): ход и итог — в центре активности, экран не сменился.
  await check('пакет на 1280: ход и итог в центре активности, экран не сменился', async () => {
    await page.setViewportSize({ width: 1280, height: 720 });
    try {
      const opened = openedId(page);
      await dropOn(page, page.getByTestId('invoice-header'), [
        { name: `Центр-${h.stamp}-1.pdf`, buffer: bytesOf(`центр 1 ${h.stamp}`) },
        { name: `Центр-${h.stamp}-2.pdf`, buffer: bytesOf(`центр 2 ${h.stamp}`) },
      ], h);
      if (await page.getByTestId('upload-tally').count() + await page.getByRole('navigation', { name: 'Счета на оплату' }).getByRole('status').count())
        throw new Error('на 1280 полоса пакета встала на экран');
      await page.getByTestId('activity').click();
      const section = page.getByTestId('activity-scan-batch');
      await h.named('итог пакета не виден в центре активности', () => section.getByText(/Заведено 2 черновика/).waitFor({ timeout: 30_000 }));
      if (openedId(page) !== opened) throw new Error('пакет сменил открытый счёт');
      await section.getByRole('button', { name: 'Закрыть' }).click();
      await page.keyboard.press('Escape');
    } finally { await page.setViewportSize(WIDE); }
  });
}
