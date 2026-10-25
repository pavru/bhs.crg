// Предпросмотр документа после правки реквизита (issue #1493) — живым браузером, настоящим pdf.js.
//
// Отдельным файлом, а не строками в `fields-smoke`: тот у предела размера. Зовётся из него на
// документе «Кабельный журнал (из PDF)», у типа которого посев заводит шаблон на четыре листа
// (`seed-preview.mjs`).
//
// Сверяется то, чего не видит тест компонента с подменённым движком: новый вариант документа, собранный
// сервером, встаёт на том же листе, а лента на время подмены не пустеет ни на кадр.

import { PREVIEW_PAGES } from './seed-preview.mjs';
import { sheetDrawn } from './pdf-viewer-checks.mjs';

/** Номер, который проверка вписывает в реквизит; шаблон печатает его на каждом листе. */
const NEW_NUMBER = 'КЖ-777';

/** Текст слоя листа без пробелов: pdf.js режет строку на куски, и пробелы между ними — его. */
const layerText = sheet => sheet.locator('.bhs-text-layer').innerText().then(t => t.replace(/\s+/g, ''));

export async function previewKeepsPage(page, editor, named) {
  // Панель по умолчанию свёрнута; ширина окна прогона — выше `xl`, иначе её нет вовсе.
  await page.getByRole('button', { name: 'Показать предпросмотр' }).click();
  const panel = page.getByTestId('document-preview');
  const region = panel.getByRole('region', { name: 'Предпросмотр документа' });
  await named('просмотрщика в панели предпросмотра нет', () => region.waitFor({ timeout: 30_000 }));
  if (await panel.locator('iframe').count()) throw new Error('в панели остался фрейм браузера');
  const toolbar = panel.getByTestId('viewer-toolbar');
  await named(`панель не говорит «из ${PREVIEW_PAGES}» — шаблона посева нет?`,
    () => toolbar.getByText(`из ${PREVIEW_PAGES}`).waitFor({ timeout: 30_000 }));

  const field = toolbar.getByRole('textbox', { name: 'Номер страницы' });
  await field.fill('2');
  await field.press('Enter');
  const second = region.getByRole('group', { name: `Страница 2 из ${PREVIEW_PAGES}` });
  await sheetDrawn(page, named, second);

  // С этого мига и до конца — наименьшее число холстов в ленте.
  await region.evaluate(el => {
    const count = () => el.querySelectorAll('canvas').length;
    window.__previewMinCanvas = count();
    new MutationObserver(() => { window.__previewMinCanvas = Math.min(window.__previewMinCanvas, count()); })
      .observe(el, { childList: true, subtree: true, attributes: true });
  });

  const number = editor.getByLabel('Номер', { exact: true });
  if ((await number.count()) !== 1) throw new Error(`поле «Номер» не найдено однозначно (${await number.count()})`);
  await number.fill(NEW_NUMBER);

  // Новый вариант пришёл, когда его номер напечатан на втором листе. Лист ищется заново на каждом
  // шаге, а не держится ручкой: ушедший и вставший заново лист ручка не увидела бы, и провал назвал
  // бы «не встал» вместо настоящей причины.
  const area = await region.elementHandle();
  await named(`второй лист не показал «${NEW_NUMBER}» — новый вариант не встал`, () => page.waitForFunction(
    ([el, text]) => (el.querySelector('[data-page="2"] .bhs-text-layer')?.textContent ?? '')
      .replace(/\s+/g, '').includes(text),
    [area, NEW_NUMBER], { timeout: 30_000 }));

  if (!(await layerText(second)).includes(NEW_NUMBER)) throw new Error('на втором листе прежний вариант');
  const shown = await field.inputValue();
  if (shown !== '2') throw new Error(`после перегенерации показана страница ${shown}, а не 2`);
  const min = await page.evaluate(() => window.__previewMinCanvas);
  if (!(min > 0)) throw new Error(`лента на время подмены опустела: холстов было ${min}`);
}
