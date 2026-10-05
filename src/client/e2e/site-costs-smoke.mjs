// Smoke отчёта «Затраты по стройке» (задача G5, issue #1098, ТЗ COST-20).
//
// ЗАЧЕМ ОН ЕСТЬ. Отчёт тонкий: своих списков оснований у него нет, каждое число ведёт в «Реестр счетов»
// с готовым отбором. Обещание — число отчёта РАВНО итогу «Суммы» в реестре. Сервер сверяет это своим
// тестом, но ссылку собирает клиент: неверное условие в ней (не та колонка, забытое «не отклонён»,
// объект без периода) оставило бы серверный тест зелёным, а человеку показало бы две разные цифры.
//
//   1. `site-report-shows-costs-and-payable` — экран стройки: затраты периода по контрагентам, итог и
//      отдельным блоком «к оплате»; общей суммы «затраты + к оплате» на экране нет.
//   2. `every-arrow-equals-the-registry-total` — по КАЖДОЙ стрелке экрана стройки: число отчёта равно
//      итогу колонки «Сумма» в реестре, открытом этой стрелкой.
//   2а. `every-section-arrow-equals-the-registry-total` — срез «по разделам» (G5b, issue #1198): та же
//      сумма, что по контрагентам; строки раздела и «без раздела» есть, и каждая стрелка сходится с реестром.
//   3. `all-sites-total-equals-the-registry-and-back-returns` — «Все стройки»: строка стройки прогона
//      есть, итог равен реестру, а «назад» возвращает отчёт с тем же периодом.
//
// ⚠️ Данные прогон заводит СЕБЕ САМ: стройку «Объект прогона затрат» (один раз, по имени), оплаченный и
// неоплаченный счёт на запуск. Счета копятся от запуска к запуску — числа растут, равенства остаются:
// проверяется не число из головы, а совпадение двух экранов.
//
// Требует поднятых фронта и бэка, посева и ВКЛЮЧЁННОГО модуля `costs` — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/site-costs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);

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

/** Число с экрана: «1 234,50 ₽», «Σ 1 234,5» → 1234.5. */
const number = text => Number(text.replace(/[^\d,.-]/g, '').replace(',', '.'));

try {
await login(page);
await page.goto(`${BASE}/site-costs`, { waitUntil: 'networkidle' });

if ((await page.getByRole('heading', { name: 'Затраты по стройке' }).count()) === 0) {
  console.error('Экрана «Затраты по стройке» нет. Включён ли модуль costs у сервера (Modules__Enabled=id,costs)? '
    + 'Без него проверять нечего, и зелёный прогон был бы отчётом о работе, которой не было.');
  process.exit(1);
}

const SITE = 'Объект прогона затрат';
const site = (await api('GET', '/constructions')).find(c => c.name === SITE)
  ?? await api('POST', '/constructions', { name: SITE });

const organizations = await api('GET', '/costs/organizations');
if (organizations.length === 0) {
  console.error('В справочнике нет ни одной организации — посев не отработал. Проверять нечего.');
  process.exit(1);
}

// Раздел стройки прогона — один раз, по имени: счёт на него даёт срезу «по разделам» строку раздела,
// а счета на стройку целиком — строку «без раздела».
const SECTION = 'Раздел прогона';
const section = (await api('GET', '/costs/constructions')).find(c => c.id === site.id)?.sections.find(s => s.name === SECTION)
  ?? await api('POST', `/constructions/${site.id}/sections`, { name: SECTION });

/** Счёт из одной строки-услуги, разнесённой на стройку прогона целиком — либо на её раздел. */
async function invoice(number, amount, onSection = null) {
  const created = await api('POST', '/costs/invoices', {
    requisites: {
      'Номер': number, 'Дата': '2026-09-01', 'Итого': amount,
      'Поставщик': { $ref: 'catalog', entryId: organizations[0].id },
    },
  });
  const view = await api('PUT', `/costs/invoices/${created.id}/lines`, {
    lines: [{ supplierText: 'Услуга прогона', amount }],
  });
  await api('PUT', `/costs/invoices/${created.id}/lines/${view.lines[0].id}/allocation`, {
    parts: [{ construction: site.id, section: onSection, amount }],
  });
  return created.id;
}

async function pay(id) {
  const seen = await api('POST', `/costs/invoices/${id}/paid/preview`, { paidOn: today });
  await api('POST', `/costs/invoices/${id}/paid`, { paidOn: today, document: 'п/п прогона', seen: seen.stamp });
}

// Оплаченный сегодня — в затратах текущего месяца; второй ждёт оплаты и в затраты не входит.
const today = (await api('GET', '/periods')).today;
await pay(await invoice(`ЗТР-О-${stamp}`, 700));
await pay(await invoice(`ЗТР-Р-${stamp}`, 300, section.id));
await invoice(`ЗТР-Н-${stamp}`, 150);

const month = today.slice(0, 7);
const report = page.locator('main');

/** Строка отчёта по началу её названия: число и стрелка в реестр. */
function row(name) {
  const line = report.locator('tr').filter({ has: page.locator('th[scope="row"]', { hasText: name }) }).first();
  return {
    amount: async () => number(await line.locator('td').last().innerText()),
    arrow: line.getByRole('link'),
  };
}

/** Итог колонки «Сумма» в открытом реестре. */
async function registryTotal() {
  await page.locator('tfoot').waitFor({ timeout: 10_000 });
  await settled(page);
  const headers = await page.locator('thead th').allInnerTexts();
  const at = headers.findIndex(h => /^Сумма(\s*\(|$)/.test(h.trim()));
  if (at < 0) throw new Error(`в реестре нет колонки «Сумма»: ${headers.join(' | ')}`);
  const cell = await page.locator('tfoot td, tfoot th').nth(at).innerText();
  return number(cell.split('\n')[0]);
}

const open = async query => {
  await page.goto('about:blank');
  await page.goto(`${BASE}/site-costs?${query}`, { waitUntil: 'networkidle' });
  await report.getByText('Не входит в затраты').waitFor({ timeout: 10_000 });
};

// ── 1. Экран стройки: затраты, итог и «к оплате» отдельно ─────────────────────────────────────────
await check('site-report-shows-costs-and-payable', async () => {
  await open(`site=${site.id}&from=${month}&to=${month}`);
  const total = await row('Итого затраты').amount();
  const payable = await row('К оплате').amount();
  if (process.env.SHOT) await page.screenshot({ path: process.env.SHOT });
  if (!(total >= 700)) throw new Error(`в затратах стройки нет оплаченного счёта прогона: итог ${total}`);
  if (!(payable >= 150)) throw new Error(`в «к оплате» нет неоплаченного счёта прогона: ${payable}`);

  const text = (await report.innerText()).replace(/\s/g, ' ');
  if (!text.includes('Расходные накладные в него пока не входят')) throw new Error('про накладные не сказано');
  // Суммы «затраты + к оплате» на экране нет нигде: её приняли бы за затраты.
  const merged = (total + payable).toLocaleString('ru-RU', { minimumFractionDigits: 2 }).replace(/\s/g, ' ');
  if (text.includes(merged)) throw new Error(`на экране есть сумма затрат и «к оплате» вместе: ${merged}`);
});

// ── 2. Каждая стрелка: число отчёта равно итогу «Суммы» в реестре ─────────────────────────────────
/** По каждой стрелке экрана: число отчёта равно итогу «Суммы» в реестре. @returns названия строк экрана. */
async function everyArrow(query, atLeast) {
  await open(query);
  const names = await report.locator('th[scope="row"]').allInnerTexts();
  const lines = names.map(n => n.split('·')[0].trim()).filter(Boolean);
  if (lines.length < atLeast) throw new Error(`строк отчёта меньше ${atLeast}: ${lines.join(' | ')}`);

  let followed = 0;
  for (const name of lines) {
    await open(query);
    const line = row(name);
    if (await line.arrow.count() === 0) continue;
    const expected = await line.amount();
    await line.arrow.click();
    const actual = await registryTotal();
    if (Math.abs(actual - expected) > 0.005)
      throw new Error(`«${name}»: в отчёте ${expected}, а итог «Суммы» в реестре — ${actual}`);
    followed++;
  }
  // Ни одной стрелки — не «все сошлись», а «сверять было нечего».
  if (followed < atLeast) throw new Error(`стрелок в реестр на экране стройки — ${followed}, а ждали не меньше ${atLeast}`);
  return lines;
}

// Контрагент, итог, к оплате — не меньше трёх.
await check('every-arrow-equals-the-registry-total', async () => {
  await everyArrow(`site=${site.id}&from=${month}&to=${month}`, 3);
});

// ── 2а. Срез по разделам: та же сумма, и каждая стрелка сходится с реестром ───────────────────────
await check('every-section-arrow-equals-the-registry-total', async () => {
  const query = `site=${site.id}&from=${month}&to=${month}`;
  await open(query);
  const bySuppliers = await row('Итого затраты').amount();

  // Переключатель — на экране, а не только параметром адреса: им человек и пользуется.
  await report.getByRole('button', { name: 'по разделам' }).click();
  await page.waitForURL(/by=sections/);
  if (Math.abs(await row('Итого затраты').amount() - bySuppliers) > 0.005)
    throw new Error('итог стройки по разделам не равен итогу по контрагентам');
  if (!(await row(SECTION).amount() >= 300)) throw new Error(`в срезе нет раздела «${SECTION}» со счётом прогона`);
  if (!(await row('без раздела').amount() >= 700)) throw new Error('в срезе нет строки «без раздела» со счётом прогона');

  // Раздел, «без раздела», итог, к оплате — не меньше четырёх.
  const lines = await everyArrow(`${query}&by=sections`, 4);
  if (!lines.includes(SECTION)) throw new Error(`после перезагрузки срез не по разделам: ${lines.join(' | ')}`);
});

// ── 3. Все стройки: строка стройки, итог равен реестру, «назад» возвращает отчёт ──────────────────
await check('all-sites-total-equals-the-registry-and-back-returns', async () => {
  const query = `from=${month}&to=${month}`;
  await open(query);
  if (!(await row(SITE).amount() >= 700)) throw new Error(`на экране всех строек нет стройки прогона «${SITE}»`);

  const total = row('Итого затраты');
  const expected = await total.amount();
  await total.arrow.click();
  const actual = await registryTotal();
  if (Math.abs(actual - expected) > 0.005) throw new Error(`итог всех строек: в отчёте ${expected}, в реестре ${actual}`);

  await page.goBack();
  await report.getByText('Не входит в затраты').waitFor({ timeout: 10_000 });
  if (!page.url().includes(query)) throw new Error(`«назад» вернул не тот отчёт: ${page.url()}`);
  if (Math.abs(await row('Итого затраты').amount() - expected) > 0.005) throw new Error('после «назад» итог другой');
});
} finally {
  await browser.close();
}

process.exitCode = summarize('Затраты по стройке');
