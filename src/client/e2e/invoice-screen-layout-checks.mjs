// Раскладка экрана счёта по варианту Г (costs-invoice-screen-10-layout, D-10.11, ТЗ COST-6.2) — живым
// браузером на четырёх размерах ноутбука.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Едет внутри него, под его
// входом и с его помощниками (`h`), как `invoice-layout-checks`.
//
// Счёт доводится до «всех сообщений сразу»: двойник по номеру, метки «со скана, не проверено», сбой
// распознавания (файл, который сервер принимает как PDF, но не читает) и сорок строк с расхождением. На
// таком счёте прежняя неподвижная шапка занимала больше окна, и до строк было не дойти — прогон обязан
// краснеть на ней (проверка проверки).
//
// ⚠️ Подпанель раздела у пользователя ОТКРЫТА — так было в разборе Дизайнера. Выбор — настройка человека
// на сервере: прогон ставит её и возвращает прежнюю в finally, иначе соседние прогоны шли бы с ней.

import { expandInvoiceParts } from './harness.mjs';
import { invoiceStatusChecks } from './invoice-status-checks.mjs';
import { invoiceScanMarkChecks } from './invoice-scan-mark-checks.mjs';
import { statusOpen } from './invoice-status.mjs';
import { saveState, untilSaveState } from './invoice-save.mjs';

/** Размеры из D10 зонтика: ноутбук, ноутбук с панелями браузера, браузер не на весь экран, 125 % на 1280. */
export const LAPTOPS = [[1366, 768], [1280, 720], [1366, 650], [1024, 768]];

/**
 * Место файла по `placeOf` (D-10.3) у черновика с метками: рабочая область — окно без рейла 80 (подпанель при
 * открытом счёте скрыта), панель — 38 % от 420 до 520, рядом — пока форме остаётся 720. Списка при файле
 * рядом нет; на 1024 файл поверх, и списка нет тоже. Числа — те же, что в `invoicePlace.test.ts`.
 */
const FILE_BESIDE = { 1366: 489, 1280: 456, 1024: null };

/** Пять полей первого этапа (ответ 4 листа 0) — в окне без прокрутки на 1366×650. */
const FIRST_STAGE = ['Поставщик', 'Плательщик', 'Номер', 'Дата', 'Сумма к оплате'];

const LINES = 40;

/**
 * Вопрос ухода о несохранённых строках там, где автосохранения нет, — `leaveQuestion` в `src/features/costs/invoiceLeave.ts`;
 * меняются вместе (сверяет `invoiceLeave.test.ts`). У черновика вопроса «сохранить ли» нет: уход дописывает (12, D-12.7).
 */
const LINES_UNSAVED = 'Не сохранены строки счёта. Сохранить их перед переходом?';

/** Рамка элемента — или отказ с названной причиной. */
async function box(locator, what) {
  const found = await locator.boundingBox();
  if (!found) throw new Error(`${what} не видно`);
  return found;
}

/** Черновик «со всеми сообщениями»; возвращает его адрес и номер. */
async function allMessages(page, h) {
  const number = `СЧ-Г${h.stamp}`;
  // Двойник: тот же поставщик, номер и дата.
  await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Двойник для раскладки') });
  const created = await h.api('POST', '/costs/invoices', {
    requisites: h.requisites(number, 'Раскладка экрана счёта'), unconfirmed: ['Номер', 'Итого', 'Срок'],
  });
  await h.attachScan(created.id, Buffer.from(`%PDF-1.4 не настоящий ${h.stamp}`).toString('base64'), 'скан-счёта.pdf', 'application/pdf');
  // Отказ запуска — тоже исход: файл сервер не читает, и полоса формы назовёт это сама.
  await h.api('POST', `/costs/invoices/${created.id}/recognition`).catch(() => {});
  // Распознавание отказывает о самом файле сразу (#1344) — ждём исхода, а не время.
  for (let i = 0; i < 40; i++) {
    const state = (await h.api('GET', `/costs/invoices/${created.id}/recognition`)).state;
    if (state === 'failed' || state === 'done') break;
    await page.waitForTimeout(500);
  }
  const lines = Array.from({ length: LINES }, (_, i) => ({ supplierText: `Строка раскладки ${i + 1}`, quantity: '1', price: '100' }));
  await h.api('PUT', `/costs/invoices/${created.id}/lines`, { lines });
  return { id: created.id, number };
}

/** Выбор подпанели — настройка на сервере; прогон ставит «открыта» и возвращает прежний. */
async function railPreference(h, value) {
  const before = (await h.api('GET', '/account/settings')) ?? {};
  await h.api('PUT', '/account/settings', { rail: value });
  return before.rail ?? 'auto';
}

export async function invoiceScreenLayoutChecks(page, check, h) {
  let invoice;
  await check('экран счёта: черновик «со всеми сообщениями» заведён', async () => { invoice = await allMessages(page, h); });
  if (!invoice) return;

  const before = await railPreference(h, 'expanded');
  try {
    for (const [width, height] of LAPTOPS) {
      await check(`экран счёта ${width}×${height}: сводка не выше 64, до последней строки и сверки доходят колесом и Tab`, async () => {
        await page.setViewportSize({ width, height });
        await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
        const summary = page.getByTestId('invoice-header');
        await h.named('сводки счёта нет', () => summary.waitFor({ timeout: 10_000 }));
        // Сообщения — в индикаторе состояния (11): ждём banner сбоя распознавания, худшего из них.
        await page.getByTestId('invoice-status-banner').waitFor({ timeout: 15_000 });

        // Подпанель раздела у пользователя открыта (D10), но при открытом счёте её нет (D-10.5): место
        // отдано форме и файлу, а выбор человека не тронут — он вернётся с закрытием счёта (проверка ниже).
        if (await page.getByTestId('rail').getAttribute('data-panel'))
          throw new Error('подпанель раздела при открытом счёте не скрыта');

        // Список и файл — по месту (D-10.3): файл рядом заданной ширины или (1024) нет его; списка нет.
        const aside = page.getByRole('complementary', { name: 'Файл счёта' });
        const beside = FILE_BESIDE[width];
        if (beside === null) {
          if (await aside.count()) throw new Error('на 1024 файл встал рядом — форме осталось бы меньше 720');
        } else {
          const panel = await box(aside, `панели файла рядом (${width})`);
          if (Math.abs(panel.width - beside) > 1) throw new Error(`панель файла ${Math.round(panel.width)} px, ждали ${beside}`);
          if (width - 80 - panel.width < 720) throw new Error(`форме рядом с файлом осталось ${Math.round(width - 80 - panel.width)} px`);
        }
        if (await page.getByTestId('ld-nav').isVisible()) throw new Error('список стоит рядом с формой, хотя места ему нет');
        await h.named('«‹ N из M ›» в сводке нет', () => summary.getByRole('group', { name: 'Счета отбора' }).waitFor({ timeout: 5_000 }));

        const head = await box(summary, 'сводки');
        if (head.height > 64.5) throw new Error(`неподвижная часть ${Math.round(head.height)} px — больше 64`);

        // Перенесённые из 3.4 (PR 1): пять полей первого этапа — в окне на 1366×650, таблица строк без
        // прокрутки вбок на 1366×768.
        if (width === 1366 && height === 650)
          for (const name of FIRST_STAGE) {
            // У поля с меткой «со скана» подпись с припиской для диктора ([UI-88], 11 D-11.4): она в `<label>` отдельным
            // блоком `sr-only`, и в тексте подписи пробела перед ней нет — имя то же, хвост свой.
            const label = new RegExp(`^${name}\\s?(\\(со скана, не проверено\\))?$`);
            const field = await box(page.getByLabel(label).first(), `поля «${name}»`);
            if (field.y < head.y + head.height - 1 || field.y + field.height > height + 1)
              throw new Error(`поле «${name}» первого этапа вне окна на 1366×650`);
          }
        if (width === 1366 && height === 768) {
          await expandInvoiceParts(page);
          const sideways = await page.getByRole('table', { name: 'Строки счёта' }).evaluate(table => {
            for (let el = table; el; el = el.parentElement)
              if (getComputedStyle(el).overflowX !== 'visible' && el.scrollWidth > el.clientWidth + 1) return el.scrollWidth - el.clientWidth;
            return 0;
          });
          if (sideways > 0) throw new Error(`таблица строк прокручивается вбок на ${sideways} px на 1366×768`);
        }

        // Страница вбок не прокручивается — прокручивается только таблица, если ей мало места.
        const sideways = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        if (sideways > 1) throw new Error(`страница прокручивается вбок на ${sideways} px`);

        // Одна прокрутка: всё под сводкой — в ней, и ей есть что прокручивать.
        const scroll = page.getByTestId('invoice-scroll');
        if (!(await scroll.evaluate(el => el.scrollHeight > el.clientHeight)))
          throw new Error('у формы нет прокрутки — части стоят в неподвижной части?');

        // Колесо доходит до конца формы.
        const area = await box(scroll, 'области прокрутки');
        await page.mouse.move(area.x + area.width / 2, area.y + area.height / 2);
        for (let i = 0; i < 60 && !(await scroll.evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 2)); i++)
          await page.mouse.wheel(0, 600);
        if (!(await scroll.evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 2)))
          throw new Error('колесо не довело до конца формы');
        const last = page.getByRole('table', { name: 'Строки счёта' }).locator('tbody tr').nth(LINES - 1);
        await last.scrollIntoViewIfNeeded();
        const row = await box(last, 'последней строки');
        if (row.y < head.y + head.height - 1 || row.y + row.height > height + 1)
          throw new Error('последняя строка не встаёт в окно под сводкой');

        // Сверка — липкой строкой у нижнего края, пока таблица в окне (COST-6.2): середина таблицы в окне.
        await page.getByRole('table', { name: 'Строки счёта' }).locator('tbody tr').nth(LINES / 2).evaluate(el => el.scrollIntoView({ block: 'center' }));
        const strip = await box(page.getByTestId('invoice-reconciliation'), 'сверки');
        if (strip.y + strip.height > height + 1 || strip.y + strip.height < height - 120)
          throw new Error(`сверка не у нижнего края при таблице в середине: низ ${Math.round(strip.y + strip.height)} из ${height}`);

        // «Разобран» — в сводке, в окне всегда и ничем не накрыта: в тесной колонке кнопка уходила под
        // соседнюю панель файла, оставаясь «в окне» по рамке.
        const button = summary.getByRole('button', { name: 'Разобран', exact: true });
        const parsed = await box(button, '«Разобран» в сводке');
        if (parsed.x + parsed.width > width + 1) throw new Error('«Разобран» за правым краем окна');
        // Неактивная кнопка-значок — `pointer-events: none`, и точка «проваливается» сквозь неё к родителю:
        // на время замера кнопка ловит точку, чтобы проверка видела перекрытие, а не своё отключение.
        if (!(await button.evaluate(el => {
          const was = el.style.pointerEvents;
          el.style.pointerEvents = 'auto';
          try {
            const r = el.getBoundingClientRect();
            return el.contains(document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2));
          } finally { el.style.pointerEvents = was; }
        }))) throw new Error('«Разобран» накрыта соседним элементом');

        // Tab: поле ниже края окна прокручивается в окно и не прячется под сводкой.
        // Начинаем за пять строк до конца, не прокручивая к ним: путь Tab до них — те же поля строк.
        await scroll.evaluate(el => { el.scrollTop = 0; });
        await page.getByLabel(`Наименование в счёте, строка ${LINES - 5}`, { exact: true }).evaluate(el => el.focus({ preventScroll: true }));
        await scroll.evaluate(el => { el.scrollTop = 0; });
        const labelOf = () => page.evaluate(() => {
          const el = document.activeElement;
          return el?.getAttribute('aria-label') ?? el?.labels?.[0]?.textContent ?? '';
        });
        for (let i = 0; i < 120; i++) {
          const label = await labelOf();
          if (label.endsWith(`строка ${LINES}`)) break;
          await page.keyboard.press('Tab');
        }
        const focused = await page.evaluate(() => {
          const el = document.activeElement;
          const r = el?.getBoundingClientRect();
          return el && r ? { label: el.getAttribute('aria-label') ?? el.labels?.[0]?.textContent ?? '', top: r.top, bottom: r.bottom } : null;
        });
        if (!focused?.label?.endsWith(`строка ${LINES}`)) throw new Error(`Tab не дошёл до строки ${LINES}: фокус на «${focused?.label}»`);
        if (focused.top < head.y + head.height - 1 || focused.bottom > height + 1)
          throw new Error(`поле под фокусом вне окна или под сводкой: ${Math.round(focused.top)}–${Math.round(focused.bottom)}`);
      });
    }
    await placeChecks(page, check, h, invoice);
    await invoiceStatusChecks(page, check, h, invoice);
    await invoiceScanMarkChecks(page, check, h, invoice);
  } finally {
    await page.setViewportSize({ width: 1500, height: 1000 });
    await page.keyboard.press('Escape').catch(() => {});
    // «Файл» пишет выбор на сервер — возвращаем «открыт», как без выбора.
    await h.api('PUT', '/account/settings', { invoiceFile: 'open' }).catch(e => console.log('  ! выбор «Файл» не возвращён:', e.message));
    await railPreference(h, before).catch(e => console.log('  ! выбор подпанели не возвращён:', e.message));
  }

  // Смена счёта сначала ДОПИСЫВАЕТ (costs-invoice-screen-12-autosave, D-12.7, D10.4 зонтика): у 10 щелчок по
  // соседнему счёту спрашивал, а до 10 — молча терял набранное. Соседний — двойник по ссылке оговорки: она идёт тем
  // же `switchTo`, что и строка списка. Отказ записи оставляет на счёте — причину называет носитель, вопроса нет.
  await check('экран счёта: переход к другому счёту дописывает строку и открывает другой; отказ записи оставляет на счёте', async () => {
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('сводки счёта нет', () => page.getByTestId('invoice-header').waitFor({ timeout: 10_000 }));
    const name = page.getByLabel('Наименование в счёте, строка 1', { exact: true });
    const lines = `**/api/costs/invoices/${invoice.id}/lines`;

    // Отказ сервера: человек остаётся, носитель называет причину словами сервера, вопроса нет.
    await page.route(lines, route => (route.request().method() === 'PUT'
      ? route.fulfill({ status: 400, contentType: 'application/json', body: '{"error":"Строки не приняты (проба)."}' })
      : route.continue()));
    try {
      await name.fill('Правка, которой откажут');
      await statusOpen(page, 'duplicate');
      await h.named('носитель не назвал отказ', () => untilSaveState(page, 'refused'));
      if (new URLSearchParams(new URL(page.url()).search).get('invoice') !== invoice.id)
        throw new Error('запись строк отказала, а счёт сменился — набранное ушло вместе с ним');
      if (await page.getByRole('dialog').count()) throw new Error('при отказе записи смена счёта задала вопрос — должна была остаться молча');
      const said = await page.getByTestId('invoice-save-state').first().innerText();
      if (!said.includes('Строки не приняты (проба).')) throw new Error(`носитель не назвал причину словами сервера: «${said}»`);
      if ((await name.inputValue()) !== 'Правка, которой откажут') throw new Error('после отказа набранное пропало');
    } finally {
      await page.unroute(lines);
    }

    // Запись проходит: сначала строка записана, затем открыт другой счёт.
    await name.fill('Дописано при смене счёта');
    const written = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith(`/costs/invoices/${invoice.id}/lines`), { timeout: 15_000 });
    await statusOpen(page, 'duplicate');
    if (!(await written).ok()) throw new Error('запись строк при смене счёта отказала');
    await h.named('другой счёт не открылся после записи', () => page.waitForURL(url => new URL(url).searchParams.get('invoice') !== invoice.id, { timeout: 10_000 }));
    if (await page.getByRole('dialog').count()) throw new Error('смена счёта с записанной строкой спросила о несохранённом');
    const stored = (await h.api('GET', `/costs/invoices/${invoice.id}`)).lines[0].supplierText;
    if (stored !== 'Дописано при смене счёта') throw new Error(`на сервере строка 1 — «${stored}», набранное при смене счёта не записано`);
  });

  // Уход из счёта (#1653, [COST-7]; 12, D-12.7): пункт «Счета» сначала дописывает и уходит; вопрос — только когда
  // запись не дошла («Запись не дошла — уйти без неё?»), и тогда же перезагрузка — вопросом браузера. Вводом с
  // клавиатуры, а не `fill`: вопрос браузера Chromium задаёт только после действия человека на странице.
  await check('экран счёта: уход дописывает строку; без связи — вопрос «Запись не дошла», «Остаться» и вопрос браузера', async () => {
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('сводки счёта нет', () => page.getByTestId('invoice-header').waitFor({ timeout: 10_000 }));
    await expandInvoiceParts(page);
    const name = page.getByLabel('Наименование в счёте, строка 3', { exact: true });
    const lines = `**/api/costs/invoices/${invoice.id}/lines`;
    const toInvoices = async () => {
      await page.getByRole('button', { name: 'Показать панель раздела' }).click();
      await page.getByTestId('section-panel-drawer').getByRole('link', { name: 'Счета', exact: true }).click();
    };
    const question = page.getByRole('alertdialog', { name: 'Запись не дошла — уйти без неё?' })
      .or(page.getByRole('dialog', { name: 'Запись не дошла — уйти без неё?' }));
    const was = await name.inputValue();
    const typed = `${was} — правка`;
    try {
      // Связи нет: запись обрывается, уход спрашивает, «Остаться» оставляет набранное.
      await page.route(lines, route => (route.request().method() === 'PUT' ? route.abort('internetdisconnected') : route.continue()));
      await name.click();
      await page.keyboard.press('End');
      await page.keyboard.type(' — правка');
      await toInvoices();
      await h.named('уход без связи не спросил «Запись не дошла — уйти без неё?»', () => question.waitFor({ timeout: 10_000 }));
      await question.getByRole('button', { name: 'Остаться', exact: true }).click();
      await question.waitFor({ state: 'detached', timeout: 5_000 });
      await page.keyboard.press('Escape');
      if (new URLSearchParams(new URL(page.url()).search).get('invoice') !== invoice.id) throw new Error('после «Остаться» счёт закрыт');
      if ((await name.inputValue()) !== typed) throw new Error(`после «Остаться» набранное пропало: «${await name.inputValue()}»`);
      if ((await saveState(page)) !== 'failed') throw new Error(`носитель после обрыва говорит «${await saveState(page)}», а не «не сохранено — повторить»`);

      // Очередь не пуста — перезагрузка спрашивает браузер. Ждём САМ вопрос, а не перезагрузку: отклонённый вопрос
      // её отменяет, и обещание `reload` не кончается вовсе.
      const asked = page.waitForEvent('dialog', { timeout: 5_000 });
      page.reload().catch(() => {});
      const unload = await asked.catch(() => { throw new Error('перезагрузка с недошедшей записью прошла без вопроса браузера'); });
      if (unload.type() !== 'beforeunload') throw new Error(`вместо вопроса об уходе — «${unload.type()}»`);
      await unload.dismiss();
      if ((await name.inputValue()) !== typed) throw new Error('перезагрузка, отменённая в вопросе, унесла набранное');

      // Связь вернулась: «Повторить» в сводке дописывает, уход идёт без вопроса.
      await page.unroute(lines);
      await page.getByTestId('invoice-header').getByRole('button', { name: 'Повторить' }).first().click();
      await h.named('«Повторить» не дописал строку', () => untilSaveState(page, 'saved'));
      await toInvoices();
      await h.named('уход после записи не открыл «Счета»', () => page.waitForURL(url => !new URL(url).searchParams.get('invoice'), { timeout: 10_000 }));
      if (await question.count()) throw new Error('уход с записанной строкой спросил');
      const stored = (await h.api('GET', `/costs/invoices/${invoice.id}`)).lines[2].supplierText;
      if (stored !== typed) throw new Error(`на сервере строка 3 — «${stored}», а набрано «${typed}»`);
    } finally {
      await page.unroute(lines).catch(() => {});
      // Брошенная недошедшая запись держала бы `beforeunload`, и `goto` соседних проверок висел бы до таймаута.
      const leave = d => { if (d.type() === 'beforeunload') void d.accept(); };
      page.on('dialog', leave);
      await page.goto(`${h.BASE}/invoices`, { waitUntil: 'networkidle' })
        .catch(e => console.log('  ! правка строки не брошена:', e.message.split('\n')[0]))
        .finally(() => page.off('dialog', leave));
    }
  });

  // Свёрнутая часть тело прячет, а не размонтирует (ревью PR #1787): черновик строк держит таблица, и свёртка
  // «Строк» молча выбрасывала набранное. «Новый счёт» сначала дописывает текущий (12, D-12.7): охрана ухода та же.
  await check('экран счёта: свёртка «Строк» правку строки не теряет, «Новый счёт» сначала дописывает её', async () => {
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('сводки счёта нет', () => page.getByTestId('invoice-header').waitFor({ timeout: 10_000 }));
    const name = page.getByLabel('Наименование в счёте, строка 2', { exact: true });
    await name.fill('Правка до свёртки');
    await page.getByRole('button', { name: 'Свернуть «Строки»' }).click();
    if (await name.isVisible()) throw new Error('свёрнутая часть «Строки» тело показывает');
    await page.getByRole('button', { name: 'Развернуть «Строки»' }).click();
    if ((await name.inputValue()) !== 'Правка до свёртки')
      throw new Error(`свёртка «Строк» выбросила набранное: «${await name.inputValue()}»`);

    // Списка рядом нет (файл рядом) — «Новый счёт» в «⋮» сводки (D-10.8).
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Ещё' }).click();
    await page.getByRole('menuitem', { name: 'Новый счёт' }).click();
    await h.named('«Новый счёт» не открыл новый черновик', () => page.waitForURL(url => new URL(url).searchParams.get('invoice') !== invoice.id, { timeout: 10_000 }));
    const stored = (await h.api('GET', `/costs/invoices/${invoice.id}`)).lines[1].supplierText;
    if (stored !== 'Правка до свёртки') throw new Error(`«Новый счёт» не дописал строку прежнего: на сервере «${stored}»`);
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('invoice-header').waitFor({ timeout: 10_000 });
  });

  // Примечание вводится чипом над таблицей (ревью PR #1786): колонка «только когда примечания есть» не
  // давала его ввести вовсе, а стёртое последнее убирало колонку у человека под курсором.
  await check('экран счёта: «Примечание» включается чипом и не пропадает, когда стёрто последнее', async () => {
    const note = page.getByLabel('Примечание, строка 1', { exact: true });
    if (await note.count()) throw new Error('колонка «Примечание» видна без примечаний и без чипа');
    await page.getByRole('button', { name: 'Примечание', exact: true }).click();
    await note.fill('с доставкой');
    await note.fill('');
    if (!(await note.isVisible())) throw new Error('стёртое последнее примечание убрало колонку');
    if (!(await note.evaluate(el => el === document.activeElement))) throw new Error('стёртое примечание увело фокус из поля');
    // Уходим, не сохраняя.
    await page.goto(`${h.BASE}/invoices`, { waitUntil: 'networkidle' }).catch(() => {});
    const dialog = page.getByRole('dialog', { name: 'Несохранённые изменения' });
    if (await dialog.isVisible().catch(() => false)) await dialog.getByRole('button', { name: 'Не сохранять', exact: true }).click();
  });
}

/**
 * Место по контейнеру (D-10.3 – D-10.5, D-10.8; ТЗ COST-6.3): «Файл» на 1366 открывает список, на 1024 —
 * слой поверх с набранным под ним, «›» на 1280 ведёт к соседнему счёту отбора, справочники — в «⋮» сводки,
 * а подпанель раздела возвращается с закрытием счёта и открывается поверх, не меняя раскладки.
 */
async function placeChecks(page, check, h, invoice) {
  const go = async (width, height) => {
    await page.setViewportSize({ width, height });
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('сводки счёта нет', () => page.getByTestId('invoice-header').waitFor({ timeout: 10_000 }));
  };
  const file = () => page.getByTestId('invoice-header').getByRole('button', { name: 'Файл', exact: true });
  const aside = () => page.getByRole('complementary', { name: 'Файл счёта' });

  await check('экран счёта 1366: «Файл» закрывает панель — встаёт список; открывает — список уходит', async () => {
    await go(1366, 768);
    await aside().waitFor({ timeout: 5_000 });
    await file().click();
    await aside().waitFor({ state: 'detached', timeout: 5_000 });
    await h.named('список не встал слева при закрытом файле', () => page.getByTestId('ld-nav').waitFor({ state: 'visible', timeout: 5_000 }));
    const form = await box(page.getByTestId('invoice-header'), 'сводки');
    if (form.width < 940) throw new Error(`форме рядом со списком ${Math.round(form.width)} px — меньше 940`);
    await file().click();
    await aside().waitFor({ timeout: 5_000 });
    if (await page.getByTestId('ld-nav').isVisible()) throw new Error('файл открыт, а список остался');
  });

  await check('экран счёта 1024: «Файл» — поверх формы, причина в подсказке; Esc возвращает к набранному', async () => {
    await go(1024, 768);
    await expandInvoiceParts(page);
    const name = page.getByLabel('Наименование в счёте, строка 1', { exact: true });
    await name.fill('Набрано до файла');
    await file().hover();
    await h.named('подсказка «Файл» не назвала, почему поверх', () => page.getByRole('tooltip')
      .filter({ hasText: /поверх формы: рядом форме нужно не меньше 720/ }).first().waitFor({ timeout: 5_000 }));
    await file().click();
    // Файл стенда — «не читается как PDF»: слой встаёт и называет это сам; поверх — то, чем показан файл.
    const layer = page.getByRole('dialog');
    await h.named('файл не открылся поверх формы', () => layer.first().waitFor({ timeout: 10_000 }));
    if (await aside().count()) throw new Error('на 1024 файл встал рядом');
    await page.keyboard.press('Escape');
    await layer.first().waitFor({ state: 'detached', timeout: 5_000 });
    if ((await name.inputValue()) !== 'Набрано до файла') throw new Error(`после Esc набранное пропало: «${await name.inputValue()}»`);
    await name.fill('Строка раскладки 1');
  });

  await check('экран счёта 1280: списка нет, «›» дописывает строку и ведёт к соседнему счёту отбора, «‹» — обратно', async () => {
    await go(1280, 720);
    const steps = page.getByTestId('invoice-header').getByRole('group', { name: 'Счета отбора' });
    await steps.waitFor({ timeout: 5_000 });
    if (await page.getByTestId('ld-nav').isVisible()) throw new Error('на 1280 список стоит рядом с формой');
    const next = steps.getByRole('button', { name: 'Следующий счёт' });
    const prev = steps.getByRole('button', { name: 'Предыдущий счёт' });
    const [there, back] = (await next.isEnabled()) ? [next, prev] : [prev, next];
    if (!(await there.isEnabled())) throw new Error('обе стрелки неактивны — в отборе один счёт?');
    const here = (await steps.innerText()).match(/\d+ из \d+/)?.[0];
    if (!here) throw new Error(`в стрелках нет «N из M»: «${await steps.innerText()}»`);
    // Набранное перед стрелкой не теряется (12, D-12.7): «›» сначала дописывает строку, затем ведёт.
    const typed = `Набрано перед стрелкой ${h.stamp}`;
    await page.getByLabel('Наименование в счёте, строка 4', { exact: true }).fill(typed);
    await there.click();
    await page.waitForFunction(id => new URLSearchParams(location.search).get('invoice') !== id, invoice.id, { timeout: 5_000 })
      .catch(() => { throw new Error('стрелка не открыла соседний счёт'); });
    // Адрес меняется раньше сводки: пока сосед читается, стоит прежний счёт со своим «N из M», и «‹» по нему
    // честно ведёт к соседу прежнего. Ждём сводку соседа — по смене места в отборе.
    await h.named('сводка соседа не встала', () => steps.filter({ hasNotText: new RegExp(`(^|\\D)${here}(\\D|$)`) }).waitFor({ timeout: 10_000 }));
    if ((await h.api('GET', `/costs/invoices/${invoice.id}`)).lines[3].supplierText !== typed)
      throw new Error('«›» увела к соседу, не дописав строку');
    await back.click();
    await page.waitForFunction(id => new URLSearchParams(location.search).get('invoice') === id, invoice.id, { timeout: 5_000 })
      .catch(() => { throw new Error('обратная стрелка не вернула к счёту'); });
  });

  await check('экран счёта 1280: шапки страницы нет, «Условия поставщиков» — из «⋮» сводки', async () => {
    await go(1280, 720);
    if (await page.getByRole('heading', { level: 1, name: 'Счета на оплату' }).isVisible())
      throw new Error('при открытом счёте шапка страницы осталась');
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Ещё' }).click();
    await page.getByRole('menuitem', { name: 'Условия поставщиков' }).click();
    const dialog = page.getByRole('dialog');
    await h.named('«Условия поставщиков» не открылись', () => dialog.first().waitFor({ timeout: 5_000 }));
    await page.keyboard.press('Escape');
    await dialog.first().waitFor({ state: 'detached', timeout: 5_000 });
  });

  await check('экран счёта: «Показать панель» — поверх, раскладка и выбор не меняются; «N из M» возвращает подпанель', async () => {
    await go(1366, 768);
    const rail = page.getByTestId('rail');
    const formBefore = await box(page.getByTestId('invoice-header'), 'сводки');
    await page.getByRole('button', { name: 'Показать панель раздела' }).click();
    const drawer = page.getByTestId('section-panel-drawer');
    await h.named('подпанель не открылась поверх', () => drawer.waitFor({ timeout: 5_000 }));
    if (await rail.getAttribute('data-panel')) throw new Error('подпанель встала в раскладку, а не поверх');
    const formUnder = await box(page.getByTestId('invoice-header'), 'сводки');
    if (Math.abs(formUnder.x - formBefore.x) > 1 || Math.abs(formUnder.width - formBefore.width) > 1)
      throw new Error('подпанель поверх сдвинула форму');
    if ((await h.api('GET', '/account/settings'))?.rail !== 'expanded') throw new Error('подпанель поверх переписала выбор человека');
    await page.keyboard.press('Escape');
    await drawer.waitFor({ state: 'detached', timeout: 5_000 });
    // «N из M» закрывает счёт и показывает список; подпанель возвращается такой, какой была.
    await page.getByTestId('invoice-header').getByRole('group', { name: 'Счета отбора' })
      .getByRole('button', { name: /из \d+/ }).click();
    await page.waitForFunction(() => !new URLSearchParams(location.search).get('invoice'), null, { timeout: 5_000 })
      .catch(() => { throw new Error('«N из M» не закрыл счёт'); });
    await page.getByTestId('section-panel').waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('после закрытия счёта подпанель не вернулась'); });
  });
}
