// «Сроки и оплата» свёрнута, если решать в ней нечего (costs-invoice-terms-collapse, ТЗ COST-6.2, #1818 п. 7) —
// живым браузером на двух размерах ноутбука.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Едет внутри него, под его входом и с
// его помощниками (`h`), как `invoice-layout-checks`.
//
// ⚠️ Счёт открывается БЕЗ `expandInvoiceParts`: проверяется ровно то, как части стоят при открытии.

import { invoiceOpened } from './invoice-save.mjs';

/** Размеры из задачи 1.2 change: ноутбук и 125 % на 1280. */
const SIZES = [[1366, 768], [1024, 768]];

const TERMS = 'Сроки и оплата';

/** Открыть счёт из списка, частей не раскрывая. */
async function openAsIs(page, h, number) {
  await page.goto(`${h.BASE}/invoices`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: new RegExp(number.replace(/[-/]/g, '.')) }).first().click();
  await invoiceOpened(page);
}

/** Реквизиты без срока: даты отгрузки нет — срок не определён. */
function withoutTerms(h, number, purpose) {
  const { ['Срок']: _due, ...rest } = h.requisites(number, purpose);
  return rest;
}

export async function invoiceTermsCollapseChecks(page, check, h) {
  const plain = `СЧ-T${h.stamp}`;
  const scanned = `СЧ-TS${h.stamp}`;
  try {
    await check('«Сроки и оплата»: счёт заведён — без срока и со сканом даты отгрузки', async () => {
      await h.api('POST', '/costs/invoices', { requisites: withoutTerms(h, plain, 'Срок не определён') });
      await h.api('POST', '/costs/invoices', {
        requisites: { ...withoutTerms(h, scanned, 'Отгрузка со скана'), 'ДатаОтгрузки': '2026-09-05' },
        unconfirmed: ['ДатаОтгрузки'],
      });
    });

    for (const [width, height] of SIZES) {
      await check(`«Сроки и оплата» ${width}×${height}: срок не определён — свёрнута строкой, «Отметить оплату» в шапке`, async () => {
        await page.setViewportSize({ width, height });
        await openAsIs(page, h, plain);
        const part = page.getByRole('region', { name: TERMS, exact: true });
        await h.named('части «Сроки и оплата» нет', () => part.waitFor({ timeout: 10_000 }));
        const toggle = part.getByRole('button', { name: `Развернуть «${TERMS}»` });
        if (!await toggle.count()) throw new Error('«Сроки и оплата» без срока открыта раскрытой');
        const said = await part.innerText();
        if (!said.includes('срок не определён · не оплачен'))
          throw new Error(`строка-итог не назвала пустой срок: «${said.replace(/\s+/g, ' ').slice(0, 200)}»`);
        if (await part.getByLabel('Дата отгрузки', { exact: true }).isVisible())
          throw new Error('поле «Дата отгрузки» видно у свёрнутой части');
        const pay = part.getByRole('button', { name: 'Отметить оплату', exact: true });
        if (!await pay.isVisible()) throw new Error('«Отметить оплату» не видна в шапке свёрнутой части');

        // Раскрывает и сворачивает человек — как прежде.
        await toggle.click();
        await h.named('раскрытая часть не показала «Дату отгрузки»',
          () => part.getByLabel('Дата отгрузки', { exact: true }).waitFor({ timeout: 5_000 }));
      });
    }

    await check('«Сроки и оплата»: дата отгрузки со скана, не проверена — часть раскрыта', async () => {
      await page.setViewportSize({ width: 1366, height: 768 });
      await openAsIs(page, h, scanned);
      const part = page.getByRole('region', { name: TERMS, exact: true });
      await h.named('части «Сроки и оплата» нет', () => part.waitFor({ timeout: 10_000 }));
      if (!await part.getByRole('button', { name: `Свернуть «${TERMS}»` }).count())
        throw new Error('«Сроки и оплата» с меткой со скана открыта свёрнутой: метку спрятали');
    });
  } finally {
    await page.setViewportSize({ width: 1500, height: 1000 });
  }
}
