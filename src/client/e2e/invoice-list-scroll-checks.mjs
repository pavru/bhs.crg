// [COST-6.2], #2010/#2012: состояние строки для диктора не раздувает прокрутку оболочки.
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { BASE, LOCALE, createChecks, findPlaywright, launchBrowser, submitLogin } from './harness.mjs';

const EPS = 1;
const VIEWPORTS = [[1536, 704], [1366, 650], [390, 844]];
const SEARCH = 'Номер, поставщик, назначение…';
const TITLE = 'Счета на оплату';
const INVOICES = Array.from({ length: 100 }, (_, i) => ({
  id: `b20f2010-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
  number: `Прокрутка-2010-${String(i + 1).padStart(3, '0')}`,
  issuedOn: '2026-10-20', supplierId: null, supplierName: null, total: 120,
  state: i % 2 === 0 ? 'Черновик' : 'Разобран', payment: 'Не оплачен', dueDate: null,
  purpose: null, unconfirmedCount: 0, hasScan: false, linesCount: 1, linesWithoutNomenclature: 0,
  constructionIds: [],
}));

const isList = request => request.method() === 'GET' && new URL(request.url()).pathname === '/api/costs/invoices';

async function box(locator) {
  const rect = await locator.boundingBox({ timeout: 5_000 });
  assert.ok(rect, 'элемент вне раскладки');
  return rect;
}

async function geometry(page, nav, phone) {
  const sizes = await nav.evaluate(el => {
    const scroll = node => ({ top: node.scrollTop, height: node.clientHeight, content: node.scrollHeight });
    const work = el.closest('[data-testid="work-area"]');
    const outer = [];
    for (let parent = el.parentElement; parent && work?.contains(parent); parent = parent.parentElement) {
      if (/^(auto|scroll)$/.test(getComputedStyle(parent).overflowY)) outer.push(scroll(parent));
    }
    return { outer, document: scroll(document.scrollingElement) };
  });
  assert.ok(sizes.outer.length, 'в оболочке нет контейнера маршрута');
  const controls = await Promise.all([
    box(page.getByRole('heading', { name: TITLE })),
    box(nav.getByPlaceholder(SEARCH)),
    box(nav.getByRole('group', { name: 'Отбор списка счетов' })),
    ...(phone ? [box(page.getByTestId('bottom-nav'))] : []),
  ]);
  return { ...sizes, controls };
}

function contained(before, after) {
  assert.equal(after.outer.length, before.outer.length, 'изменилась структура внешней прокрутки');
  const expected = [...before.outer, before.document];
  [...after.outer, after.document].forEach((now, i) => {
    const was = expected[i];
    assert.ok(Math.abs(now.top - was.top) <= EPS && Math.abs(now.top) <= EPS
      && Math.abs(now.height - was.height) <= EPS && Math.abs(now.content - was.content) <= EPS
      && now.content - now.height <= EPS,
    `внешняя область ${i + 1}: scrollTop ${was.top} → ${now.top}, clientHeight ${now.height}, scrollHeight ${now.content}`);
  });
  const labels = ['заголовок', 'поиск', 'отборы', 'нижняя навигация'];
  after.controls.forEach((now, i) => {
    for (const key of ['x', 'y', 'width', 'height']) {
      assert.ok(Math.abs(now[key] - before.controls[i][key]) <= EPS,
        `${labels[i]}: ${key} ${before.controls[i][key]} → ${now[key]}`);
    }
  });
}

async function wheel(page, target, delta) {
  const { x, y, width, height } = await box(target);
  await page.mouse.move(x + width / 2, y + height / 2);
  // Playwright возвращает управление до прокрутки. Ждём само колесо и тишину после событий scroll.
  // На краю scroll не приходит, поэтому окно тишины начинается с wheel и продлевается каждым scroll.
  const input = await page.evaluateHandle(() => ({ done: new Promise(resolve => {
    let idle;
    const deadline = setTimeout(() => finish(false), 3_000);
    const finish = seen => {
      clearTimeout(deadline);
      clearTimeout(idle);
      document.removeEventListener('wheel', onWheel, true);
      document.removeEventListener('scroll', onScroll, true);
      resolve(seen);
    };
    const onScroll = () => {
      clearTimeout(idle);
      idle = setTimeout(() => finish(true), 100);
    };
    const onWheel = () => {
      document.addEventListener('scroll', onScroll, true);
      onScroll();
    };
    document.addEventListener('wheel', onWheel, { capture: true, passive: true, once: true });
  }) }));
  try {
    await page.mouse.wheel(0, delta);
    assert.ok(await input.evaluate(({ done }) => done), 'браузер не завершил обработку колеса');
  } finally {
    await input.dispose();
  }
}

async function visibleLast(page, column, last, phone) {
  const [list, row] = await Promise.all([box(column), box(last)]);
  assert.ok(row.y >= list.y - EPS && row.y + row.height <= list.y + list.height + EPS,
    'последняя строка не помещается в видимую часть списка');
  if (phone) {
    const fab = await box(page.getByTestId('primary-fab'));
    const nav = await box(page.getByTestId('bottom-nav'));
    assert.ok(row.y + row.height <= fab.y + EPS, 'последняя строка скрыта под плавающей кнопкой');
    assert.ok(fab.y + fab.height <= nav.y + EPS, 'плавающая кнопка заходит на нижнюю навигацию');
  }
}

async function checkViewport(page, url, { width, height }) {
  await page.setViewportSize({ width, height });
  await page.goto(url);
  const phone = width === 390;
  const nav = page.getByTestId('ld-nav');
  const rows = nav.getByTestId('invoice-row');
  const last = rows.nth(INVOICES.length - 1);
  await last.waitFor({ timeout: 10_000 });
  assert.equal(await rows.count(), INVOICES.length, 'фикстура показалась не целиком');
  const handle = await rows.first().evaluateHandle(row => {
    for (let el = row.parentElement; el && el.tagName !== 'NAV'; el = el.parentElement) {
      if (/^(auto|scroll)$/.test(getComputedStyle(el).overflowY)) return el;
    }
    return null;
  });
  const column = handle.asElement();
  assert.ok(column, 'у списка нет собственной области прокрутки');
  const end = await column.evaluate(el => el.scrollHeight - el.clientHeight);
  assert.ok(end > EPS, 'список поместился в колонку');
  const offset = () => column.evaluate(el => el.scrollTop);
  const geometryNow = () => geometry(page, nav, phone);
  const before = await geometryNow();

  if (!phone) {
    const detail = page.getByTestId('ld-detail');
    await detail.getByText('Выберите счёт', { exact: true }).waitFor({ timeout: 5_000 });
    await wheel(page, detail, 680);
    contained(before, await geometryNow());
    assert.ok(await offset() <= EPS, 'колесо над правой областью прокрутило список');
  }

  await wheel(page, column, 400);
  assert.ok(await offset() > EPS, 'колесо над списком не прокрутило список');
  contained(before, await geometryNow());

  // Firefox ограничивает смещение от одного события, даже если передать высоту всего списка.
  for (let i = 0; i < 40 && (await offset()) < end - EPS; i++) await wheel(page, column, 680);
  const bottom = await offset();
  assert.ok(Math.abs(bottom - end) <= EPS, `колесо не довело список до конца: ${bottom} из ${end}`);
  await visibleLast(page, column, last, phone);
  contained(before, await geometryNow());

  await wheel(page, column, 680);
  assert.ok(Math.abs(await offset() - end) <= EPS, 'дальнейшее колесо сдвинуло конец списка');
  await visibleLast(page, column, last, phone);
  contained(before, await geometryNow());

  await column.evaluate(el => { el.scrollTop = 0; });
  await rows.nth(INVOICES.length - 2).evaluate(el => el.focus({ preventScroll: true }));
  assert.ok(await offset() <= EPS, 'фокус для подготовки сценария прокрутил список');
  assert.ok((await box(last)).y > height, 'последняя строка уже видна до Tab');
  await page.keyboard.press('Tab');
  assert.ok(await last.evaluate(el => document.activeElement === el), 'Tab не перевёл фокус на последнюю строку');
  assert.ok(await offset() > EPS, 'фокус не прокрутил список');
  await visibleLast(page, column, last, phone);
  contained(before, await geometryNow());
}

export async function invoiceListScrollChecks(page, check, base = BASE) {
  // Отдельный контекст со входом из набора сохраняет отборы, поиск и размер его окна.
  const context = page.context();
  const sessionToken = await page.evaluate(() => sessionStorage.getItem('access_token'));
  const isolated = await context.browser().newContext({ locale: LOCALE, storageState: await context.storageState() });
  try {
    const url = `${base}/invoices`;
    if (sessionToken) await isolated.addInitScript(({ origin, token }) => {
      if (location.origin === origin) sessionStorage.setItem('access_token', token);
    }, { origin: new URL(base).origin, token: sessionToken });
    const listPage = await isolated.newPage();
    try {
      await listPage.route(url => url.pathname === '/api/costs/invoices', route => isList(route.request())
        ? route.fulfill({ json: INVOICES })
        : route.fallback());
      for (const [width, height] of VIEWPORTS) {
        await check(`список счетов ${width}×${height}: колесо и Tab прокручивают только колонку`,
          () => checkViewport(listPage, url, { width, height }));
      }
    } finally {
      // Вкладка вместе с маршрутами удаляется целиком. Выключать перехват на работающей вкладке не требуется.
      await listPage.close();
    }
    await check('после проверки прокрутки снова читается серверный список счетов', async () => {
      const serverPage = await isolated.newPage();
      const [response] = await Promise.all([
        serverPage.waitForResponse(r => isList(r.request()), { timeout: 10_000 }),
        serverPage.goto(url),
      ]);
      assert.ok(response.ok(), `чтение списка счетов: HTTP ${response.status()}`);
      const items = await response.json();
      assert.ok(Array.isArray(items) && !items.some(item => item.id === INVOICES[0].id), 'перехват не снят');
    });
  } finally {
    await isolated.close();
  }
}

if (import.meta.main) {
  const pkg = findPlaywright();
  const pw = process.argv.includes('--firefox') ? await import(pkg ? pathToFileURL(pkg).href : 'playwright') : null;
  const browser = pw ? await (pw.default ?? pw).firefox.launch({ headless: true }) : await launchBrowser();
  try {
    console.log(`${browser.browserType().name()} ${browser.version()}`);
    const page = await browser.newPage({ locale: LOCALE });
    const { check, summarize } = createChecks();
    await page.goto(`${BASE}/login`);
    await submitLogin(page);
    await invoiceListScrollChecks(page, check);
    process.exitCode = summarize('Прокрутка списка счетов');
  } finally {
    await browser.close();
  }
}
