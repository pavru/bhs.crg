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
import { clickAt } from './harness.mjs';

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

        // Кнопка оплаты в шапке делает своё и часть не раскрывает: она над растянутой целью шапки
        // (costs-invoice-part-header-toggle, [COST-6.2]).
        await pay.click();
        const payment = page.getByRole('dialog', { name: /^Оплата: / });
        await h.named('«Отметить оплату» в шапке не открыла окно оплаты', () => payment.waitFor({ timeout: 5_000 }));
        await payment.getByRole('button', { name: 'Отмена', exact: true }).click();
        await h.named('окно оплаты не закрылось', () => payment.waitFor({ state: 'hidden', timeout: 5_000 }));
        if (!await toggle.count()) throw new Error('кнопка оплаты в шапке раскрыла «Сроки и оплату»');

        // Цель — вся шапка: щелчок по строке-итогу раскрывает часть. Мышью в точку строки, а не `click()` по ней:
        // строка лежит под растянутой целью, и щелчок туда получает цель — ровно это и проверяется.
        await clickAt(page, part.getByText('срок не определён · не оплачен', { exact: false }));
        await h.named('щелчок по строке-итогу не раскрыл часть',
          () => part.getByLabel('Дата отгрузки', { exact: true }).waitFor({ timeout: 5_000 }));
        // И сворачивает её щелчок по заголовку раскрытой.
        await clickAt(page, part.getByRole('button', { name: `Свернуть «${TERMS}»` }));
        await h.named('щелчок по заголовку раскрытой части её не свернул',
          () => part.getByLabel('Дата отгрузки', { exact: true }).waitFor({ state: 'hidden', timeout: 5_000 }));
        await clickAt(page, part.getByRole('button', { name: `Развернуть «${TERMS}»` }));
        await h.named('повторный щелчок по заголовку не раскрыл часть',
          () => part.getByLabel('Дата отгрузки', { exact: true }).waitFor({ timeout: 5_000 }));
      });

      // Узкая часть переносит действия строкой ниже (`@container` уже `@md`). На экранах ноутбука часть так не
      // сужается — форме остаётся не меньше 720, — поэтому часть сужают руками: проверяется, кому достаётся
      // щелчок, когда строка действий лежит над растянутой целью шапки. Сужается сама часть — её `@container`, —
      // а не обёртки карточки: прогон не зависит от того, как `Card` устроен внутри.
      await check(`«Сроки и оплата» ${width}×${height}: в узкой части щелчок мимо действий переключает часть, по действию — нет`, async () => {
        await page.setViewportSize({ width, height });
        await openAsIs(page, h, made.plain);
        const part = page.getByRole('region', { name: TERMS, exact: true });
        await h.named('части «Сроки и оплата» нет', () => part.waitFor({ timeout: 10_000 }));
        await part.evaluate(el => { el.style.width = '400px'; });
        const heading = part.getByRole('heading', { name: TERMS, exact: true });
        const pay = part.getByRole('button', { name: 'Отметить оплату', exact: true });
        // Суженная часть выше прежней: строка действий уходила ниже окна, и щелчок по точке — мимо экрана.
        await pay.scrollIntoViewIfNeeded();
        const [title, action] = [await heading.boundingBox(), await pay.boundingBox()];
        if (!title || !action || action.y < title.y + title.height)
          throw new Error('в части шириной 400 действия не перенесены строкой ниже заголовка');

        // Пустое место строки действий, правее кнопки, — часть шапки: щелчок туда раскрывает часть.
        const narrow = await part.boundingBox();
        await page.mouse.click(Math.min(action.x + action.width + 40, narrow.x + narrow.width - 24), action.y + action.height / 2);
        await h.named('щелчок мимо действия в узкой части её не раскрыл',
          () => part.getByLabel('Дата отгрузки', { exact: true }).waitFor({ timeout: 5_000 }));

        // Кнопка действия делает своё: окно оплаты, а часть остаётся раскрытой.
        await clickAt(page, pay);
        const payment = page.getByRole('dialog', { name: /^Оплата: / });
        await h.named('«Отметить оплату» в узкой части не открыла окно оплаты', () => payment.waitFor({ timeout: 5_000 }));
        await payment.getByRole('button', { name: 'Отмена', exact: true }).click();
        await h.named('окно оплаты не закрылось', () => payment.waitFor({ state: 'hidden', timeout: 5_000 }));
        if (!await part.getByRole('button', { name: `Свернуть «${TERMS}»` }).count())
          throw new Error('кнопка оплаты в узкой части переключила часть');
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
