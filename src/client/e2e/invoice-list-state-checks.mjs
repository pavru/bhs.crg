// Состояние счёта ведущим значком в ленте (costs-invoice-list-state-mark, ТЗ COST-6.3, #1818 п. 1, макет А) — живым
// браузером на двух размерах ноутбука. У черновика, разобранного, оплаченного, оплаченного черновика и отклонённого —
// свой значок и тон; подсказка значка называет состояние словом, фокус строки с клавиатуры открывает её сам, наведение
// на строку мимо значка — нет; слово стоит в имени строки; «отклонён» назван один раз.
//
// Отдельным файлом: `invoice-lines-smoke` у предела размера. Едет внутри него, с его страницей и помощниками — там
// есть и позиция номенклатуры, без которой счёт не разобрать.

import { BASE } from './harness.mjs';

/** Размеры из задачи 1.3 change: ноутбук и 125 % на 1280. Первый — основной: на нём и проверки после цикла. */
const SIZES = [[1366, 768], [1024, 768]];
const [MAIN_W, MAIN_H] = SIZES[0];

const yesterday = () => new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);

export async function listStateChecks({ page, check, stamp, api, invoice, position }) {
  const site = (await api('GET', '/constructions')).find(c => c.name === 'Демо-стройка');
  const made = {};

  /** Строки на всю сумму и разноска на стройку; с позицией — счёт можно разобрать, без неё — только оплатить. */
  async function lined(number, positionId) {
    const created = await invoice(number, 1_000);
    const view = await api('PUT', `/costs/invoices/${created.id}/lines`, { lines: [{
      ...(positionId ? { nomenclature: { $ref: 'catalog', entryId: positionId } } : {}),
      supplierText: 'Кабель ленты', unit: 'м', quantity: '2', price: '500',
    }] });
    await api('PUT', `/costs/invoices/${created.id}/lines/${view.lines[0].id}/allocation`, {
      parts: [{ construction: site.id, quantity: '2' }],
    });
    return created.id;
  }
  /** Переход состояния. Версию счёта заголовком `If-Match` ставит обвязка (`nameSeenVersion` в `harness.mjs`). */
  const transition = (id, path) => api('POST', `/costs/invoices/${id}/${path}`);
  async function pay(id) {
    const paidOn = yesterday();
    const seen = await api('POST', `/costs/invoices/${id}/paid/preview`, { paidOn });
    await api('POST', `/costs/invoices/${id}/paid`, { paidOn, document: 'п/п прогона ленты', seen: seen.stamp });
  }

  await check('лента: счета всех состояний заведены', async () => {
    if (!site) throw new Error('стройки «Демо-стройка» нет — посев не отработал');
    const positionId = await position(`Кабель ленты ${stamp}`);
    made.draft = { number: `ЛС-Ч-${stamp}`, word: 'черновик', hint: 'Черновик — ещё не разобран', tone: 'text-on-surface-variant' };
    made.draft.id = (await invoice(made.draft.number)).id;
    made.parsed = { number: `ЛС-Р-${stamp}`, word: 'разобран', hint: 'Разобран, не оплачен', tone: 'text-primary' };
    made.parsed.id = await lined(made.parsed.number, positionId);
    await transition(made.parsed.id, 'parsed');
    made.paid = { number: `ЛС-О-${stamp}`, word: 'оплачен', hint: 'Оплачен', tone: 'text-success' };
    made.paid.id = await lined(made.paid.number, positionId);
    await transition(made.paid.id, 'parsed');
    await pay(made.paid.id);
    made.paidDraft = { number: `ЛС-ОЧ-${stamp}`, word: 'оплачен, черновик', hint: 'Оплачен, но не разобран — черновик', tone: 'text-success' };
    made.paidDraft.id = await lined(made.paidDraft.number);
    await pay(made.paidDraft.id);
    made.rejected = { number: `ЛС-Х-${stamp}`, word: 'отклонён', hint: 'Отклонён — по счёту не платим', tone: 'text-on-surface-variant' };
    made.rejected.id = (await invoice(made.rejected.number)).id;
    await transition(made.rejected.id, 'rejected');
  });
  if (!made.rejected?.id) return;

  const row = number => page.getByTestId('invoice-row').filter({ hasText: number });
  // Подсказка ровно этими словами: «Оплачен» — начало и подсказки оплаченного черновика.
  const tooltip = text => page.getByRole('tooltip').getByText(text, { exact: true });
  const was = page.viewportSize();
  try {
    for (const [width, height] of SIZES) {
      await check(`лента ${width}×${height}: у каждого состояния свой значок, тон, слово в имени и подсказка значка`, async () => {
        await page.setViewportSize({ width, height });
        await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
        for (const s of [made.draft, made.parsed, made.paid, made.paidDraft, made.rejected]) {
          const r = row(s.number);
          await r.waitFor({ timeout: 10_000 });
          const mark = r.getByTestId('row-state');
          const box = await mark.boundingBox();
          if (!box || box.width < 23) throw new Error(`у строки ${s.number} значка состояния не видно`);
          const tone = await mark.locator('svg').getAttribute('class');
          if (!tone?.includes(s.tone)) throw new Error(`значок «${s.word}» не тона ${s.tone}: «${tone}»`);
          const said = await r.textContent();
          if (!said.includes(`, ${s.word}, `)) throw new Error(`имя строки ${s.number} не называет «${s.word}»: «${said}»`);
          await mark.hover();
          await tooltip(s.hint).waitFor({ timeout: 3_000 })
            .catch(() => { throw new Error(`подсказка значка «${s.word}» не открылась словами «${s.hint}»`); });
          await page.mouse.move(0, 0);
        }
        const rejected = (await row(made.rejected.number).textContent()).match(/отклонён/g) ?? [];
        if (rejected.length !== 1) throw new Error(`«отклонён» назван в строке ${rejected.length} раз(а) — значком и словом?`);
      });
    }

    await check('лента: наведение на строку мимо значка подсказки состояния не открывает, фокус с клавиатуры — открывает', async () => {
      // На основном размере, а не на последнем из цикла: на узком открытый счёт встаёт вместо ленты.
      await page.setViewportSize({ width: MAIN_W, height: MAIN_H });
      await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' });
      // Ровно «Оплачен»: у соседней строки оплаченного черновика подсказка начинается тем же словом.
      const paidTip = tooltip('Оплачен');
      const r = row(made.paid.number);
      await r.getByText(made.paid.number, { exact: true }).hover();
      await page.waitForTimeout(800);
      if (await paidTip.count()) throw new Error('подсказка состояния открылась наведением на строку, а не на значок');
      await page.mouse.move(0, 0);
      // Фокус — программно, затем Shift+Tab и Tab: возврат клавишей ставит фокус «с клавиатуры» (`:focus-visible`).
      await r.focus();
      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Tab');
      await paidTip.waitFor({ timeout: 3_000 })
        .catch(() => { throw new Error('фокус строки с клавиатуры не открыл подсказку значка'); });
      // Уход фокуса — снятием, а не Tab: следующая строка могла бы открыть свою подсказку тех же слов.
      await r.evaluate(el => el.blur());
      await paidTip.waitFor({ state: 'detached', timeout: 3_000 })
        .catch(() => { throw new Error('подсказка значка осталась после ухода фокуса'); });
    });

    await check('лента: выбранная строка оплаченного — значок держит цвет success, слово на месте', async () => {
      const r = row(made.paid.number);
      await r.click();
      await page.waitForFunction(n => [...document.querySelectorAll('[data-testid=invoice-row]')]
        .some(b => b.textContent.includes(n) && b.getAttribute('aria-current') === 'true'), made.paid.number, { timeout: 10_000 });
      const tone = await r.getByTestId('row-state').locator('svg').getAttribute('class');
      if (!tone?.includes('text-success')) throw new Error(`на выбранной строке значок «оплачен» сменил тон: «${tone}»`);
      if (!(await r.textContent()).includes(', оплачен, ')) throw new Error('на выбранной строке слово «оплачен» пропало из имени');
    });
  } finally {
    if (was) await page.setViewportSize(was);
  }
}
