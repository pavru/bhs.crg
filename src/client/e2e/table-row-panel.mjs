// Панель строки таблицы — standard side sheet ([UI-77], sharedui-md3-13 PR 2б). Своим модулем, а не строками
// в table-view-smoke.mjs: тот стоит у порога храповика размера. Вход и посев — того прогона.

/**
 * Панель показывает ВСЕ колонки строки, хотя таблица — в двух: назначение там есть, хотя колонкой оно не
 * показано. Строка — в адресе и переживает перезагрузку; «Закрыть строку» снимает её с адреса.
 * `open()` — таблица в двух колонках, `rows()` — дождаться её строк, `firstRow()` — первая строка с данными,
 * `value` — номер первого счёта, `purpose` — назначение посева.
 */
export async function rowPanelHoldsAllColumns(page, { open, rows, firstRow, value, purpose }) {
  await open();
  await rows('посеянные счета в двух колонках');
  await firstRow().click();
  const panel = page.getByRole('complementary', { name: 'Строка: счёт' });
  await panel.waitFor({ timeout: 5000 });
  await panel.getByText(purpose).waitFor({ timeout: 5000 });
  await panel.getByText(value, { exact: true }).waitFor();
  if (!page.url().includes('row=')) throw new Error('открытая строка в адрес не попала');
  await sitsBesideTable(page, panel);

  await page.reload({ waitUntil: 'networkidle' });
  await panel.getByText(value, { exact: true }).waitFor({ timeout: 8000 });
  await panel.getByRole('button', { name: 'Закрыть строку' }).click();
  await panel.waitFor({ state: 'detached', timeout: 5000 });
  if (page.url().includes('row=')) throw new Error('панель закрыта, а строка осталась в адресе');
}

/**
 * На столе панель стоит рядом с таблицей и сдвигает её: таблица под панель не заходит, затемнения нет. Меряется
 * область прокрутки таблицы, а не сама `<table>`: широкая таблица шире своей области и выходит за неё законно.
 */
async function sitsBesideTable(page, panel) {
  const [table, sheet] = await Promise.all([
    page.locator('table').first().evaluate(t => {
      let el = t;
      while (el.parentElement && getComputedStyle(el).overflowX === 'visible') el = el.parentElement;
      const { x, width } = el.getBoundingClientRect();
      return { x, width };
    }),
    panel.boundingBox(),
  ]);
  if (!sheet || table.x + table.width > sheet.x + 1)
    throw new Error(`таблица заходит под панель: таблица ${JSON.stringify(table)}, панель ${JSON.stringify(sheet)}`);
  if (await page.locator('[data-modal-state]').count() !== 0) throw new Error('панель строки открылась окном');
}

/**
 * На 375 px панель строки — лист во весь экран, окном (страница под ним не в Tab и не в читалке), и «Назад»
 * закрывает её своим слоем истории: строка уходит из адреса, таблица остаётся на месте. Без слоя «Назад» увёл
 * бы с таблицы вместе с панелью.
 * `url` — та же таблица адресом; прочее — как у `rowPanelHoldsAllColumns`.
 */
export async function rowPanelOnPhoneClosesWithBack(page, { url, rows, firstRow, value }) {
  await page.setViewportSize({ width: 375, height: 812 });
  try {
    await page.goto('about:blank');
    await page.goto(url, { waitUntil: 'networkidle' });
    await rows('посеянные счета на телефоне');
    await firstRow().click();
    const panel = page.getByRole('dialog', { name: 'Строка: счёт' });
    await panel.getByText(value, { exact: true }).waitFor({ timeout: 8000 });
    const box = await panel.boundingBox();
    if (!box || box.width < 370) throw new Error(`панель строки на телефоне не во весь экран: ${JSON.stringify(box)}`);

    await page.goBack();
    await panel.waitFor({ state: 'detached', timeout: 5000 });
    if (page.url().includes('row=')) throw new Error('«Назад» закрыл панель, а строка осталась в адресе');
    if (!page.url().startsWith(url.split('#')[0])) throw new Error(`«Назад» увёл с таблицы: ${page.url()}`);
    await rows('таблица после «Назад»');
  } finally {
    await page.setViewportSize({ width: 1500, height: 1000 });
  }
}
