// Smoke по формам настроек и профиля (issue #858, порция 4).
//
// Все они устроены одинаково: сервер отдаёт текущее состояние, человек правит, пришедший заново
// ответ обязан правку заместить. Раньше это делал эффект «пришли данные — перелей их в форму»,
// теперь — общий useServerForm (правка хранится вместе с тем ответом, от которого начата).
// Ошибка в такой замене не падает, а показывает ПУСТУЮ форму там, где данные есть, — экран
// выглядит «ещё не загрузился», и заметить это можно только глазами.
//
// Ничего не сохраняет: формы заполняются и бросаются вместе с браузером.
//
// Требует поднятых фронта (:5173) и бэка (:5000). Учётка — админская (настройки под AdminRoute).
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/settings-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

/**
 * Раскрывает раздел настроек, предварительно свернув предыдущий, и возвращает текст страницы.
 *
 * <p>Сворачивать обязательно: поля ищутся по странице целиком, и при двух раскрытых разделах
 * проверка «форма собралась из ответа» находила бы чужие заполненные поля — то есть оставалась бы
 * зелёной на пустой форме (проверено: со сломанным smtpForm она проходила).</p>
 */
let openTitle = null;
async function openSection(title) {
  if (openTitle) {
    await page.locator('button').filter({ hasText: openTitle }).first().click();
    await settled(page);
  }
  await page.locator('button').filter({ hasText: title }).first().click();
  await settled(page);
  openTitle = title;
  return page.locator('body').innerText();
}

try {

await page.goto(`${BASE}/settings`);
await settled(page);

// ── Интеграции: пять кусков формы поверх одного ответа ─────────────────────────
// Значок вкладки отдаёт сервер, но Chromium держит его в своей базе по АДРЕСУ значка до недели и
// сервер не спрашивает (issue #1315). Свежесть даёт только адрес с меткой версии, и ставить её
// обязано приложение — при каждой загрузке. Без этой проверки значок молча отставал бы от
// логотипа на неделю, и заметить это можно было бы только глазами.
await check('tab-icon-link-carries-the-icons-version', async () => {
  const { status, body } = await callApi(page, 'GET', '/branding');
  if (status !== 200) throw new Error(`адрес оформления ответил ${status}`);
  const expected = `?v=${encodeURIComponent(body.icons.version)}`;

  const links = () => page.evaluate(() => ({
    tab: document.querySelector('link[rel="icon"]')?.getAttribute('href'),
    apple: document.querySelector('link[rel="apple-touch-icon"]')?.getAttribute('href'),
    stored: localStorage.getItem('crg-brand-icons'),
  }));
  const now = await links();
  if (now.tab !== `/api/branding/icons/tab${expected}`) throw new Error(`ссылка значка вкладки — «${now.tab}», метки версии в ней нет`);
  if (now.apple !== `/api/branding/icons/apple${expected}`) throw new Error(`ссылка значка iPhone — «${now.apple}»`);
  if (now.stored !== body.icons.version) throw new Error(`метка не запомнена для следующей загрузки: «${now.stored}»`);

  // Следующая загрузка: ссылку ставит ранний скрипт — до приложения и до запроса оформления.
  // Запрос оформления придерживаем, иначе не отличить скрипт от приложения.
  let release;
  const held = new Promise(resolve => { release = resolve; });
  await page.route('**/api/branding', async route => { await held; await route.continue(); });
  try {
    await page.reload({ waitUntil: 'domcontentloaded' });
    const early = await links();
    if (early.tab !== `/api/branding/icons/tab${expected}`)
      throw new Error(`до ответа об оформлении ссылка значка — «${early.tab}»: ранний скрипт её не поставил`);
  } finally {
    release();
    // С ожиданием: придержанный запрос ещё в обработчике, и снятая под ним подмена обернулась бы
    // необработанным «Route is already handled» — падением всего набора, а не одной проверки.
    await page.unrouteAll({ behavior: 'wait' });
  }
  await settled(page);

  // И адрес с меткой — живой: отвечает картинкой и длинным кешем.
  const served = await page.evaluate(async url => {
    const res = await fetch(url);
    return { status: res.status, cache: res.headers.get('cache-control'), type: res.headers.get('content-type') };
  }, now.tab);
  if (served.status !== 200 || !served.type?.startsWith('image/'))
    throw new Error(`адрес значка ответил ${served.status} ${served.type}`);
  if (!served.cache?.includes('max-age=604800')) throw new Error(`у адреса с действующей меткой кеш «${served.cache}»`);
});

await check('integrations-form-shows-saved-engines', async () => {
  const t = await openSection('ПОИСК И РАСПОЗНАВАНИЕ');
  for (const engine of ['Google Gemini', 'Anthropic Claude', 'Ollama']) {
    if (!t.includes(engine)) throw new Error(`движка «${engine}» на форме нет`);
  }
  // Хотя бы один включённый движок — признак того, что форма собралась из ОТВЕТА, а не из пустышки.
  const checked = await page.locator('input[type=checkbox]:checked').count();
  if (checked < 1) throw new Error('ни один движок не отмечен — форма собралась не из ответа сервера');
});

await check('integrations-domain-list-keeps-typed-text', async () => {
  const areas = page.locator('textarea');
  if ((await areas.count()) < 1) throw new Error('списков доменов на форме нет');
  const area = areas.first();
  const before = await area.inputValue();
  if (!before.trim()) throw new Error('список доменов пуст — проверять сохранение ввода не на чем');
  // Хвостовой перевод строки — то, ради чего список и хранит «сырой» текст: очищенный список от
  // него не меняется, и текст обязан остаться на экране как набран.
  await area.fill(`${before}\n`);
  await settled(page);
  const after = await area.inputValue();
  if (after !== `${before}\n`) throw new Error(`набранное не удержалось: «${JSON.stringify(after)}»`);
  await area.fill(before);   // возвращаем как было (наружу и так уходит тот же список)
  await settled(page);
});

// ── Почта, обновления, резервное копирование ──────────────────────────────────
await check('smtp-form-shows-saved-settings', async () => {
  const t = await openSection('ПОЧТА (SMTP)');
  if (!/SMTP|Хост|Порт/i.test(t)) throw new Error(`раздел почты не раскрылся: ${t.slice(-200)}`);
  // Проверяем ИМЕННО сохранённые значения (адрес отправителя), а не порт: у порта есть разумное
  // умолчание, и пустая форма выглядела бы заполненной.
  const values = await page.locator('input:not([type])').evaluateAll(els => els.map(e => e.value));
  if (!values.some(v => v && v.includes('@')))
    throw new Error(`форма почты собралась не из ответа сервера: ${JSON.stringify(values)}`);
});

await check('updates-toggle-reflects-server', async () => {
  const t = await openSection('ОБНОВЛЕНИЯ');
  if (!/Проверять обновления|Последняя проверка|обновлени/i.test(t))
    throw new Error(`раздел обновлений не раскрылся: ${t.slice(-200)}`);
});

// Пояс компании (issue #1148): пока он не выбран, сутки считаются по поясу сервера — и отказа
// нет нигде. Сказать об этом обязан экран; проверка сверяет его слова с ответом сервера.
await check('company-time-zone-names-what-is-in-force', async () => {
  const { body } = await callApi(page, 'GET', '/settings/company');
  await openSection('КОМПАНИЯ');
  const block = page.getByTestId('company-time-zone');
  const text = (await block.innerText()).replace(/\s+/g, ' ');
  if (!/Сейчас у компании: \d\d\.\d\d\.\d{4}, \d\d:\d\d/.test(text))
    throw new Error(`нет числа и часа у компании: ${text}`);
  const unset = /Пояс не выбран — действует пояс сервера/.test(text);
  if (unset !== (body.timeZoneId === null))
    throw new Error(`экран говорит «${unset ? 'не выбран' : 'выбран'}», сервер — ${body.timeZoneId}`);
  if (unset && !text.includes(body.serverTimeZoneId)) throw new Error('пояс сервера не назван');
  if (await block.getByRole('button', { name: 'Сохранить пояс' }).isEnabled())
    throw new Error('«Сохранить пояс» доступна без правки');

  // Выбор без сохранения пропадает, стоит свернуть раздел, — предупреждает о нём только пометка
  // в шапке раздела. Выбираем любой пояс, кроме действующего, и бросаем.
  const other = body.timeZones.find(z => z.id !== body.timeZoneId);
  await block.getByRole('combobox').click();
  await page.getByRole('option', { name: new RegExp(other.city) }).click();
  const header = page.locator('button').filter({ hasText: 'КОМПАНИЯ' }).first();
  if (!/не сохранено/.test(await header.innerText())) throw new Error('шапка раздела не говорит о несохранённом поясе');
  await block.getByRole('button', { name: 'Отменить' }).click();
  if (/не сохранено/.test(await header.innerText())) throw new Error('пометка осталась после отмены');
});

await check('backup-schedule-form-shows-saved-values', async () => {
  const t = await openSection('РЕЗЕРВНОЕ КОПИРОВАНИЕ');
  if (!/расписан|Хранить|копи/i.test(t)) throw new Error(`раздел копий не раскрылся: ${t.slice(-200)}`);
  const times = await page.locator('input[type=time]').evaluateAll(els => els.map(e => e.value));
  if (times.length && !times.some(v => /^\d{2}:\d{2}/.test(v)))
    throw new Error(`время в расписании пусто: ${JSON.stringify(times)}`);
});

/**
 * Запрос копий опрашивается каждые 15 секунд, и перечитывание, ничего не изменившее, правку
 * выбрасывать не должно. Это стережёт инвариант «серверное значение стабильно по ссылке»: отдай в
 * useServerForm свежесобранный объект — и форма сбрасывалась бы на каждый ответ.
 *
 * <p>Чего проверка НЕ ловит: сценария из ревью PR #864, где вместе с ответом меняется след службы
 * (`running`, `lastRunAt`). Чтобы он случился, надо в самом деле снять копию — то есть записать
 * файл в хранилище, а прогон этого делать не должен. Правка «форма поверх трёх полей расписания,
 * а не поверх всего ответа» проверена чтением, живой проверки под ней нет.</p>
 */
await check('backup-schedule-edit-survives-refetch', async () => {
  const time = page.locator('input[type=time]').first();
  if (!(await time.count())) throw new Error('поля времени в расписании нет');
  const before = await time.inputValue();
  const probe = before === '04:44' ? '05:55' : '04:44';
  await time.fill(probe);
  // Ждём САМ опрос, а не «дольше 15 с»: ответ на перечитывание копий, пришедший ПОСЛЕ правки, и
  // есть событие, после которого поломка возможна. Пауза в 18 с платилась целиком, а обещала
  // меньше: задержись опрос — проверка прочла бы поле до него и позеленела, ничего не проверив.
  await page.waitForResponse(
    r => r.request().method() === 'GET' && new URL(r.url()).pathname.endsWith('/backup/files'),
    { timeout: 30_000 },
  ).catch(() => { throw new Error('за 30 с копии не перечитались ни разу — проверять правку не на чем'); });
  await settled(page);   // ответ — ещё не экран: форме нужно его принять
  const after = await time.inputValue();
  if (after !== probe) throw new Error(`перечитывание выбросило правку: набрано «${probe}», стало «${after}»`);
  await time.fill(before);   // ничего не сохраняем — возвращаем как было
  await settled(page);
});

// ── Профиль ───────────────────────────────────────────────────────────────────
await check('profile-shows-account-name-and-keeps-typing', async () => {
  await page.goto(`${BASE}/profile`);
  await settled(page);
  // Первый input на странице — скрытый выбор аватара; имя лежит в текстовом, а TextField атрибут
  // type не проставляет — потому `:not([type])`, а не `[type=text]`.
  const field = page.locator('input:not([type])').first();
  const before = await field.inputValue();
  if (!before.trim()) throw new Error('имя учётной записи в форме пусто');
  // Набранное держится при перерисовках страницы. Настоящего перечитывания здесь НЕ происходит:
  // у useAccount нет ни интервала, ни повода перечитать за это время — проверять «правка переживает
  // ответ сервера» этим прогоном было бы самообманом (поймано ревью PR #864).
  await field.fill(`${before} X`);
  await settled(page);
  const after = await field.inputValue();
  if (after !== `${before} X`) throw new Error(`набранное имя не удержалось: «${after}»`);
  await field.fill(before);
  await settled(page);
});

// ── Профили распознавания ─────────────────────────────────────────────────────
// Мои права в профиле (issue #954, ТЗ AUTH-16.6). Проверяется не «блок есть», а что он отвечает
// на вопрос «почему у меня нет раздела»: группы раскрываются и внутри стоят ОБЪЯСНЕНИЯ, а не
// только коды. Список из одних кодов — это те же коды, только в рамке.
//
// ⚠️ Сверка идёт со СПИСКОМ прав, а не с текстом страницы. Первая редакция искала слова
// «видеть|править|…» по всему body и проходила на сломанном блоке: «править» нашлось внутри
// «ОтПРАВИТЬ подтверждение» — в форме смены почты этажом ниже. Проверка по подстроке в чужом
// абзаце подтверждает лишь то, что на странице есть буквы.
await check('profile-shows-my-permissions-in-words', async () => {
  await page.goto(`${BASE}/profile`);
  await settled(page);

  const core = page.getByRole('button', { name: /^Ядро/ });
  if ((await core.count()) < 1) throw new Error('в профиле нет группы прав «Ядро»');
  await core.first().click();
  await settled(page);

  const list = page.locator('ul').filter({ hasText: /core\./ }).first();
  const items = await list.locator('li').allInnerTexts();
  if (items.length < 5) throw new Error(`в раскрытой группе ${items.length} прав — список не собрался`);

  // У каждого права, кроме кода, обязана быть строка словами: код в переписке с администратором
  // нужен, но объясняет он только тому, кто читал справочник.
  const isCode = (line) => /^[a-z*][a-z.*]+$/i.test(line.trim());
  const bare = items.filter(t => t.split(/\r?\n/).filter(l => l.trim() && !isCode(l)).length === 0);
  if (bare.length > 0) throw new Error(`права показаны одними кодами, без объяснений: ${bare.length} из ${items.length}`);
});

// Тема и язык — личные настройки, и место им в профиле (ТЗ AUTH-16.6). Язык до #954 лежал в
// «Настройке системы» за правом core.system.manage: сменить формат дат мог только администратор
// экземпляра. Проверка держит переезд с двух сторон.
//
// ⚠️ Заголовки ищутся ПО РОЛИ, а не по тексту страницы. Первая редакция сверялась с body: якорь
// «Шаблоны» находился в боковом меню (то есть подтверждал открытие ЛЮБОЙ страницы), а запрет не
// срабатывал вовсе — заголовки разделов настроек подняты в верхний регистр средствами CSS, и
// innerText отдаёт их прописными.
await check('appearance-and-language-live-in-the-profile', async () => {
  await page.goto(`${BASE}/profile`);
  await settled(page);
  if ((await page.getByRole('heading', { name: /Оформление и язык/i }).count()) < 1)
    throw new Error('в профиле нет блока «Оформление и язык»');
  // ⚠️ Имя группы — ЛИЧНОЕ («…, личная настройка»), а не общее. Такой же переключатель стоит в
  // боковой панели на каждом экране: с общим именем проверка удовлетворялась панелью и проходила
  // даже с убранным из профиля переключателем (ревью PR #1000).
  if ((await page.getByRole('group', { name: 'Тема оформления, личная настройка' }).count()) < 1)
    throw new Error('в профиле нет своего выбора темы');
  if ((await page.getByRole('button', { name: /Русский \(Россия\)/ }).count()) < 1)
    throw new Error('в профиле нет выбора языка');

  await page.goto(`${BASE}/settings`);
  await settled(page);
  if ((await page.getByRole('heading', { name: /шаблоны/i }).count()) < 1)
    throw new Error('раздел настроек не открылся — запрет ниже ничего не значит');
  if ((await page.getByRole('heading', { name: /региональн/i }).count()) > 0)
    throw new Error('язык остался в «Настройке системы»: личная настройка под административным правом');
});

await check('recognition-profile-detail-shows-fields', async () => {
  await page.goto(`${BASE}/recognition-profiles`);
  await settled(page);
  const rows = page.locator('button').filter({ hasText: /штамп|счёт|обложк|титул|таблиц/i });
  if ((await rows.count()) < 1) throw new Error('в списке профилей нечего открыть');
  await rows.first().click();
  await settled(page);
  const inputs = await page.locator('input[type=text], input:not([type])').evaluateAll(els => els.map(e => e.value));
  if (!inputs.some(v => v && v.trim())) throw new Error('поля профиля пусты — форма собралась не из ответа');
});

// Секция «Название и логотип» о значках из логотипа (issue #1315): что вышло — картинками, что не
// вышло — словами. Идёт ПОСЛЕДНЕЙ: раскрытая секция запоминается в localStorage, а проверки выше
// ищут поля по странице целиком и чужой раскрытой секции не терпят.
//
// Ответ `GET /api/branding` берётся настоящий и правится: логотип на стендах разный (в CI его нет
// вовсе), а загружать его ради проверки значило бы писать в оформление стенда. Метка версии в
// правке остаётся настоящей — по ней превью и грузят картинки.
await check('branding-says-what-came-of-the-logo', async () => {
  let change = body => body;
  let broken = false;
  await page.route('**/api/branding', async route => {
    if (broken) return route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
    const response = await route.fetch();
    if (!response.ok()) return route.fulfill({ response });
    await route.fulfill({ response, json: change(await response.json()) });
  });

  const previews = page.getByTestId('brand-icon-previews');
  const failed = page.getByTestId('brand-icons-failed');
  const elongated = page.getByTestId('brand-icons-elongated');
  const row = page.getByTestId('branding-logo-row');

  const loadFailed = page.getByTestId('branding-load-failed');
  const unread = page.getByTestId('branding-logo-unread');

  /** Страница настроек с раскрытой секцией оформления. */
  async function opened() {
    await page.goto(`${BASE}/settings`);
    await settled(page);
    const header = page.locator('button').filter({ hasText: 'Название и логотип' }).first();
    if ((await header.getAttribute('aria-expanded')) !== 'true') { await header.click(); await settled(page); }
  }

  /** Секция с оформлением, каким его назвал бы сервер после правки `patch`. */
  async function shown(patch) {
    change = patch;
    await opened();
    await row.waitFor({ timeout: 8000 }).catch(() => { throw new Error('строка логотипа не появилась'); });
  }

  try {
    // 1) Значки построены, логотип вчетверо шире своей высоты, название не задано.
    await shown(b => ({ ...b, hasLogo: true, logoVersion: b.logoVersion ?? '1', isCustom: false,
      icons: { ...b.icons, state: 'custom', problem: null, aspect: 4 } }));
    const version = (await callApi(page, 'GET', '/branding')).body.icons.version;
    const sources = await previews.locator('img').evaluateAll(imgs => imgs.map(i => ({ alt: i.alt, src: i.getAttribute('src'), loaded: i.complete && i.naturalWidth > 0 })));
    const expected = [
      ['Значок вкладки на светлом фоне', 'tab'], ['Значок вкладки на тёмном фоне', 'tab'], ['Значок приложения', 'apple'],
    ];
    if (sources.length !== expected.length) throw new Error(`в превью ${sources.length} картинок вместо трёх`);
    for (const [i, [alt, name]] of expected.entries()) {
      const want = `/api/branding/icons/${name}?v=${encodeURIComponent(version)}`;
      if (sources[i].alt !== alt || sources[i].src !== want)
        throw new Error(`превью ${i + 1}: «${sources[i].alt}» ${sources[i].src}, а ждали «${alt}» ${want}`);
    }
    // Значок приложения — PNG: по нему и видно, что адрес с меткой отдаёт картинку.
    if (!sources[2].loaded) throw new Error('картинка значка приложения в превью не загрузилась');
    if (!(await elongated.count())) throw new Error('логотип вчетверо шире высоты, а предупреждения о вытянутом логотипе нет');
    const warning = (await elongated.innerText()).replace(/\s+/g, ' ');
    if (!warning.includes('занимает 4 из 16 точек по высоте')) throw new Error(`предупреждение о вытянутом логотипе: «${warning}»`);
    if (!warning.includes('Название тогда задайте выше')) throw new Error('при незаданном названии предупреждение не зовёт его задать');
    if (await failed.count()) throw new Error('плашка отказа стоит при построенных значках');

    // 2) Значки не построены: вместо превью — слова, и совет по причине. Файл при этом сохранён, а
    //    картинкой не является — браузер его тоже не показывает: об этом говорит рамка логотипа, а
    //    плашка про сам логотип не утверждает ничего (раньше уверяла, что он виден в шапке).
    await page.route('**/api/branding/logo*', route => route.fulfill({ status: 404, body: '' }));
    await shown(b => ({ ...b, hasLogo: true, logoVersion: b.logoVersion ?? '1',
      icons: { ...b.icons, state: 'failed', problem: 'file', aspect: null } }));
    if (await previews.count()) throw new Error('при отказе построения показаны превью — стандартные значки выданы за построенные');
    if (!(await failed.count())) throw new Error('значки не построены, а слов об этом под логотипом нет');
    const refusal = (await failed.innerText()).replace(/\s+/g, ' ');
    for (const part of ['Значки из логотипа не построены:', 'Сохраните логотип в PNG и загрузите его.'])
      if (!refusal.includes(part)) throw new Error(`в плашке отказа нет «${part}»: «${refusal}»`);
    if (/сохранён|шапке/.test(refusal)) throw new Error(`плашка отказа говорит о самом логотипе, хотя не знает, виден ли он: «${refusal}»`);
    await unread.waitFor({ timeout: 8000 })
      .catch(() => { throw new Error('файл логотипа браузер не показывает, а рамка логотипа об этом не говорит'); });
    if (await elongated.count()) throw new Error('предупреждение о вытянутом логотипе стоит при непостроенных значках');
    await page.unroute('**/api/branding/logo*');

    // 3) Логотипа нет: ни превью, ни слов о значках — только «нет» и «Загрузить».
    await shown(b => ({ ...b, hasLogo: false, logoVersion: null,
      icons: { ...b.icons, state: 'standard', problem: null, aspect: null } }));
    if ((await previews.count()) || (await failed.count()) || (await elongated.count()))
      throw new Error('без логотипа секция что-то говорит о значках из него');
    if (!(await row.innerText()).includes('нет') || !(await row.getByRole('button', { name: 'Загрузить' }).count()))
      throw new Error(`без логотипа в строке: «${(await row.innerText()).replace(/\s+/g, ' ')}»`);
    if (await row.getByRole('button', { name: 'Убрать' }).count()) throw new Error('без логотипа предлагается его убрать');

    // 4) Оформление не загрузилось: секция говорит об этом и даёт повторить. Без слов она стояла бы
    //    с пустым полем и без строки логотипа — отказ, одетый в загрузку.
    broken = true;
    await opened();
    await loadFailed.waitFor({ timeout: 15000 })
      .catch(() => { throw new Error('оформление не загрузилось, а секция об этом молчит'); });
    if (await row.count()) throw new Error('оформление не получено, а строка логотипа стоит — «не знаем» выдано за «логотипа нет»');
    broken = false;
    await loadFailed.getByRole('button', { name: 'Повторить' }).click();
    await row.waitFor({ timeout: 8000 }).catch(() => { throw new Error('после «Повторить» строка логотипа не появилась'); });
    if (await loadFailed.count()) throw new Error('оформление получено, а слова об отказе остались');

    // 5) Сбой построителя сервер повторяет сам — и секция спрашивает оформление снова: плашка
    //    уходит без перезагрузки страницы. Минуту не ждём: часы страницы подменены и переведены.
    //    Шаг последний, после него страница не нужна — подменённые часы никому не мешают.
    //
    //    Сбой здесь тот, что бывает после перезапуска: хранилище не отвечает, поэтому и картинка
    //    логотипа не читается. Когда оно поднялось, уйти обязаны ОБА отказа — и плашка, и «не
    //    читается» в рамке: адрес логотипа при этом прежний, и рамка, помнящая отказ по одному
    //    адресу, стояла бы со своими словами рядом с превью из этого самого логотипа.
    let storageUp = false;
    await page.route('**/api/branding/logo*', async route => {
      if (!storageUp) return route.fulfill({ status: 404, body: '' });
      // Картинкой логотипа служит значок приложения: он есть на любом стенде, а логотипа в CI нет.
      const picture = await page.request.get(`${BASE}/api/branding/icons/apple`);
      await route.fulfill({ status: 200, contentType: 'image/png', body: await picture.body() });
    });
    await page.clock.install();
    const notBuilt = b => ({ ...b, hasLogo: true, logoVersion: b.logoVersion ?? '1',
      icons: { ...b.icons, state: 'failed', version: `${b.icons.version}-до`, problem: 'builder', aspect: null } });
    await shown(notBuilt);
    if (!(await failed.count())) throw new Error('при сбое построителя плашки отказа нет');
    await unread.waitFor({ timeout: 8000 })
      .catch(() => { throw new Error('хранилище не отвечает, а рамка логотипа об этом не говорит'); });
    storageUp = true;
    // Как отвечает сервер: запрос, запустивший повтор, результата не приносит — построение идёт в
    // фоне, и ответ на него прежний отказ. Построенные значки видит только СЛЕДУЮЩИЙ запрос. Подмена,
    // отдающая значки первым же опросом, проверяла бы поведение, которого у сервера нет.
    let polls = 0;
    change = b => (polls++ === 0 ? notBuilt(b) : { ...b, hasLogo: true, logoVersion: b.logoVersion ?? '1',
      icons: { ...b.icons, state: 'custom', problem: null, aspect: 1 } });
    const asked = page.waitForResponse(r => new URL(r.url()).pathname === '/api/branding', { timeout: 8000 })
      .catch(() => { throw new Error('полминуты спустя секция оформление не переспросила'); });
    await page.clock.fastForward(31_000);
    await asked;
    if (!(await failed.count())) throw new Error('повтор только начат, а плашка «не построены» уже ушла');
    await page.clock.fastForward(30_000);
    await previews.waitFor({ timeout: 8000 })
      .catch(() => { throw new Error('значки достроены, а следующий опрос их не показал — оформление не переспрошено'); });
    if (await failed.count()) throw new Error('значки достроены, а плашка «не построены» осталась');
    await unread.waitFor({ state: 'detached', timeout: 8000 })
      .catch(() => { throw new Error('значки построены из логотипа, а рамка рядом всё ещё говорит «не читается»'); });
    const logoShown = await row.locator('img[alt="Логотип компании"]').evaluate(i => i.complete && i.naturalWidth > 0).catch(() => false);
    if (!logoShown) throw new Error('хранилище поднялось, а логотип в рамке так и не показан');
  } finally {
    await page.unrouteAll({ behavior: 'wait' });
  }
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke форм настроек');
