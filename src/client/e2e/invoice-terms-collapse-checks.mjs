// «Сроки и оплата» свёрнута, если решать в ней нечего (costs-invoice-terms-collapse, ТЗ COST-6.2, #1818 п. 7) —
// живым браузером на двух размерах ноутбука.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Едет внутри него, под его входом и с
// его помощниками (`h`), как `invoice-layout-checks`.
//
// ⚠️ Счёт открывается БЕЗ `expandInvoiceParts`: проверяется ровно то, как части стоят при открытии.
//
// Пункт готовности о поле части живьём не завести: сервер ставит их только об обязательных полях, а в «Сроках и
// оплате» обязательных нет ([COST-9]). Этот случай — в `invoiceParts.test.ts`; здесь раскрывают метка со скана и
// отгрузка раньше даты счёта.

import { invoiceOpened } from './invoice-save.mjs';

/** Размеры из задачи 1.2 change: ноутбук и 125 % на 1280. */
const SIZES = [[1366, 768], [1024, 768]];

const TERMS = 'Сроки и оплата';

/**
 * Открыть счёт по адресу, частей не раскрывая. По id, а не по номеру в списке: номер с приметой прогона может
 * совпасть со счётом прошлого прогона на той же базе, и список открыл бы чужой.
 */
async function openAsIs(page, h, id) {
  await page.goto(`${h.BASE}/invoices?invoice=${id}`, { waitUntil: 'networkidle' });
  await invoiceOpened(page);
}

/** Реквизиты без срока: даты отгрузки нет — срок не определён. */
function withoutTerms(h, number, purpose) {
  const { ['Срок']: _due, ...rest } = h.requisites(number, purpose);
  return rest;
}

/** Часть «Сроки и оплата» открыта раскрытой — у неё «Свернуть». */
async function shownOpen(page, h, id, why) {
  await openAsIs(page, h, id);
  const part = page.getByRole('region', { name: TERMS, exact: true });
  await h.named('части «Сроки и оплата» нет', () => part.waitFor({ timeout: 10_000 }));
  if (!await part.getByRole('button', { name: `Свернуть «${TERMS}»` }).count())
    throw new Error(`«Сроки и оплата» открыта свёрнутой, хотя ${why}`);
}

export async function invoiceTermsCollapseChecks(page, check, h) {
  const was = page.viewportSize();
  const made = {};
  try {
    await check('«Сроки и оплата»: счета заведены — без срока, со сканом даты отгрузки, с отгрузкой раньше счёта', async () => {
      made.plain = (await h.api('POST', '/costs/invoices', {
        requisites: withoutTerms(h, `СЧ-T${h.stamp}`, 'Срок не определён'),
      })).id;
      made.scanned = (await h.api('POST', '/costs/invoices', {
        requisites: { ...withoutTerms(h, `СЧ-TS${h.stamp}`, 'Отгрузка со скана'), 'ДатаОтгрузки': '2026-09-05' },
        unconfirmed: ['ДатаОтгрузки'],
      })).id;
      // Дата счёта у `requisites` — 03.09.2026: отгрузка 01.09 раньше неё. С отсрочкой срок определён — часть
      // раскрывает именно замечание, а не пустой срок.
      made.early = (await h.api('POST', '/costs/invoices', {
        requisites: { ...withoutTerms(h, `СЧ-TE${h.stamp}`, 'Отгрузка раньше счёта'), 'ДатаОтгрузки': '2026-09-01', 'Отсрочка': 10 },
      })).id;
    });
    if (!made.early) return;

    for (const [width, height] of SIZES) {
      await check(`«Сроки и оплата» ${width}×${height}: срок не определён — свёрнута строкой, «Отметить оплату» в шапке`, async () => {
        await page.setViewportSize({ width, height });
        await openAsIs(page, h, made.plain);
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

      await check(`«Сроки и оплата» ${width}×${height}: метка со скана и отгрузка раньше счёта — часть раскрыта`, async () => {
        await page.setViewportSize({ width, height });
        await shownOpen(page, h, made.scanned, 'дата отгрузки со скана не проверена: метку спрятали');
        await shownOpen(page, h, made.early, 'отгрузка раньше даты счёта: опечатку спрятали');
      });
    }
  } finally {
    if (was) await page.setViewportSize(was);
  }
}
