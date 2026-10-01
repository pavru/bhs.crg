// Живой прогон МАТРИЦЫ РАЗНОСКИ (задача F2 этапа 2, issue #1086, ТЗ COST-6.2, COST-11, COST-12).
//
// ЗАЧЕМ ОН ЕСТЬ. Четыре утверждения задачи — про экран целиком, и юнит-тестом их не проверить:
//   1. `preview-matches-applied` — числа, показанные до применения, посимвольно равны числам после, И
//      при нажатии «Поровну» ушёл запрос. ⚠️ Одного сравнения мало: посчитай раскладку клиент — оно
//      сравнивало бы клиент с клиентом и было бы зелено всегда. Запрос доказывает, что считал сервер.
//   2. `kopeck-goes-to-a-named-part` — 100 ₽ на три объекта: ровно 100,00, клетка с остатком помечена.
//   3. `remainder-is-written-and-stays-visible` — «не разнесено» пишет ноль, а не пустоту, и при восьми
//      объектах остаётся на виду после горизонтальной прокрутки (закреплённая колонка).
//   4. `matrix-under-read-only-right` — под «Бухгалтером» (чтение счетов без права разноски) матрица
//      открывается, но полей и кнопок «поровну» и «по %» НЕТ, и ни один запрос не получил отказа —
//      а не «кнопки есть и получают 403».
//
// ⚠️ ДАННЫЕ ПРОГОН ГОТОВИТ СЕБЕ САМ: свои счета (с приметой в номере) и восемь своих объектов
// (заводятся один раз и переиспользуются). Посеянный счёт первый же прогон разнёс бы, и второй
// проверял бы разнесённое.
//
// ⚠️ Элементы ищутся по видимому тексту, ролям и подписям — без служебных атрибутов.
//
// Требует поднятых фронта и бэка, посева (бухгалтер buh@bhs.local — e2e/seed.mjs) и ВКЛЮЧЁННОГО
// модуля `costs` (`Modules__Enabled=id,costs`) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/allocation-matrix-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const ACCOUNTANT_EMAIL = process.env.SMOKE_ACCOUNTANT_EMAIL || 'buh@bhs.local';
const ACCOUNTANT_PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);

try {
await login(page);
await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });

if ((await page.getByRole('heading', { name: 'Счета на оплату' }).count()) === 0) {
  console.error('Экрана счетов нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs)? '
    + 'Без него проверять нечего, и зелёный прогон был бы отчётом о работе, которой не было.');
  process.exit(1);
}

/** Запрос к приложению ТЕМ ЖЕ токеном, что у открытой страницы. */
async function api(method, path, body) {
  return page.evaluate(async ([method, path, body]) => {
    const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
    const res = await fetch(`/api${path}`, {
      method,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: body === null ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text.slice(0, 200)}`);
    return text ? JSON.parse(text) : null;
  }, [method, path, body ?? null]);
}

// Восемь объектов прогона — по имени, заводятся однажды: стройки прогон не удаляет, и новые на каждом
// запуске копились бы в списке выбора у всех.
const known = await api('GET', '/constructions');
const sites = [];
for (let index = 1; index <= 8; index++) {
  const name = `Объект прогона матрицы ${index}`;
  sites.push(known.find(c => c.name === name) ?? await api('POST', '/constructions', { name }));
}

const organizations = await api('GET', '/costs/organizations');
if (organizations.length === 0) {
  console.error('В справочнике нет ни одной организации — посев не отработал. Проверять нечего.');
  process.exit(1);
}

/** Счёт со строками: доставка суммой и кабель метрами. Сумма к оплате — ровно сумма строк. */
async function invoice(number, lines, total) {
  const created = await api('POST', '/costs/invoices', {
    requisites: {
      'Номер': number, 'Дата': '2026-10-01', 'Итого': total,
      'Поставщик': { $ref: 'catalog', entryId: organizations[0].id },
    },
  });
  await api('PUT', `/costs/invoices/${created.id}/lines`, { lines });
  return created.id;
}

/** Открыть счёт по номеру и матрицу его разноски. */
async function openMatrix(target, number, button) {
  await target.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
  await target.getByRole('button', { name: new RegExp(number) }).first().click();
  await target.getByRole('button', { name: button }).click();
  const dialog = target.getByRole('dialog', { name: 'Разноска по объектам' });
  await dialog.waitFor({ timeout: 10_000 });
  return dialog;
}

/** Текст клеток матрицы построчно — то, что видит человек (значения полей ввода в него не входят). */
const cells = dialog => dialog.locator('tbody tr').evaluateAll(rows =>
  rows.map(row => [...row.querySelectorAll('td')].map(td => td.innerText.trim())));

const money = text => Number(text.replace(/[^\d,-]/g, '').replace(',', '.'));

// ── 1–2. Поровну на три объекта: предпросмотр = записанному, копейка у названной клетки ───────────
const first = `МТР-${stamp}`;
const firstId = await invoice(first, [
  { supplierText: 'Доставка', amount: 100 },
  { supplierText: 'Кабель прогона', unit: 'м', quantity: 10, price: 1 },
], 110);

const dialog = await openMatrix(page, first, 'на несколько объектов');
await dialog.getByRole('button', { name: 'Добавить объект' }).click();
await dialog.getByRole('button', { name: 'Добавить объект' }).click();
for (let index = 0; index < 3; index++)
  await dialog.getByLabel(`Объект ${index + 1}`, { exact: true }).selectOption({ label: sites[index].name });

let asked = false;
page.on('request', request => { if (request.url().includes('/allocation/preview')) asked = true; });
await Promise.all([
  page.waitForResponse(r => r.url().includes('/allocation/preview') && r.ok()),
  dialog.getByRole('button', { name: 'Поровну' }).click(),
]);
await dialog.getByRole('button', { name: 'Применить' }).waitFor();

const shown = await cells(dialog);
const marked = await dialog.getByTitle(/остаток округления/).count();

await check('kopeck-goes-to-a-named-part', async () => {
  const delivery = shown[0];
  const parts = delivery.slice(0, 3).map(money);
  const sum = Math.round(parts.reduce((a, b) => a + b, 0) * 100) / 100;
  if (sum !== 100) throw new Error(`части доставки дают ${sum}, а не 100,00: ${delivery.join(' | ')}`);
  if (!delivery[2].includes('◆')) throw new Error(`клетка с остатком не помечена: ${delivery.join(' | ')}`);
  if (marked === 0) throw new Error('пометки «остаток округления» в матрице нет');
});

await Promise.all([
  page.waitForResponse(r => r.url().endsWith(`/costs/invoices/${firstId}/allocation`) && r.request().method() === 'PUT'),
  dialog.getByRole('button', { name: 'Применить' }).click(),
]);
await dialog.getByRole('button', { name: 'Сохранить разноску' }).waitFor();
const applied = await cells(dialog);

await check('preview-matches-applied', async () => {
  if (!asked) throw new Error('при нажатии «Поровну» запроса к серверу не было — раскладку посчитал клиент');
  // Пометка остатка — только у предпросмотра: после записи она уже не новость. Числа — посимвольно.
  const before = JSON.stringify(shown.map(r => r.map(c => c.replace('◆', '').trim())));
  const after = JSON.stringify(applied);
  if (before !== after) throw new Error(`до: ${before}\n      после: ${after}`);
});

// ── 3. Ноль пишется; при восьми объектах «не разнесено» видно после прокрутки ─────────────────────
await check('remainder-is-written-and-stays-visible', async () => {
  const rests = applied.map(row => row[row.length - 1]);
  if (rests.some(r => !/^0/.test(r))) throw new Error(`«не разнесено» при сошедшемся балансе: ${rests.join(' | ')}`);

  const wide = `МТР-8-${stamp}`;
  const wideId = await invoice(wide, [{ supplierText: 'Кабель прогона', unit: 'м', quantity: 80, price: 1 }], 80);
  const view = await api('GET', `/costs/invoices/${wideId}`);
  await api('PUT', `/costs/invoices/${wideId}/allocation`, {
    lines: [{ line: view.lines[0].id, parts: sites.map(s => ({ construction: s.id, quantity: 10 })) }],
    document: [],
    stamp: view.allocation.stamp,
  });

  await page.setViewportSize({ width: 1100, height: 900 });
  const matrix = await openMatrix(page, wide, 'на несколько объектов');

  const geometry = await matrix.locator('table').evaluate(table => {
    const scroller = table.parentElement;
    const header = [...table.querySelectorAll('th')].find(th => th.innerText.trim() === 'Не разнесено');
    const inside = () => {
      const box = scroller.getBoundingClientRect();
      const cell = header.getBoundingClientRect();
      return cell.left >= box.left - 1 && cell.right <= box.right + 1;
    };
    scroller.scrollLeft = 0;
    const atStart = inside();
    scroller.scrollLeft = scroller.scrollWidth;
    const atEnd = inside();
    return { scrolls: scroller.scrollWidth > scroller.clientWidth, atStart, atEnd };
  });

  if (!geometry.scrolls) throw new Error('восемь объектов уместились без прокрутки — проверка ничего не проверила бы');
  if (!geometry.atStart || !geometry.atEnd)
    throw new Error(`колонка «Не разнесено» уходит из виду при прокрутке: ${JSON.stringify(geometry)}`);

  const rest = (await cells(matrix))[0].at(-1);
  if (rest !== '0 м') throw new Error(`«не разнесено» у разнесённой на восемь объектов строки: «${rest}», ожидалось «0 м»`);
});

// ── 4. Бухгалтер: матрица только для чтения, и ни одного отказа ───────────────────────────────────
await check('matrix-under-read-only-right', async () => {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
  const reader = await context.newPage();
  const denials = [];
  reader.on('response', r => { if (r.status() === 403) denials.push(`${r.request().method()} ${r.url()}`); });

  try {
    await login(reader, ACCOUNTANT_EMAIL, ACCOUNTANT_PASSWORD);
    // Отказы считаются С ВХОДА, и стартовой странице дают догрузиться: она — список строек — запрашивала
    // у бухгалтера сводки сверок и планов без права на них (issue #1125). Не дождись прогон её запросов,
    // переход к счетам обрывал бы их, и проверка была бы зелёной через раз — так и было в CI.
    await reader.waitForLoadState('networkidle');
    const matrix = await openMatrix(reader, first, 'разноска по объектам');

    const text = (await cells(matrix)).flat().join(' ');
    if (!text.includes('₽')) throw new Error(`матрица пуста для чтения: ${text}`);

    for (const name of ['Поровну', 'По %', 'Добавить объект', 'Сохранить разноску'])
      if (await matrix.getByRole('button', { name }).count())
        throw new Error(`кнопка «${name}» видна без права разноски`);
    if (await matrix.locator('tbody input').count())
      throw new Error('клетки матрицы правятся без права разноски');
    if (denials.length)
      throw new Error(`запросов с отказом доступа: ${denials.length}\n      ${denials.join('\n      ')}`);
  } finally {
    await context.close();
  }
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Матрица разноски');
