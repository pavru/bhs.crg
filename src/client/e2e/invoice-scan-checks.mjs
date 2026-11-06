// Скан счёта на своём просмотрщике (issue #1491, [COST-6.2], [UI-24]) — живым браузером, под CSP поставки.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Едет внутри него, под
// его входом и с его помощниками (`h`), как `invoice-layout-checks`.
//
// Сверяется то, чего не видят тесты компонента: фото декодировано из `blob:` под CSP, PDF-скан
// НАРИСОВАН, на узком экране файл встаёт слоем поверх формы — и новой вкладки при этом нет.

import { sheetDrawn } from './pdf-viewer-checks.mjs';

const PDF_TYPE = 'application/pdf';

/**
 * Следить за первым листом просмотрщика и записать его вид в миг появления контура — до отрисовки: лист
 * белый, край — тень (светлая) или обводка (тёмная), холст — роль `surface-container` ([UI-18], md3-16).
 * Наблюдатель срабатывает до кадра, а холст листа встаёт позже — по готовности отрисовки вне экрана, так
 * что видна именно загрузка, а не нарисованный лист.
 */
async function watchFirstSheet(page) {
  await page.evaluate(() => {
    window.__sheetAtLoad = null;
    const look = el => {
      const probe = document.createElement('div');
      probe.style.background = 'var(--md-sys-color-surface-container)';
      document.body.append(probe);
      const role = getComputedStyle(probe).backgroundColor;
      probe.remove();
      // Холст — первая непрозрачная заливка над листом: область прокрутки прозрачна, красит корень.
      let canvas = null;
      for (let n = el.parentElement; n && !canvas; n = n.parentElement) {
        const bg = getComputedStyle(n).backgroundColor;
        if (bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') canvas = bg;
      }
      const s = getComputedStyle(el);
      return {
        theme: document.documentElement.dataset.theme, drawn: !!el.querySelector('canvas'),
        sheet: s.backgroundColor, shadow: s.boxShadow, outline: s.outlineStyle, canvas, role,
      };
    };
    const observer = new MutationObserver(() => {
      const el = document.querySelector('[data-testid=viewer-region] .bhs-sheet[data-page="1"]');
      if (!el) return;
      observer.disconnect();
      window.__sheetAtLoad = look(el);
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });
}

export async function invoiceScanChecks(page, check, h) {
  await check('фото счёта — режимом картинки: декодировано под CSP, без нумерации страниц', async () => {
    const number = `СЧ-Ф${h.stamp}`;
    const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Фото счёта') });
    await h.attachScan(created.id);

    await h.open(number);
    const region = page.locator('[data-testid=viewer-region][data-view=image]');
    await h.named('фото не встало в просмотрщик', () => region.waitFor({ timeout: 15_000 }));
    // Картинка декодирована: `blob:` под `img-src` поставки пропущен. Пустая рамка выглядела бы показом.
    const shown = await region.elementHandle();
    await h.named('фото не декодировано', () => page.waitForFunction(el => {
      const img = el.querySelector('img');
      return !!img && img.complete && img.naturalWidth > 0;
    }, shown, { timeout: 15_000 }));
    await h.named('панели просмотрщика у фото нет', () => page.getByTestId('viewer-toolbar').waitFor({ timeout: 5_000 }));
    if ((await page.getByTestId('viewer-toolbar').getByRole('textbox', { name: 'Номер страницы' }).count()) !== 0)
      throw new Error('у фото на панели нумерация страниц');
  });

  await check('лист белый при загрузке в обеих темах, холст — surface-container', async () => {
    const number = `СЧ-Т${h.stamp}`;
    const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Лист в темах') });
    await h.attachScan(created.id, h.officeFile('converter-reference.pdf'), 'Скан.pdf', PDF_TYPE);
    const sheet = page.getByRole('region', { name: 'Скан.pdf' }).getByRole('group', { name: /^Страница 1 из / });
    // Тема ставится атрибутом на списке, а счёт открывается переходом внутри приложения: лист грузится
    // уже в ней. Выбор темы на сервер не пишется — он пережил бы проверку; перезагрузка вернёт свою.
    for (const scheme of ['light', 'dark']) {
      await page.goto(new URL('/invoices', page.url()).href, { waitUntil: 'networkidle' });
      await page.evaluate(t => document.documentElement.setAttribute('data-theme', t), scheme);
      await watchFirstSheet(page);
      await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
      await h.named(`контур листа не встал (${scheme})`, () => sheet.waitFor({ timeout: 30_000 }));
      const at = await (await page.waitForFunction(() => window.__sheetAtLoad, null, { timeout: 5_000 })).jsonValue();
      if (at.drawn) throw new Error(`контур листа пойман уже нарисованным (${scheme}) — загрузка не проверена`);
      const edge = scheme === 'dark' ? at.outline === 'solid' && at.shadow === 'none'
        : at.outline === 'none' && at.shadow !== 'none';
      if (at.theme !== scheme || at.sheet !== 'rgb(255, 255, 255)' || !edge || at.canvas !== at.role)
        throw new Error(`вид листа в теме ${scheme}: ${JSON.stringify(at)}`);
      await sheetDrawn(page, h.named, sheet);
    }
    await page.reload({ waitUntil: 'networkidle' });
  });

  await check('поддельный PDF: отказ «файл не читается как PDF» и «Оригинал»', async () => {
    const number = `СЧ-Н${h.stamp}`;
    const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Поддельный PDF') });
    // От PDF одна подпись с версией: без неё сервер файл не принял бы — вид он определяет по содержимому.
    await h.attachScan(created.id, Buffer.from(`%PDF-1.4 не настоящий ${h.stamp}`).toString('base64'), 'Поддельный.pdf', PDF_TYPE);

    await h.open(number);
    const failure = page.getByTestId('viewer-failure');
    await h.named('отказа просмотрщика нет', () => failure.waitFor({ timeout: 30_000 }));
    if (await failure.getAttribute('data-state') !== 'not-pdf')
      throw new Error(`отказ не тот: «${await failure.innerText()}»`);
    await h.named('в отказе нет «Оригинал»',
      () => failure.getByRole('button', { name: 'Оригинал', exact: true }).waitFor({ timeout: 5_000 }));
  });

  await check('PDF-скан на 1100 px: «Файл» — просмотрщик поверх формы, новой вкладки нет', async () => {
    const number = `СЧ-У${h.stamp}`;
    const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Узкий экран') });
    await h.attachScan(created.id, h.officeFile('converter-reference.pdf'), 'Скан.pdf', PDF_TYPE);

    await h.open(number);
    await h.named('PDF-скан не нарисован рядом с формой',
      () => sheetDrawn(page, h.named, page.getByRole('region', { name: 'Скан.pdf' }).getByRole('group', { name: /^Страница 1 из / })));

    const tabs = page.context().pages().length;
    let popups = 0;
    const onPopup = () => { popups++; };
    page.on('popup', onPopup);
    await page.setViewportSize({ width: 1100, height: 1000 });
    try {
      // Рабочая область 1020: файлу 420, форме 600 — рядом места нет (D-10.3), панель уходит, и «Файл» в
      // сводке открывает файл поверх формы.
      await h.named('панель файла осталась рядом на 1100',
        () => page.getByRole('complementary', { name: 'Файл счёта' }).waitFor({ state: 'detached', timeout: 10_000 }));
      await page.getByTestId('invoice-header').getByRole('button', { name: 'Файл', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await h.named('просмотрщик поверх формы не открылся', () => dialog.waitFor({ timeout: 15_000 }));
      const sheet = dialog.getByRole('region', { name: 'Скан.pdf' }).getByRole('group', { name: /^Страница 1 из / });
      await sheetDrawn(page, h.named, sheet);
      if (popups || page.context().pages().length !== tabs) throw new Error('файл открылся новой вкладкой');

      await page.keyboard.press('Escape');
      await h.named('Esc не закрыл просмотрщик', () => dialog.waitFor({ state: 'detached', timeout: 10_000 }));
      await h.named('после Esc формы нет',
        () => page.getByRole('button', { name: 'Сохранить', exact: true }).waitFor({ timeout: 5_000 }));
    } finally {
      page.off('popup', onPopup);
      await page.setViewportSize({ width: 1500, height: 1000 });
    }
  });
}
