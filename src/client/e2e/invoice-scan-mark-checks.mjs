// Метка «со скана, не проверено» в поле (costs-invoice-screen-11, задача 4.1, D-11.4, ТЗ COST-6.4) — живым
// браузером на счёте «со всеми сообщениями» из `invoice-screen-layout-checks`: у него есть файл и метки на
// «Номере» и «Сумме к оплате». Ломается: вернуть рамку и строку подсказки — поле с меткой выше соседнего;
// убрать кнопку хвоста — метки не видно вовсе.
//
// Отдельным файлом: `invoice-screen-layout-checks` и `invoices-smoke` и так велики. Едет внутри первого.

export async function invoiceScanMarkChecks(page, check, h, invoice) {
  await check('метка «со скана» — кнопкой в хвосте поля: высота поля та же, файл открывается', async () => {
    await page.setViewportSize({ width: 1366, height: 768 });
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    const marked = page.locator('[data-readiness="field:Номер"]');
    const plain = page.locator('[data-readiness="field:ВТомЧислеНДС"]');
    await h.named('поля «Номер» нет', () => marked.waitFor({ timeout: 10_000 }));

    const mark = marked.getByRole('button', { name: 'Показать на скане' });
    if ((await mark.count()) !== 1) throw new Error('у «Номера» с меткой нет кнопки «Показать на скане»');
    if ((await plain.getByRole('button', { name: 'Показать на скане' }).count()) !== 0)
      throw new Error('кнопка «Показать на скане» у поля без метки');

    const [a, b] = await Promise.all([marked.boundingBox(), plain.boundingBox()]);
    if (Math.abs(a.height - b.height) > 0.5)
      throw new Error(`поле с меткой ${Math.round(a.height)} px, без метки ${Math.round(b.height)} — метка добавила высоту`);
    if ((await marked.getByText('Распознано, не подтверждено', { exact: true }).count()) !== 0)
      throw new Error('у поля с меткой осталась строка подсказки');

    const name = await marked.getByRole('textbox').first().getAttribute('aria-label');
    if (name !== 'Номер (со скана, не проверено)') throw new Error(`подпись для диктора: «${name}»`);

    // Черновик с метками открывает файл сам; закрываем «Файлом» — и «Показать на скане» открывает его снова
    // рядом с формой (место на 1366 есть), а не переключает: второй щелчок его не закрывает.
    const pane = page.getByRole('complementary', { name: 'Файл счёта' });
    await h.named('файла рядом с формой нет', () => pane.waitFor({ timeout: 10_000 }));
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Файл', exact: true }).click();
    await pane.waitFor({ state: 'detached', timeout: 5_000 });
    await mark.click();
    await h.named('«Показать на скане» не открыл файл рядом с формой', () => pane.waitFor({ timeout: 10_000 }));
    // Открытый рядом — фокус уходит в его панель: щелчок отработан, и файл после него на месте.
    await mark.click();
    await page.waitForFunction(() => !!document.activeElement?.closest('aside[aria-label="Файл счёта"]'), null, { timeout: 5_000 })
      .catch(() => { throw new Error('«Показать на скане» при открытом файле не перевёл фокус в его панель'); });
    if ((await pane.count()) !== 1) throw new Error('второй щелчок «Показать на скане» закрыл файл');
  });
}
