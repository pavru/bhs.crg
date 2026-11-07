// Метка «со скана, не проверено» в поле (costs-invoice-screen-11, задачи 4.1 и 4.1a, D-11.4, ТЗ COST-6.4) — живым
// браузером на счёте «со всеми сообщениями» из `invoice-screen-layout-checks`: у него есть файл и метки на
// «Номере» и «Сумме к оплате». Ломается: вернуть рамку и строку подсказки — поле с меткой выше соседнего;
// снять `fromScan` — знака не видно вовсе; вернуть кнопку в хвост — в поле снова действие, которого у знака нет
// ([UI-88]); убрать «Показать на скане» у пункта перечня — сверять с файлом нечем.
//
// Отдельным файлом: `invoice-screen-layout-checks` и `invoices-smoke` и так велики. Едет внутри первого.
import { inStatusSheet } from './invoice-status.mjs';

export async function invoiceScanMarkChecks(page, check, h, invoice) {
  await check('метка «со скана» — знаком в хвосте поля: высота поля та же; файл показывает пункт перечня', async () => {
    await page.setViewportSize({ width: 1366, height: 768 });
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    const marked = page.locator('[data-readiness="field:Номер"]');
    const plain = page.locator('[data-readiness="field:ВТомЧислеНДС"]');
    await h.named('поля «Номер» нет', () => marked.waitFor({ timeout: 10_000 }));

    if ((await marked.locator('[data-field-scan-mark]').count()) !== 1) throw new Error('у «Номера» с меткой нет знака «со скана»');
    if ((await plain.locator('[data-field-scan-mark]').count()) !== 0) throw new Error('знак «со скана» у поля без метки');
    if ((await marked.getByRole('button').count()) !== 0) throw new Error('в поле с меткой есть кнопка: у знака действий нет');

    const [a, b] = await Promise.all([marked.boundingBox(), plain.boundingBox()]);
    if (Math.abs(a.height - b.height) > 0.5)
      throw new Error(`поле с меткой ${Math.round(a.height)} px, без метки ${Math.round(b.height)} — метка добавила высоту`);
    if ((await marked.getByText('Распознано, не подтверждено', { exact: true }).count()) !== 0)
      throw new Error('у поля с меткой осталась строка подсказки');

    // Приписка — в связанной подписи ([UI-88]), не в `aria-label`: сверяем имя поля, как его слышит диктор. Звёздочка
    // обязательного для «Разобран» — в той же подписи, после приписки.
    if ((await marked.getByRole('textbox', { name: /^Номер \(со скана, не проверено\)( \*)?$/ }).count()) !== 1) {
      throw new Error(`у «Номера» нет имени с припиской для диктора: ${await marked.ariaSnapshot()}`);
    }

    // Черновик с метками открывает файл сам; закрываем «Файлом» — и «Показать на скане» у пункта о метках
    // открывает его снова рядом с формой (место на 1366 есть), а не переключает: второй раз его не закрывает.
    const pane = page.getByRole('complementary', { name: 'Файл счёта' });
    await h.named('файла рядом с формой нет', () => pane.waitFor({ timeout: 10_000 }));
    await page.getByTestId('invoice-header').getByRole('button', { name: 'Файл', exact: true }).click();
    await pane.waitFor({ state: 'detached', timeout: 5_000 });
    const showScan = () => inStatusSheet(page, async sheet => {
      const item = sheet.getByTestId('status-item-marks');
      await h.named('в перечне нет пункта о метках', () => item.waitFor({ timeout: 5_000 }));
      // Справа у пункта — «Всё верно», а «Показать на скане» — сама строка, подсказкой строки.
      const hint = await item.getAttribute('aria-description');
      if (hint !== 'Показать на скане') throw new Error(`строка пункта о метках — не «Показать на скане»: «${hint}»`);
      await item.click();
    });
    await showScan();
    await h.named('«Показать на скане» не открыл файл рядом с формой', () => pane.waitFor({ timeout: 10_000 }));
    // Открытый рядом — фокус уходит в его панель (после снятия листа): действие отработано, и файл на месте.
    await showScan();
    await page.waitForFunction(() => !!document.activeElement?.closest('aside[aria-label="Файл счёта"]'), null, { timeout: 5_000 })
      .catch(() => { throw new Error('«Показать на скане» при открытом файле не перевёл фокус в его панель'); });
    if ((await pane.count()) !== 1) throw new Error('второе «Показать на скане» закрыло файл');
  });
}
