// Индикатор состояния счёта (costs-invoice-screen-11, задача 3.3, ТЗ COST-6.4) — живым браузером на счёте
// «со всеми сообщениями» из `invoice-screen-layout-checks`: двойник, метки со скана, сбой распознавания,
// сорок строк без позиции с расхождением суммы, просрочка.
//
// Отдельным файлом: `invoices-smoke` у предела размера. Едет внутри `invoice-screen-layout-checks`, с его
// счётом и помощниками (`h`). На ветке без индикатора (10) краснеет: индикатора нет, плашки стоят в форме.

import { inStatusSheet, statusAction, statusBanner, statusItems } from './invoice-status.mjs';

export async function invoiceStatusChecks(page, check, h, invoice) {
  const go = async () => {
    await page.setViewportSize({ width: 1366, height: 768 });
    await page.goto(`${h.BASE}/invoices?invoice=${invoice.id}`, { waitUntil: 'networkidle' });
    await h.named('индикатора состояния нет', () => page.getByTestId('invoice-status').waitFor({ timeout: 10_000 }));
    // Сбой распознавания — «Мешает работе»: дождаться его, иначе перечень собран до ответа о распознавании.
    await h.named('banner сбоя распознавания нет', () => statusBanner(page).waitFor({ timeout: 15_000 }));
  };

  await check('индикатор: число — пункты перечня вне «К сведению»; подзаголовок «по сохранённому»', async () => {
    await go();
    const shown = Number(await page.getByTestId('invoice-status').getAttribute('data-count'));
    const { counted, subtitle } = await inStatusSheet(page, async sheet => ({
      counted: await sheet.locator('section:not([data-testid="invoice-status-info"]) [data-testid^="status-item-"]').count(),
      subtitle: await sheet.getByTestId('invoice-status-subtitle').innerText(),
    }));
    if (shown !== counted) throw new Error(`в индикаторе ${shown}, а пунктов вне «К сведению» ${counted}`);
    if (!subtitle.startsWith(`${counted} `) || !/по сохранённому · \d\d\.\d\d\.\d{4}/.test(subtitle))
      throw new Error(`подзаголовок перечня: «${subtitle}»`);
  });

  await check('индикатор: расхождение суммы названо один раз, в форме один banner и других тревог нет', async () => {
    await go();
    const inSheet = (await statusItems(page)).filter(i => /расход/i.test(i.text));
    // Форма — сводка и прокрутка частей, без полосы сверки: в ней остаются числа (до PR 2 — и её фраза). Список
    // рядом не в счёт: его строка говорит о СВОЁМ счёте.
    const onPage = await page.evaluate(() => {
      const strip = document.querySelector('[data-testid="invoice-reconciliation"]');
      let n = 0;
      for (const root of document.querySelectorAll('[data-testid="invoice-header"], [data-testid="invoice-scroll"]')) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node; node = walker.nextNode())
          if (!strip?.contains(node)) n += (node.textContent.match(/расход/gi) ?? []).length;
      }
      return n;
    });
    if (inSheet.length + onPage !== 1)
      throw new Error(`расхождение названо ${inSheet.length + onPage} раз: в перечне ${JSON.stringify(inSheet.map(i => i.text))}, в форме ${onPage}`);
    if (!/мешает: Разобран · оплата|мешает: Разобран/.test(inSheet[0]?.text ?? '')) throw new Error(`у пункта нет «мешает»: ${inSheet[0]?.text}`);

    const banners = await statusBanner(page).count();
    const alerts = await page.getByTestId('invoice-scroll').locator('[role="alert"]').count();
    if (banners !== 1 || alerts !== 1) throw new Error(`в форме banner: ${banners}, тревог (role=alert): ${alerts} — ждали по одному`);
  });

  await check('индикатор: «К строкам» раскрывает «Строки» и ставит фокус в названную строку', async () => {
    await go();
    const items = await statusItems(page);
    const lines = items.find(i => i.key.startsWith('gap:') && /позиции/.test(i.text));
    if (!lines) throw new Error(`в перечне нет пункта о строках без позиции: ${JSON.stringify(items.map(i => i.text))}`);
    await statusAction(page, lines.key, 'К строкам');
    await page.waitForFunction(() => !!document.activeElement?.closest('[data-readiness^="line:"]'), null, { timeout: 5_000 })
      .catch(() => { throw new Error('после «К строкам» фокус не в строке таблицы'); });
    const row = await page.evaluate(() => document.activeElement.closest('[data-readiness^="line:"]').getAttribute('data-readiness'));
    if (row !== 'line:1') throw new Error(`фокус в строке «${row}», а пункт называет первой строку 1`);
  });
}
