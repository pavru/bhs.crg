// Полный текст обрезанной клетки (change `sharedui-truncated-cell-tooltip`, [UI-52] [UI-53], #1519; п. 5а #1818) —
// живым браузером, задача 2.1: в матрице разноски обрезанное название строки показывает ОДНА подсказка — сетки;
// своего нативного `title` у названия больше нет. Короткое название подсказки не получает; Esc закрывает показ,
// а не окно матрицы.
//
// Отдельным файлом: `allocation-matrix-smoke` и так велик. Едет внутри него, с его страницей и помощниками.

const TIP = '[data-truncated-tip]';

export async function truncatedCellChecks({ page, check, invoice, openMatrix, stamp }) {
  await check('truncated-row-title-shows-one-tip', async () => {
    const number = `МТР-ДЛН-${stamp}`;
    const long = 'Кабель силовой ВВГнг(А)-LS 5×16 ок(N, PE)-1 ГОСТ 31996-2012, барабан 500 м, поставка частями';
    await invoice(number, [
      { supplierText: long, unit: 'м', quantity: 10, price: 100 },
      { supplierText: 'Короб', amount: 50 },
    ], 1050);
    const dialog = await openMatrix(page, number, 'на несколько объектов');
    const titles = dialog.locator('tbody th[scope="row"] .truncate');
    const longTitle = titles.first();
    await longTitle.waitFor();

    if (await longTitle.getAttribute('title') !== null) throw new Error('у названия строки остался свой нативный title');
    const cut = await longTitle.evaluate(el => el.scrollWidth > el.clientWidth);
    if (!cut) throw new Error('длинное название не обрезано — проверять показ не на чем');

    const box = await longTitle.boundingBox();
    await page.mouse.move(box.x + 20, box.y + box.height / 2);
    await page.locator(TIP).waitFor({ timeout: 3_000 })
      .catch(() => { throw new Error('у обрезанного названия полный текст не показан'); });
    const shown = await page.locator(TIP).allTextContents();
    if (shown.length !== 1) throw new Error(`подсказок ${shown.length}, а не одна`);
    // Название строки матрицы — с её номером («1. Кабель…»): сверяется с текстом самого названия, целиком.
    const full = (await longTitle.textContent()).trim();
    if (shown[0] !== full || !full.endsWith(long)) throw new Error(`показано «${shown[0]}», а не полное название «${full}»`);

    // Esc закрывает показ, а окно матрицы остаётся.
    await page.keyboard.press('Escape');
    await page.locator(TIP).waitFor({ state: 'detached', timeout: 2_000 });
    if (!(await dialog.isVisible())) throw new Error('Esc закрыл окно матрицы, а не показ');

    // Короткое название помещается — показа нет.
    const shortBox = await titles.nth(1).boundingBox();
    await page.mouse.move(shortBox.x + 5, shortBox.y + shortBox.height / 2);
    await page.waitForTimeout(800);
    if (await page.locator(TIP).count() !== 0) throw new Error('у необрезанного названия показан полный текст');

    await page.mouse.move(0, 0);
    await dialog.locator('[data-modal-exit]').first().click();
    await dialog.waitFor({ state: 'hidden', timeout: 5_000 });
  });
}
