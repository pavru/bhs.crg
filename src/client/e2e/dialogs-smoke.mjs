// Smoke по диалогам наборов данных и документов (issue #858, порция 2).
//
// Здесь тоже убраны эффекты-синхронизаторы: «открылось — перелей пропсы в состояние», «закрылось —
// сбрось», «пришёл ответ сервера — скопируй в редактируемое». Замены — монтирование по открытию,
// инициализаторы состояния и локальный ОВЕРРАЙД поверх серверного значения. Ошибка в такой замене
// не падает, а тихо показывает не то: пустой список групп, чужой поиск, забытый выбор шаблонов.
//
// Почти ничего не сохраняет: правки разбиения и материализации бросаются вместе с браузером. Пишут
// по-настоящему две группы проверок — галка шаблона на демо-документе уезжает PUT'ом, обработка
// источника правится «чужой» правкой и сохраняется (issue #1141), — и обе возвращают исходное
// состояние в finally, чтобы прерванный прогон не оставил за собой чужой выбор и чужую обработку.
//
// Требует поднятых фронта (:5173), бэка (:5000) и хранилища (Garage, :3900) — экран разбиения
// читает страницы PDF из него. Плюс демо-данные, см. e2e/README.md.
//
// В CI гоняются 13 проверок из 16: три проверки разбиения PDF требуют РАСПОЗНАННЫХ страниц, то
// есть ИИ-движка, которого там нет (issue #872). Пропуск включается пустым `SMOKE_PDF_FILE_ID` и
// называется вслух в итоге прогона — молчаливый превратил бы «13 из 16» в «13 из 13».
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/dialogs-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';

/**
 * Набор с РАСПОЗНАННЫМИ страницами. ПУСТАЯ строка ⇒ три проверки разбиения PDF пропускаются:
 * страницы распознаёт ИИ-движок, которого в CI нет и, вероятно, не будет (issue #872). Пустой её
 * ставит `ci.yml` — переменной уровня работы: «чего не умеет окружение» объявляет само окружение,
 * а не посев. Умолчание — набор живой базы, где прогон запускается руками.
 *
 * `??`, а не `||`: заданная пустая строка — это ответ «такого набора нет», и подменять её
 * умолчанием значит гонять проверки по чужому идентификатору из моей базы.
 */
const PDF_FILE = process.env.SMOKE_PDF_FILE_ID ?? '688d45ed-834e-4d54-b74f-db1d4220e994';

const AOSR = '250701.ЭОМ-1.АОСР';
// Имена, которые прогон ищет ТОЧНЫМ совпадением, приходят из окружения: посев заводит их с
// суффиксом «(посев)» (иначе он умирал бы на занятом имени в рабочей базе — см. seed.mjs), а
// умолчания здесь — имена ЖИВОЙ базы, где прогон и запускается руками.
const MATERIALS_DOC = process.env.SMOKE_MATERIALS_DOC || '250701.ЭОМ-1.2.Реестр материалов';
const DATASET_FILE = process.env.SMOKE_DATASET_FILE || 'Счет на оплату';
const UNION_TYPE = process.env.SMOKE_UNION_TYPE || 'Документ произвольный';
const WORKS_TYPE = process.env.SMOKE_WORKS_UNION_TYPE || 'Работы АОСР';
const MATERIALS_TYPE = process.env.SMOKE_MATERIALS_UNION_TYPE || 'Материалы АОСР';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, skip, summarize } = createChecks();

await login(page);

/** Имя типа приходит из окружения и содержит скобки — в регулярное выражение его надо экранировать. */
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const checkedVariant = async () => {
  const dlg = page.locator('[role=dialog]').last();
  const radios = await dlg.getByRole('radio').evaluateAll(els =>
    els.map(e => ({ label: e.innerText.trim(), on: e.getAttribute('aria-checked') === 'true' })));
  return radios.find(r => r.on)?.label ?? null;
};

try {

// ── Разбиение PDF: серверные группы плюс правка поверх ─────────────────────────
//
// Проверки объявлены СПИСКОМ, потому что имена нужны дважды: чтобы прогнать и чтобы пропустить.
// Записанные в двух местах, они разъезжаются молча — переименуй проверку, и прогон продолжил бы
// печатать «3 пропущено» с именем, которого больше нет, а «79 из 82» перестало бы сходиться.
// Одно объявление — и такому расхождению взяться неоткуда.
//
// `save` — локатор, а не найденный элемент: он ленив, поэтому объявляется до перехода на экран.
const save = page.getByRole('button', { name: /^Сохранить$/ }).first();

/**
 * Листы — ПО ПОДПИСИ, а не по тегу `img`.
 *
 * ⚠️ Картинка на этом экране не только лист: первым в разметке идёт ЛОГОТИП оболочки
 * (`/api/branding/logo`, 24×20, alt = имя продукта) — если он загружен. На дев-стенде он загружен,
 * в посеве CI нет, поэтому `img.first()` оказывался листом ровно там, где проверку никто не читает.
 * На стенде он щёлкал логотип: лист не выделялся, панель «Выделено страниц» не рисовалась, и клик
 * по «Отделить в новый документ» висел до истечения срока — красным без поломки (issue #1120).
 *
 * ⚠️ И счётчик тоже: `img.count() >= 1` выполнялся ОДНИМ ЛОГОТИПОМ, то есть «ни одного листа» не
 * срабатывало бы и при нуле листов — утверждение было зелёным, не проверяя обещанного.
 *
 * Та же ловушка, что уже описана ниже про `nth(1)` и колокольчик уведомлений: общая оболочка
 * подмешивает в выборку свои элементы, и адресоваться надо тем, что принадлежит самому экрану.
 */
const sheets = page.locator('img[alt^="Страница"]');

const pdfChecks = [
['pdf-grouping-loads-groups', async () => {
  const t = await page.locator('body').innerText();
  for (const g of ['Обложка', 'Титульный лист', 'Без группы']) {
    if (!t.includes(g)) throw new Error(`группы «${g}» на экране нет`);
  }
  if ((await sheets.count()) < 1) throw new Error('ни одного листа');
  // Правки ещё не было — сохранять нечего. Если бы «грязно» и содержимое разошлись, здесь бы и
  // вылезло: кнопка активна на нетронутом экране.
  if (!(await save.isDisabled())) throw new Error('«Сохранить» активна на нетронутом разбиении');
}],

['pdf-grouping-edit-enables-save', async () => {
  await sheets.first().click();
  // Панель действий рисуется ТОЛЬКО когда в группе есть выделенный лист (`hasSelectionHere`).
  // Ждём её и называем отсутствие словами: иначе отказ приезжает сырым таймаутом щелчка по
  // «Отделить в новый документ», по которому не видно, что не сработало ВЫДЕЛЕНИЕ, а не кнопка
  // (проверено сломом выделения, issue #1120).
  await page.getByText(/Выделено страниц/).first().waitFor({ timeout: 10_000 })
    .catch(() => { throw new Error('лист не выделился — панель действий над выделением не появилась'); });
  await page.getByRole('button', { name: /Отделить в новый документ/ }).first().click();
  await page.waitForTimeout(900);
  if (await save.isDisabled()) throw new Error('после правки «Сохранить» так и не стала активной');
  const t = await page.locator('body').innerText();
  if (!/1 документ/.test(t)) throw new Error(`новый документ в сводке не появился: ${t.slice(-300)}`);
}],

['pdf-page-viewer-opens', async () => {
  await page.getByRole('button', { name: /Просмотреть лист крупно/ }).first().click();
  await page.waitForTimeout(2500);
  const dlg = page.locator('[role=dialog]').last();
  if (!/Лист \d/.test(await dlg.innerText())) throw new Error('заголовка «Лист N» нет');
  const img = dlg.locator('img').first();
  await img.waitFor({ state: 'attached', timeout: 20000 })
    .catch(() => { throw new Error('изображение листа не появилось за 20 с'); });
  const ok = await img.evaluate(el => el.complete && el.naturalWidth > 0);
  if (!ok) throw new Error('изображение листа не разобралось (naturalWidth = 0)');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(600);
}],
];

if (!PDF_FILE) {
  const why = 'нужен набор с распознанными страницами (ИИ-движок), посев его не создаёт';
  for (const [name] of pdfChecks) skip(name, why);
} else {
  await page.goto(`${BASE}/datasets/files/${PDF_FILE}/grouping`);
  // Ждём ЛИСТ, а не любую картинку: логотип оболочки есть на экране с первого кадра, и ожидание
  // `img` заканчивалось бы до того, как приедет хоть одна страница (см. `sheets` выше).
  //
  // Истечение срока тут ГЛОТАЕМ: сказать «ни одного листа» — дело утверждения ниже, у которого есть
  // имя проверки и слова. Брошенное отсюда, оно убило бы набор целиком, и тринадцать проверок
  // превратились бы в один сырой таймаут посреди перехода.
  await sheets.first().waitFor({ timeout: 30000 }).catch(() => {});
  await page.waitForTimeout(1500);
  for (const [name, fn] of pdfChecks) await check(name, fn);
}

// ── Материализация: активный вариант union ────────────────────────────────────
await page.goto(`${BASE}/datasets`);
await page.waitForTimeout(2500);
await page.getByText(DATASET_FILE, { exact: false }).first().click();
await page.waitForTimeout(2500);
// Кебаб источника — ПО ИМЕНИ, а не порядковым номером. Раньше стояло `nth(1)` с пояснением
// «кебаб первого источника», и пояснение было верным лишь по совпадению: `aria-haspopup` на этой
// странице есть и у КОЛОКОЛЬЧИКА уведомлений в оболочке (`haspopup="dialog"`), он идёт нулевым и
// сдвигает нумерацию ровно на единицу. Пропади колокольчик — и тот же `nth(1)` молча уехал бы на
// второй источник (проверено пробой DOM: на экране ровно две такие кнопки — колокольчик и меню
// источника). Имя ни от оболочки, ни от порядка не зависит.
const sourceMenu = page.locator('button[aria-label="Действия над источником"]').first();
if (!(await sourceMenu.count())) {
  throw new Error(`у набора «${DATASET_FILE}» нет ни одного источника — материализацию открывать не из чего`);
}
await sourceMenu.click();
await page.waitForTimeout(700);
await page.getByText('Материализация', { exact: false }).first().click();
await page.waitForTimeout(1800);

await check('materialize-dialog-opens', async () => {
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/Материализация источника/.test(t)) throw new Error(`не тот диалог: ${t.slice(0, 140)}`);
});

await check('materialize-union-shows-variants', async () => {
  const dlg = page.locator('[role=dialog]').last();
  await dlg.getByText('не материализовать', { exact: false }).first().click();
  await page.waitForTimeout(1000);
  const picker = page.locator('[role=dialog]').last();
  await picker.locator('input').first().fill(UNION_TYPE);
  await page.waitForTimeout(800);
  await picker.getByText(UNION_TYPE, { exact: true }).first().click();
  await page.waitForTimeout(1800);
  const on = await checkedVariant();
  if (on !== 'Документ') throw new Error(`активен вариант «${on}», ждали первый — «Документ»`);
});

// Ключевая проверка порции. Раньше активный вариант был копией, которую эффект переливал из
// маппинга; переключение на НЕзамапленный вариант оставляло маппинг пустым, эффект видел «нет
// замапленного» и тут же возвращал первый вариант — выбрать второй было нельзя вовсе.
await check('materialize-union-switch-to-empty-variant-sticks', async () => {
  const dlg = page.locator('[role=dialog]').last();
  const select = dlg.locator('select').first();
  if (!(await select.count())) throw new Error('нет селектора колонки — нечего маппить');
  const columns = await select.locator('option').evaluateAll(o => o.map(x => x.textContent.trim()));
  const column = columns.find(c => c && !/не привязано/i.test(c));
  if (!column) throw new Error('в селекторе нет ни одной колонки источника');
  await select.selectOption({ label: column });
  await page.waitForTimeout(900);
  if ((await checkedVariant()) !== 'Документ') throw new Error('маппинг сбил активный вариант');

  await dlg.getByRole('radio', { name: /^Проект$/ }).first().click();
  await page.waitForTimeout(1000);
  const on = await checkedVariant();
  if (on !== 'Проект') throw new Error(`после переключения активен «${on}» — выбор не удержался`);
});

// Смена типа обязана снимать и пометку выбранного варианта. «Отбросится сама» она только пока у
// типов не совпадают ключи полей: у «Работы АОСР» и «Материалы АОСР» второй вариант — один и тот
// же ключ «Реестр», и пометка пережила бы смену типа (поймано ревью PR #862).
await check('materialize-type-change-resets-chosen-variant', async () => {
  const dlg = page.locator('[role=dialog]').last();
  const pickType = async (name) => {
    // Поле типа — кнопка с названием выбранного типа (после первого выбора плейсхолдера уже нет).
    // Имена берём из констант: они приходят из окружения, и зашитый здесь литерал разъехался бы
    // с посеянным типом молча — кнопка не нашлась бы, а сказано было бы про варианты.
    const shown = new RegExp(`${escapeRe(UNION_TYPE)}|${escapeRe(WORKS_TYPE)}`);
    await dlg.locator('button').filter({ hasText: shown }).first().click();
    await page.waitForTimeout(900);
    const picker = page.locator('[role=dialog]').last();
    await picker.locator('input').first().fill(name);
    await page.waitForTimeout(800);
    await picker.getByText(name, { exact: true }).first().click();
    await page.waitForTimeout(1600);
  };
  await pickType(WORKS_TYPE);
  if ((await checkedVariant()) !== 'Работы') throw new Error('новый тип открылся не на первом варианте');
  await dlg.getByRole('radio', { name: /^Реестр$/ }).first().click();
  await page.waitForTimeout(900);
  if ((await checkedVariant()) !== 'Реестр') throw new Error('второй вариант не выбрался');

  await pickType(MATERIALS_TYPE);
  const on = await checkedVariant();
  if (on !== 'Материалы') throw new Error(`после смены типа активен «${on}» — пометка пережила смену`);
});

await page.keyboard.press('Escape');
await page.waitForTimeout(800);

// ── Обработка источника: правка с устаревшей копии (issue #1141) ───────────────
// Диалог обработки собран по копии источника на странице. Источник тем временем правит другой
// человек — сохранение обязано получить отказ и показать его в диалоге, а не затереть чужую правку.
//
// «Другой человек» здесь — запрос к API мимо страницы, от имени того же пользователя: серверу важна
// не личность, а версия обработки, которую называет правка.
const apiAs = (method, path, body) => page.evaluate(async ([method, path, body]) => {
  const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
  const res = await fetch(`/api${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}, [method, path, body]);

/** Источник, чьё меню открывает `sourceMenu`: первый у набора — в том же порядке, что на экране. */
const smokeSource = async () => {
  const files = await apiAs('GET', '/datasets/files?scope=System');
  const file = files.find(f => f.name.includes(DATASET_FILE));
  if (!file?.sources?.length) throw new Error(`набор «${DATASET_FILE}» с источником не найден среди системных`);
  return file.sources[0];
};
/** Диалог обработки по пункту меню источника; заголовок диалога совпадает с пунктом. */
const openProcessingDialog = async (item) => {
  await sourceMenu.click();
  await page.waitForTimeout(700);
  await page.getByText(item, { exact: true }).first().click();
  await page.waitForTimeout(1000);
  const dlg = page.locator('[role=dialog]').last();
  if (!(await dlg.innerText()).includes(item)) throw new Error(`диалог «${item}» не открылся`);
  return dlg;
};
const openSortDialog = () => openProcessingDialog('Сортировка строк');
/** Ждём, пока диалог закроется сам (сохранение прошло); остался — отдаём его отказ. */
const closedAfterSave = async (dlg, what) => {
  await page.waitForFunction(() => !document.querySelector('[role=dialog]'), null, { timeout: 15000 })
    .catch(() => {});
  if (await page.locator('[role=dialog]').count())
    throw new Error(`${what} не сохранилось: «${(await refusalOf(dlg)).slice(0, 160)}»`);
};
const saveButton = (dlg) => dlg.getByRole('button', { name: /^Сохранить$/ });
const refusalOf = async (dlg) => (await dlg.locator('[role=alert]').count())
  ? (await dlg.locator('[role=alert]').first().innerText()) : '';
/** Перечитывание списка наборов страницей — то, что подменяет копию источника под диалогом. */
const filesRefetched = () => page.waitForResponse(
  r => r.request().method() === 'GET' && /\/api\/datasets\/files(\?|$)/.test(r.url()), { timeout: 10000 });

const sourceBefore = await smokeSource();
const FOREIGN = 'smoke_чужая_правка';
try {
  let staleDialog = null;

  await check('processing-save-from-stale-copy-is-refused', async () => {
    staleDialog = await openSortDialog();
    await apiAs('PUT', `/datasets/sources/${sourceBefore.id}/processing`, {
      ifMatch: sourceBefore.processingVersion,
      computedColumns: [...(sourceBefore.computedColumns ?? []), { alias: FOREIGN, expr: '1' }],
    });

    const refetched = filesRefetched();
    await saveButton(staleDialog).click();
    await staleDialog.locator('[role=alert]').first().waitFor({ timeout: 10000 }).catch(() => {});
    const said = await refusalOf(staleDialog);
    if (!/тем временем изменили/.test(said)) throw new Error(`отказа в диалоге нет: «${said.slice(0, 160)}»`);
    // Отказ страница встречает перечитыванием источников — ждём его: следующая проверка стоит на нём.
    await refetched;
    await page.waitForTimeout(800);
  });

  // Ключевая проверка. Страница уже перечитала источник и знает его НОВУЮ версию, а диалог открыт с
  // прежней копии. Возьми сохранение версию из страницы, а не из снимка, с которого диалог открыт, —
  // оно прошло бы: черновик по старым данным уехал бы с версией «я видел новые».
  await check('processing-stale-dialog-stays-refused-after-page-refresh', async () => {
    if (!staleDialog) throw new Error('диалог с устаревшей копии не открыт — предыдущая проверка не дошла');
    await saveButton(staleDialog).click();
    await page.waitForTimeout(2500);
    if (!(await staleDialog.count()) || !/Сортировка строк/.test(await staleDialog.innerText()))
      throw new Error('диалог закрылся — устаревшая правка сохранена');
    if (!/тем временем изменили/.test(await refusalOf(staleDialog)))
      throw new Error('повторное сохранение из того же диалога не получило отказа');
    const now = await smokeSource();
    if (JSON.stringify(now.sortSpec) !== JSON.stringify(sourceBefore.sortSpec))
      throw new Error('сортировка источника изменилась, хотя сохранение отклонено');
    if (!(now.computedColumns ?? []).some(c => c.alias === FOREIGN))
      throw new Error('чужая правка вычисляемых колонок пропала');
  });

  // Свежий диалог сохраняется — и следующий за ним тоже. Вторая половина — про то, что сохранение
  // отпускает человека, только когда страница ПЕРЕЧИТАЛА источник: первая правка меняет версию
  // обработки, и диалог, открытый с прежней копии, получил бы отказ «источник изменили» на свою же
  // правку. Перечитывание здесь нарочно задержано: без задержки оно успевает и так, и проверка была
  // бы зелёной при любом коде (поломка «мутация не ждёт перечитывания» её не роняла).
  await check('processing-next-dialog-opens-from-saved-copy', async () => {
    if (await page.locator('[role=dialog]').count()) {
      await page.locator('[role=dialog]').last().getByRole('button', { name: /^Отмена$/ }).click();
      await page.waitForTimeout(800);
    }
    const slowFiles = /\/api\/datasets\/files(\?|$)/;
    await page.route(slowFiles, async route => {
      await new Promise(resolve => setTimeout(resolve, 4000));
      await route.continue();
    });
    try {
      // Правка, которая меняет версию: сброс вычисляемых колонок убирает и «чужую» колонку.
      const computed = await openProcessingDialog('Вычисляемые колонки');
      await computed.getByRole('button', { name: /^Сбросить$/ }).click();
      await closedAfterSave(computed, 'сброс вычисляемых колонок из свежего диалога');

      const sort = await openSortDialog();
      await saveButton(sort).click();
      await closedAfterSave(sort, 'сохранение сортировки сразу после своей же правки');
    } finally {
      await page.unroute(slowFiles);
    }
    if (((await smokeSource()).computedColumns ?? []).length)
      throw new Error('вычисляемые колонки не сброшены, хотя диалог закрылся');
  });
} finally {
  // Вернуть обработку, какой она была до прогона, — с версией, какая у источника сейчас.
  if (await page.locator('[role=dialog]').count()) await page.keyboard.press('Escape');
  const now = await smokeSource();
  await apiAs('PUT', `/datasets/sources/${now.id}/processing`, {
    ifMatch: now.processingVersion,
    computedColumns: sourceBefore.computedColumns, sortSpec: sourceBefore.sortSpec,
  });
}

// ── Копирование документа: цель забывается при закрытии ────────────────────────
await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
await page.waitForSelector('tbody tr', { timeout: 15000 });
await page.waitForTimeout(1000);

const openCopy = async () => {
  await page.locator('tbody tr').first().locator('button[aria-haspopup]').first().click();
  await page.waitForTimeout(700);
  await page.getByText('Скопировать', { exact: false }).first().click();
  await page.waitForTimeout(1200);
};

await check('copy-dialog-opens-on-set-picker', async () => {
  await openCopy();
  const t = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать в комплект/.test(t)) throw new Error(`не пикер комплекта: ${t.slice(0, 160)}`);
});

await check('copy-dialog-reopens-at-picker-not-confirm', async () => {
  const picker = page.locator('[role=dialog]').last();
  const target = picker.getByRole('option').or(picker.locator('button')).filter({ hasText: /ЭОМ|СКС|ОВиК/ }).first();
  if (!(await target.count())) throw new Error('в пикере нет ни одного комплекта-цели');
  await target.click();
  await page.waitForTimeout(1500);
  const confirm = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать «/.test(confirm)) throw new Error(`подтверждение не открылось: ${confirm.slice(0, 200)}`);
  await page.keyboard.press('Escape');   // отмена — цель обязана забыться вместе с закрытием
  await page.waitForTimeout(900);
  await openCopy();
  const again = await page.locator('[role=dialog]').last().innerText();
  if (!/Скопировать в комплект/.test(again))
    throw new Error(`повторное открытие показало не пикер, а: ${again.slice(0, 200)}`);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(700);
});

// ── Вкладка генерации: выбор шаблонов ─────────────────────────────────────────
await page.getByText(AOSR, { exact: false }).first().click();
await page.waitForSelector('[role=dialog]', { timeout: 15000 });
await page.waitForTimeout(2000);
const editor = page.locator('[role=dialog]').first();

await check('generation-tab-shows-templates', async () => {
  // Вкладки редактора — не role=button (getByRole их не видит), поэтому ищем по тексту кнопки.
  await editor.locator('button').filter({ hasText: /^Генерация$/ }).first().click();
  await page.waitForTimeout(2000);
  const t = await editor.innerText();
  if (!/Статус:/.test(t)) throw new Error(`вкладка генерации не открылась: ${t.slice(0, 200)}`);
  const boxes = await editor.locator('input[type=checkbox]').count();
  if (boxes < 1) throw new Error('ни одного шаблона в списке — проверять выбор не на чем');
});

await check('generation-tab-selection-follows-click', async () => {
  const box = editor.locator('input[type=checkbox]').first();
  const before = await box.isChecked();
  try {
    await box.click();
    await page.waitForTimeout(1200);
    if ((await box.isChecked()) === before) throw new Error('галка не переключилась');
  } finally {
    // Возврат — в finally: выбор шаблонов уходит на сервер, и провалившаяся проверка не должна
    // оставлять демо-документ с чужой настройкой (следующий прогон стартовал бы с неё).
    if ((await box.isChecked()) !== before) {
      await box.click();
      await page.waitForTimeout(1200);
    }
  }
  if ((await box.isChecked()) !== before) throw new Error('галка не вернулась в исходное состояние');
});

// ── Пикер документа качества: выбранный тип поиска переживает закрытие ─────────
// Прежний эффект сбрасывал при открытии всё, кроме типа для веб-поиска, — это было сделано
// намеренно: материалы связывают подряд, десятками, и выбирать тип заново на каждый значит делать
// одну и ту же работу столько раз, сколько строк в реестре (поймано ревью PR #862).
await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
await page.waitForSelector('tbody tr', { timeout: 15000 });
await page.waitForTimeout(1000);
await page.getByText(MATERIALS_DOC, { exact: false }).first().click();
await page.waitForSelector('[role=dialog]', { timeout: 15000 });
await page.waitForTimeout(2500);
const matEditor = page.locator('[role=dialog]').first();
await matEditor.locator('button').filter({ hasText: /^Документы качества$/ }).first().click();
await page.waitForTimeout(3000);

const openLinkPicker = async () => {
  // Без якорей: hasText сверяет textContent (с переносами вокруг значка), а не innerText.
  await matEditor.locator('button').filter({ hasText: /Связать/ }).first().click();
  await page.waitForTimeout(2000);
  const dlg = page.locator('[role=dialog]').last();
  if (!/Документ качества/.test(await dlg.innerText())) throw new Error('пикер документа качества не открылся');
  await dlg.locator('button').filter({ hasText: /^Поиск в интернете$/ }).first().click();
  await page.waitForTimeout(900);
  return dlg;
};
// Поле типа — триггер TypePickerField с aria-label «Тип документа качества»; показанное значение
// это его первая строка (второй идёт код типа).
const TYPE_TRIGGER = 'button[aria-label="Тип документа качества"]';
const firstLine = (text) => text.split(/\r?\n/)[0].trim();
const shownSearchType = async (dlg) => firstLine(await dlg.locator(TYPE_TRIGGER).first().innerText());

let firstType = '';
await check('quality-picker-opens-with-default-search-type', async () => {
  const dlg = await openLinkPicker();
  if (!(await dlg.locator(TYPE_TRIGGER).count())) throw new Error('поля типа для поиска нет');
  firstType = await shownSearchType(dlg);
  if (!firstType) throw new Error('тип для поиска не показан');
});

await check('quality-picker-keeps-chosen-search-type-across-close', async () => {
  const dlg = page.locator('[role=dialog]').last();
  await dlg.locator(TYPE_TRIGGER).first().click();
  await page.waitForTimeout(1200);
  const picker = page.locator('[role=dialog]').last();
  const options = await picker.locator('button')
    .evaluateAll(els => els.map(e => e.innerText.split(/\r?\n/)[0].trim()).filter(Boolean));
  const other = options.find(o => o !== firstType && /письмо|Декларация|Паспорт|Сертификат/i.test(o));
  if (!other) throw new Error(`второго типа в пикере нет: ${options.slice(0, 12).join(' | ')}`);
  await picker.getByText(other, { exact: true }).first().click();
  await page.waitForTimeout(1200);
  if ((await shownSearchType(page.locator('[role=dialog]').last())) !== other)
    throw new Error('тип не сменился в самом окне');

  await page.keyboard.press('Escape');
  await page.waitForTimeout(1000);
  const again = await openLinkPicker();
  const kept = await shownSearchType(again);
  if (kept !== other) throw new Error(`после повторного открытия тип «${kept}», а выбирали «${other}»`);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(700);
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke диалогов наборов и документов');
