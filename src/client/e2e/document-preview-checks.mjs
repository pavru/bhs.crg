// Предпросмотр документа после правки реквизита (issue #1493) — живым браузером, настоящим pdf.js.
//
// Отдельным файлом, а не строками в `fields-smoke`: тот у предела размера. Зовётся из него на
// документе «Кабельный журнал (из PDF)», у типа которого посев заводит шаблон на четыре листа
// (`seed-preview.mjs`).
//
// Сверяется то, чего не видит тест компонента с подменённым движком: новый вариант документа, собранный
// сервером, встаёт на том же листе, а лента на время подмены не пустеет ни на кадр. И отказ ОБНОВЛЕНИЯ —
// шаблон посева падает на номере `BROKEN_NUMBER` — прежний PDF не стирает: лист тот же, он приглушён, а
// отказ сказан строкой под шапкой (`preview-stale`; макет «Отказ обновления предпросмотра», вариант В).

import { BROKEN_MESSAGE, BROKEN_NUMBER, PREVIEW_PAGES } from './seed-preview.mjs';
import { sheetDrawn } from './pdf-viewer-checks.mjs';

/** Номер, который проверка вписывает в реквизит; шаблон печатает его на каждом листе. */
const NEW_NUMBER = 'КЖ-777';

/** Текст слоя листа без пробелов: pdf.js режет строку на куски, и пробелы между ними — его. */
const layerText = sheet => sheet.locator('.bhs-text-layer').innerText().then(t => t.replace(/\s+/g, ''));

/**
 * Отказ на экране панели — её собственный (`preview-failure`: сервер не построил PDF, нет шаблона) или
 * просмотрщика (`viewer-failure`: не загрузился кусок pdf.js, битый PDF). Второй рисуется то вместо
 * области, то внутри неё, поэтому ищется по всей панели. Каждое ожидание ниже ждёт свой исход ИЛИ отказ:
 * иначе отказ — например, Typst на стенде не запускается — краснел бы тридцатисекундным таймаутом с
 * чужой причиной («просмотрщика нет», «шаблона посева нет?», «новый вариант не встал»).
 */
const FAILURE = '[data-testid="preview-failure"], [data-testid="viewer-failure"]';

async function refused(panel) {
  const failure = panel.locator(FAILURE).first();
  if (!(await failure.count())) return;
  const [id, state] = await Promise.all([failure.getAttribute('data-testid'), failure.getAttribute('data-state')]);
  throw new Error(`предпросмотр отказал (${id}, ${state}): «${(await failure.innerText()).replace(/\s+/g, ' ').trim()}»`);
}

/**
 * Отказ обновления — строка `preview-stale` над прежним PDF. Текст сервера в ней свёрнут, и чтобы назвать
 * причину, его раскрывают; без строки — пусто.
 */
async function staleReason(panel) {
  const stale = panel.getByTestId('preview-stale');
  if (!(await stale.count())) return '';
  const more = stale.getByRole('button', { name: 'Подробнее' });
  if (await more.count()) await more.click();
  const message = stale.getByTestId('preview-stale-message');
  return `${await stale.getByRole('status').innerText()}: «${(await message.innerText()).replace(/\s+/g, ' ').trim()}»`;
}

/** Приглушён ли лист: прозрачность области просмотра, как её посчитал браузер. */
const opacity = region => region.evaluate(el => getComputedStyle(el).opacity);

export async function previewKeepsPage(page, editor, named) {
  // Панель по умолчанию свёрнута; ширина окна прогона — выше `xl`, иначе её нет вовсе.
  await page.getByRole('button', { name: 'Показать предпросмотр' }).click();
  const panel = page.getByTestId('document-preview');
  const region = panel.getByRole('region', { name: 'Предпросмотр документа' });
  const failure = panel.locator(FAILURE);
  await named('панель предпросмотра не показала ни просмотрщик, ни отказ',
    () => region.or(failure).first().waitFor({ timeout: 30_000 }));
  await refused(panel);
  if (await panel.locator('iframe').count()) throw new Error('в панели остался фрейм браузера');
  const toolbar = panel.getByTestId('viewer-toolbar');
  await named(`панель не говорит «из ${PREVIEW_PAGES}» — шаблона посева нет?`,
    () => toolbar.getByText(`из ${PREVIEW_PAGES}`).or(failure).first().waitFor({ timeout: 30_000 }));
  await refused(panel);

  const field = toolbar.getByRole('textbox', { name: 'Номер страницы' });
  await field.fill('2');
  await field.press('Enter');
  const second = region.getByRole('group', { name: `Страница 2 из ${PREVIEW_PAGES}` });
  await sheetDrawn(page, named, second).catch(async e => { await refused(panel); throw e; });

  // С этого мига и до конца — не побелел ли хоть на миг лист, нарисованный сейчас. Смотрят пиксели, а
  // не число холстов: холст, которому движок обнулил размеры в начале отрисовки, на странице остаётся,
  // а лист при этом белый до её конца. Размеры холста — атрибуты, и их обнуление наблюдатель видит.
  await region.evaluate(el => {
    const drawn = sheet => [...sheet.querySelectorAll('canvas')].some(c => {
      if (!c.width || !c.height) return false;
      const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      for (let i = 0; i < d.length; i += 4 * 31) if (d[i] < 160 && d[i + 3] > 0) return true;
      return false;
    });
    const watched = [...el.querySelectorAll('[data-page]')].filter(drawn).map(s => s.dataset.page);
    const blank = new Set();
    const observer = new MutationObserver(() => {
      for (const n of watched) {
        const sheet = el.querySelector(`[data-page="${n}"]`);
        if (!sheet || !drawn(sheet)) blank.add(n);
      }
    });
    observer.observe(el, { childList: true, subtree: true, attributes: true, attributeFilter: ['width', 'height'] });
    window.__previewWatch = { watched, blank, observer };
  });
  const watchResult = () => page.evaluate(() => {
    const w = window.__previewWatch;
    w?.observer.disconnect();
    delete window.__previewWatch;
    return w ? { watched: w.watched, blank: [...w.blank] } : null;
  });

  let watch;
  try {
    await regenerate(page, editor, named, panel, second, field);
  } finally {
    watch = await watchResult();
  }
  if (!watch?.watched.includes('2')) throw new Error(`второй лист не под наблюдением: ${JSON.stringify(watch)}`);
  if (watch.blank.length) throw new Error(`лист побелел на время подмены: листы ${watch.blank.join(', ')}`);
}

async function regenerate(page, editor, named, panel, second, field) {
  const number = editor.getByLabel('Номер', { exact: true });
  if ((await number.count()) !== 1) throw new Error(`поле «Номер» не найдено однозначно (${await number.count()})`);
  await brokenUpdate(page, named, panel, number, second, field);
  await number.fill(NEW_NUMBER);

  // Новый вариант пришёл, когда его номер напечатан на втором листе. Лист ищется заново на каждом
  // шаге, а не держится ручкой: ушедший и вставший заново лист ручка не увидела бы, и провал назвал
  // бы «не встал» вместо настоящей причины. Ищется в панели, а не в области: отказ перегенерации
  // снимает область вместе с прежним PDF, и ручка на неё указывала бы на отсоединённый узел.
  const area = await panel.elementHandle();
  // Строку отказа от проверочного номера сменит только удачный вариант, поэтому ждать её ухода нельзя: она
  // и есть «ещё не встал». Не встал за отведённое — причину называет она, если ещё стоит.
  await named(`второй лист не показал «${NEW_NUMBER}» — новый вариант не встал`, () => page.waitForFunction(
    ([el, text, failure]) => !!el.querySelector(failure)
      || (el.querySelector('[data-page="2"] .bhs-text-layer')?.textContent ?? '').replace(/\s+/g, '').includes(text),
    [area, NEW_NUMBER, FAILURE], { timeout: 30_000 })).catch(async e => {
    const reason = await staleReason(panel);
    if (reason) throw new Error(`новый вариант не встал, отказ обновления: ${reason}`);
    throw e;
  });
  await refused(panel);
  if (await panel.getByTestId('preview-stale').count()) throw new Error('строка отказа обновления осталась после удачного варианта');
  if ((await opacity(panel.getByRole('region', { name: 'Предпросмотр документа' }))) !== '1')
    throw new Error('лист остался приглушённым после удачного варианта');

  if (!(await layerText(second)).includes(NEW_NUMBER)) throw new Error('на втором листе прежний вариант');
  const shown = await field.inputValue();
  if (shown !== '2') throw new Error(`после перегенерации показана страница ${shown}, а не 2`);
}

/**
 * Отказ ОБНОВЛЕНИЯ: проверочный номер ломает шаблон посева. Прежний PDF обязан остаться — тот же лист, тот
 * же номер страницы, — приглушённым, а отказ — строкой с текстом сервера; плашки вместо PDF быть не должно.
 * Лист под наблюдением пикселей и здесь: побелей он на время отказа — провал назовёт `watch`.
 */
async function brokenUpdate(page, named, panel, number, second, field) {
  const before = await layerText(second);
  await number.fill(BROKEN_NUMBER);
  const stale = panel.getByTestId('preview-stale');
  await named('отказ перегенерации не показан строкой над прежним PDF', () =>
    stale.or(panel.locator(FAILURE)).first().waitFor({ timeout: 30_000 }));
  await refused(panel);
  const state = await stale.getAttribute('data-state');
  if (state !== 'error') throw new Error(`строка отказа обновления не того вида: ${state}`);

  const region = panel.getByRole('region', { name: 'Предпросмотр документа' });
  if ((await region.count()) !== 1) throw new Error('отказ обновления снял просмотрщик');
  if ((await field.inputValue()) !== '2') throw new Error(`на отказе обновления показана страница ${await field.inputValue()}, а не 2`);
  if ((await layerText(second)) !== before) throw new Error('на отказе обновления второй лист сменился');
  const dim = await opacity(region);
  if (dim === '1') throw new Error('устаревший лист не приглушён');

  const reason = await staleReason(panel);
  if (!reason.includes(BROKEN_MESSAGE)) throw new Error(`в строке отказа не текст сервера: ${reason}`);
}
