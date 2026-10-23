// Живой прогон ПОДСКАЗОК ПО ВВЕДЁННОМУ РАНЕЕ (issue #1533, эпик #1527, ТЗ CORE-8, CORE-9, TYPE-2).
//
// ЗАЧЕМ ОН ЕСТЬ. Юнит-тесты проверяют поле на подменённом ответе: кого оно спрашивает и как ведёт себя
// список. Не проверяют они главного — что ответ НАСТОЯЩЕГО сервера по настоящим записям доходит до
// поля: своя стройка первой, чужая — только встреченная дважды и под чертой, флажок в редакторе типа
// переживает сохранение и действительно гасит запросы. Каждое из этого — про связку клиента, адреса и
// базы целиком, и в браузере видно только оно.
//
// ⚠️ ДАННЫЕ ПРОГОН ГОТОВИТ СЕБЕ САМ — тип документа, подтип номенклатуры, две стройки, документы,
// позицию и счёт, все с приметой прогона в имени. Посеянные не годятся: проверка флажка снимает его, и
// второй запуск на той же базе проверял бы уже выключенное поле.
//
// ⚠️ Элементы ищутся по видимому тексту, ролям и подписям — так заодно проверяется, что человек видит
// нужные слова, а читалка экрана — роль поля со списком.
//
// Требует поднятых фронта и бэка, посеянной организации и ВКЛЮЧЁННОГО модуля `costs`
// (`Modules__Enabled=id,costs`) — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/suggest-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, until, pickOption } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));

const { check, summarize } = createChecks();
const stamp = Date.now().toString().slice(-6);

/** Запросы к адресу подсказок с начала прогона — по ним проверка флажка судит «зовёт / не зовёт». */
const suggestRequests = [];
page.on('request', r => { if (r.url().includes('/api/field-suggestions')) suggestRequests.push(r.url()); });

await login(page);

/** Запрос к приложению ТЕМ ЖЕ токеном, что у открытой страницы; отказ — исключение с телом. */
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

const field = (key, type = 'string', extra = {}) => ({ key, title: key, type, required: false, ...extra });

/** Тип документа прогона: строка с подсказкой, текст, вычисляемое и поле однозначности. */
async function documentType() {
  const created = await api('POST', '/document-types', {
    name: `Акт подсказок ${stamp}`, code: `АктПодсказок${stamp}`, kind: 'Document', parentId: null,
    schema: JSON.stringify({
      fields: [
        field('Марка'),
        field('Примечание', 'text'),
        field('Код', 'string', { computed: true, expression: '1' }),
        field('Номер', 'string', { tags: ['doc.number'] }),
      ],
    }),
    isAbstract: false, module: 'core',
  });
  return created.id;
}

/** Стройка с одним комплектом — тем, где лежат документы прогона. */
async function constructionWithSet(name) {
  const construction = await api('POST', '/constructions', { name: `${name} ${stamp}` });
  await api('POST', `/constructions/${construction.id}/sections`, { name: 'Раздел' });
  const section = (await api('GET', `/constructions/${construction.id}`)).sections[0];
  const set = await api('POST', '/document-sets', { sectionId: section.id, name: 'Комплект' });
  return { constructionId: construction.id, setId: set.id };
}

async function documentIn(where, typeId, name, requisites) {
  const inst = await api('POST', `/document-sets/${where.setId}/documents`, { documentTypeId: typeId });
  await api('PUT', `/document-sets/${where.setId}/documents/${inst.id}/name`, { name });
  if (requisites) await api('PUT', `/document-sets/${where.setId}/documents/${inst.id}/requisites`, requisites);
  return inst.id;
}

async function openDocument(where, instanceId) {
  await page.goto(`${BASE}/document-sets/${where.constructionId}/sets/${where.setId}/docs/${instanceId}`);
  await page.getByTestId('document-editor').waitFor({ timeout: 20_000 });
  await settled(page);
}

/** Пункты открытого списка подсказок — текстом, в порядке показа, вместе с чертой «С других строек». */
async function shownList() {
  const list = page.getByRole('listbox', { name: /^Введённое ранее/ });
  await list.waitFor({ timeout: 10_000 });
  return (await list.innerText()).split('\n').map(s => s.trim()).filter(Boolean);
}

try {
  const own = `ВВГнг ${stamp} своя`;
  const otherOnce = `ВВГнг ${stamp} одна`;
  const otherTwice = `ВВГнг ${stamp} две`;
  const docType = await documentType();
  const here = await constructionWithSet('Стройка подсказок');
  const there = await constructionWithSet('Чужая стройка подсказок');
  await documentIn(here, docType, 'Источник', { Марка: own });
  const target = await documentIn(here, docType, 'Цель', {});
  await documentIn(there, docType, 'Чужой 1', { Марка: otherOnce });
  await documentIn(there, docType, 'Чужой 2', { Марка: otherTwice });
  await documentIn(there, docType, 'Чужой 3', { Марка: otherTwice });

  // ── 1. Реквизиты: своя стройка первой, чужая — дважды и под чертой ──────────
  await check('реквизиты: своё значение первым, чужое — встреченное дважды и под «С других строек»', async () => {
    await openDocument(here, target);
    const mark = page.getByRole('combobox', { name: 'Марка' });
    await mark.fill(`ВВГнг ${stamp}`);
    const shown = await shownList();
    if (shown[0] !== own) throw new Error(`первым стоит не своё значение: ${JSON.stringify(shown)}`);
    const split = shown.indexOf('С других строек');
    if (split < 0 || shown.indexOf(otherTwice) < split)
      throw new Error(`чужое значение не под чертой «С других строек»: ${JSON.stringify(shown)}`);
    if (shown.includes(otherOnce))
      throw new Error(`значение, встреченное на чужой стройке однажды, всплыло: ${JSON.stringify(shown)}`);

    await mark.press('ArrowDown');
    await mark.press('Enter');
    if ((await mark.inputValue()) !== own) throw new Error(`Enter не подставил «${own}»: ${await mark.inputValue()}`);
  });

  await check('реквизиты: значения, равного набранному целиком, в списке нет', async () => {
    const mark = page.getByRole('combobox', { name: 'Марка' });
    await mark.fill('');
    await mark.fill(`ВВГнг ${stamp} св`);
    await until(async () => { if (!(await shownList()).includes(own)) throw new Error(`нет «${own}» в списке`); });
    // Ответ на полное значение приходит после задержки набора, которой `settled` не видит: ждём сам ответ.
    const answered = page.waitForResponse(r => r.url().includes('/api/field-suggestions')
      && new URL(r.url()).searchParams.get('q') === own, { timeout: 10_000 });
    await mark.fill(own);
    const { items } = await (await answered).json();
    if (items.some(i => i.value === own)) throw new Error('адрес отдал значение, равное набранному');
    const list = page.getByRole('listbox', { name: /^Введённое ранее/ });
    await until(async () => {
      if ((await list.count()) > 0 && (await shownList()).includes(own))
        throw new Error('набранное целиком значение предлагается само себе');
    });
  });

  // ── 2. Номенклатура: производитель из прошлого счёта в шаге «Новая позиция» ──
  const types = await api('GET', '/document-types');
  const tagsOf = t => {
    try { return (typeof t.schema === 'string' ? JSON.parse(t.schema) : t.schema)?.tags ?? []; }
    catch { return []; }
  };
  const nomenclature = types.find(t => tagsOf(t).includes('role.nomenclature'));
  const organizations = await api('GET', '/costs/organizations?purpose=choice');
  const kindName = `Позиция подсказок ${stamp}`;
  const maker = `Камкабель${stamp}`;
  let kindId = null;

  await check('шаг «Новая позиция»: производитель прошлой позиции в списке, Enter подставляет', async () => {
    if (!nomenclature) throw new Error('справочника номенклатуры нет: ни один тип не несёт role.nomenclature (посев?)');
    if (organizations.length === 0) throw new Error('в справочнике нет организаций — посев не отработал');
    // Вид со своим ключом: у посеянного корня поля «Идентификатор» нет, и шаг назвал бы его негодным.
    kindId = (await api('POST', '/document-types', {
      name: kindName, code: `ПозицияПодсказок${stamp}`, kind: 'Composite', parentId: nomenclature.id,
      schema: JSON.stringify({
        fields: [
          field('Название', 'string', { required: true, tags: ['identity:1'] }),
          field('Производитель', 'string', { tags: ['identity:2'] }),
          field('Артикул', 'string', { tags: ['identity:3', 'nomenclature.article'] }),
        ],
      }),
      isAbstract: false, module: 'core',
    })).id;
    // Прошлая позиция — заведённая той же дверью, что зовёт шаг.
    await api('POST', '/nomenclature', { typeId: kindId, values: { Название: `Кабель ${stamp}`, Производитель: maker } });

    const number = `СЧ-П${stamp}`;
    await api('POST', '/costs/invoices', {
      requisites: {
        'Номер': number, 'Дата': '2026-10-23',
        'Поставщик': { $ref: 'catalog', entryId: organizations[0].id },
        'Плательщик': { $ref: 'catalog', entryId: (organizations[1] ?? organizations[0]).id },
        'Итого': 100,
      },
    });
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
    await page.getByRole('button', { name: 'Сохранить строки' }).waitFor({ timeout: 15_000 });
    await page.getByRole('button', { name: 'Добавить строку' }).click();
    await page.getByRole('button', { name: 'выбрать позицию' }).first().click();
    await page.getByRole('button', { name: 'Завести новую позицию…' }).click();
    await pickOption(page.getByRole('combobox', { name: 'Вид' }), kindName);
    // Закрываясь, список «Вид» возвращает фокус себе — поле, набранное раньше, потеряло бы его, и
    // подсказка закрылась бы до запроса. Человек так быстро не набирает: ждём и встаём в поле сами.
    await settled(page);

    // Похожие спрашиваются, только когда позиция названа: строка счёта пустая, называем сами.
    await page.getByRole('combobox', { name: 'Название' }).fill(`Кабель ${stamp} новый`);
    const field_ = page.getByRole('combobox', { name: 'Производитель' });
    await field_.click();
    await field_.fill(maker.slice(0, -2));
    await page.getByRole('option', { name: maker }).waitFor({ timeout: 10_000 });
    const similar = page.waitForRequest(r => r.url().endsWith('/api/nomenclature/similar')
      && (r.postData() ?? '').includes(maker), { timeout: 10_000 });
    await field_.press('ArrowDown');
    await field_.press('Enter');
    if ((await field_.inputValue()) !== maker) throw new Error(`Enter не подставил «${maker}»: ${await field_.inputValue()}`);
    // Подставленное идёт тем же путём, что набранное: «Похожие» спрошены о нём.
    await similar.catch(() => { throw new Error('после подстановки похожие не спрошены о подставленном производителе'); });
  });

  // ── 3. Флажок в редакторе типа ──────────────────────────────────────────────
  /**
   * Раскрыть карточку поля по подписи; раскрыта всегда одна, так что чтение страницы — про неё.
   * ⚠️ Страница открывается заново: у раскрытой карточки чипы тэгов — тоже кнопки, и чип «Номер
   * документа» начинается с «Номер» — щелчок по подписи попал бы в чип соседнего поля.
   */
  async function openCard(url, title) {
    await page.goto(url, { waitUntil: 'networkidle' });
    await header(title).click();
    await settled(page);
  }
  const flag = () => page.getByRole('checkbox', { name: /Подсказывать введённое ранее/ });
  /** Шапка карточки: подпись и ключ поля подряд — так её не спутать с чипом тэга. */
  const header = title => page.locator('button').filter({ hasText: new RegExp(`^${title}${title}`) }).first();

  await check('флажок: снят, сохранён и после перезагрузки снят — пометка в шапке, чипа нет', async () => {
    const types = `${BASE}/document-types?type=${docType}`;
    await openCard(types, 'Марка');
    if (!(await flag().isChecked())) throw new Error('у строкового поля флажок не стоит по умолчанию');
    await flag().uncheck();
    const saved = page.waitForResponse(r => r.request().method() === 'PUT' && /\/schema$/.test(r.url()), { timeout: 15_000 });
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    const answer = await saved;
    if (!answer.ok()) throw new Error(`схема не сохранилась: ${answer.status()}`);

    await page.reload({ waitUntil: 'networkidle' });
    const collapsed = await header('Марка').innerText();
    if (!collapsed.includes('без подсказки')) throw new Error(`в шапке нет пометки «без подсказки»: ${collapsed}`);
    if (collapsed.includes('Не подсказывать')) throw new Error(`тэг показан чипом: ${collapsed}`);
    await openCard(types, 'Марка');
    if (await flag().isChecked()) throw new Error('после перезагрузки флажок снова стоит');
  });

  await check('флажок: снятый гасит в форме и список, и запрос к адресу', async () => {
    await openDocument(here, target);
    const before = suggestRequests.length;
    const mark = page.getByRole('textbox', { name: 'Марка' });
    await mark.fill(`ВВГнг ${stamp}`);
    await page.waitForTimeout(600);   // дольше задержки набора: запрос, будь он, уже ушёл бы
    if (suggestRequests.length !== before) throw new Error(`ушло запросов к адресу подсказок: ${suggestRequests.length - before}`);
    if ((await page.getByRole('combobox', { name: 'Марка' }).count()) !== 0) throw new Error('поле осталось полем со списком');
  });

  await check('флажок: у текста и вычисляемого его нет, у поля однозначности — причина', async () => {
    const types = `${BASE}/document-types?type=${docType}`;
    for (const title of ['Примечание', 'Код']) {
      await openCard(types, title);
      if ((await flag().count()) !== 0) throw new Error(`у поля «${title}» есть флажок`);
      if ((await page.getByText(/^Не подсказывается/).count()) !== 0) throw new Error(`у поля «${title}» строка причины`);
    }
    await openCard(types, 'Номер');
    if ((await flag().count()) !== 0) throw new Error('у номера документа есть флажок');
    await page.getByText('Не подсказывается: номер у каждого документа свой.').waitFor({ timeout: 5_000 });

    if (!kindId) throw new Error('вид позиции не заведён — шаг «Новая позиция» упал раньше');
    const kinds = `${BASE}/composite-types?type=${kindId}`;
    await openCard(kinds, 'Артикул');
    if ((await flag().count()) !== 0) throw new Error('у артикула есть флажок');
    await page.getByText(/^Не подсказывается: артикул/).waitFor({ timeout: 5_000 });
    await openCard(kinds, 'Производитель');
    // «Идентификатор» — компонент ключа, не однозначность (решение 23.10.2026): флажок стоит.
    if (!(await flag().isChecked())) throw new Error('у производителя (компонент ключа) флажок не стоит');
  });
} finally {
  await browser.close();
}

// ⚠️ Значение `summarize` отдаётся КАК ЕСТЬ: это готовый код возврата (0 — прошло). Сверяет `suites.test.mjs`.
process.exitCode = summarize('Подсказки по введённому ранее');
