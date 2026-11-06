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

/** Размеры из D10 зонтика: ноутбук, ноутбук с панелями браузера, браузер не на весь экран, 125 % на 1280. */
export const LAPTOPS = [[1366, 768], [1280, 720], [1366, 650], [1024, 768]];

const LINES = 40;

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
        await page.getByText('Похоже на дубликат').first().waitFor({ timeout: 10_000 });

        // Подпанель раздела у пользователя открыта (D10): проверка идёт при ней, а не на пустой оболочке.
        if (width >= 1200 && !(await page.getByTestId('rail').getAttribute('data-panel')))
          throw new Error('подпанель раздела не открыта — проверка шла бы не в том окружении');

        const head = await box(summary, 'сводки');
        if (head.height > 64.5) throw new Error(`неподвижная часть ${Math.round(head.height)} px — больше 64`);

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
  } finally {
    await page.setViewportSize({ width: 1500, height: 1000 });
    await railPreference(h, before).catch(e => console.log('  ! выбор подпанели не возвращён:', e.message));
  }

  // Смена счёта идёт через охрану ухода (D-10.7, задача 2.3): раньше щелчок по соседнему счёту
  // перемонтировал форму, и набранное в строке пропадало без вопроса. Соседний — двойник по ссылке
  // оговорки: она идёт тем же `switchTo`, что и строка списка.
  await check('экран счёта: переход к другому счёту при несохранённой строке спрашивает, «Отмена» оставляет набранное', async () => {
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('сводки счёта нет', () => page.getByTestId('invoice-header').waitFor({ timeout: 10_000 }));
    const name = page.getByLabel('Наименование в счёте, строка 1', { exact: true });
    await name.fill('Несохранённая правка');
    await page.locator('button', { hasText: new RegExp(`${invoice.number} от`) }).first().click();
    const dialog = page.getByRole('dialog', { name: 'Несохранённые изменения' });
    await h.named('переход к другому счёту не спросил о несохранённом', () => dialog.waitFor({ timeout: 5_000 }));
    await dialog.getByRole('button', { name: 'Отмена', exact: true }).click();
    await dialog.waitFor({ state: 'detached', timeout: 5_000 });
    if (new URLSearchParams(new URL(page.url()).search).get('invoice') !== invoice.id)
      throw new Error('после «Отмена» открыт другой счёт');
    if ((await name.inputValue()) !== 'Несохранённая правка')
      throw new Error(`после «Отмена» набранное пропало: «${await name.inputValue()}»`);
    // Уходим, не сохраняя: соседним проверкам правка не нужна.
    await page.locator('button', { hasText: new RegExp(`${invoice.number} от`) }).first().click();
    await dialog.getByRole('button', { name: 'Не сохранять', exact: true }).click();
  });
}
