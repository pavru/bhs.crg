// Блок «Подходящие для этой строки» в окне выбора позиции (issue #1518, эпик #1511).
//
// Проверяется глазами человека, а не ответом сервера: кандидат виден с причиной; «Лучшее совпадение» —
// только у сильного, и Enter выбирает его, а строка обещает «запомнится»; строку, подходящую лишь по
// словам, Enter НЕ выбирает (выбор запоминается для поставщика — догадка запомнилась бы вместе с ним);
// отказ подбора сказан отказом, и «не нашлось» не мелькает ни на кадр; Enter, нажатый до ответа, не
// выбирает пришедшее после; в окне соответствий выбранная позиция стоит «Сейчас», а не кандидатом.

import { underRoute } from './harness.mjs';

/**
 * @param ctx то, чем живёт прогон строк счёта: страница, проверка, свои помощники.
 */
export async function candidateChecks({ page, check, stamp, invoice, open, cell, addRow, saveLines, position }) {
  const name = `Шина подбора ${stamp}`;
  const picker = () => page.getByRole('dialog', { name: 'Позиция номенклатуры' });
  const choose = () => page.getByRole('button', { name: 'выбрать позицию' }).first();

  await check('подходящие для строки: лучшее выбирается Enter, «по словам» — только стрелкой', async () => {
    await position(name);
    await position(`${name} медная`);
    const number = `СЧ-П${stamp}`;
    await invoice(number);
    await open(number);

    // Строка 1 — то же наименование, что у позиции: сильный кандидат.
    await addRow(1);
    await cell('Наименование в счёте', 1).fill(name);
    await cell('Количество', 1).fill('1');
    await choose().click();
    const best = picker().getByRole('option', { name: new RegExp(`Лучшее совпадение.*${name}`) });
    await best.waitFor({ timeout: 15_000 })
      .catch(() => { throw new Error('у строки с тем же наименованием нет «Лучшего совпадения»'); });
    if (!(await best.innerText()).includes('то же наименование'))
      throw new Error(`у кандидата не названа причина: «${await best.innerText()}»`);
    await page.keyboard.press('Enter');
    await picker().waitFor({ state: 'hidden', timeout: 10_000 })
      .catch(() => { throw new Error('Enter не выбрал «Лучшее совпадение»'); });
    await page.getByRole('button', { name, exact: true }).waitFor({ timeout: 10_000 });
    // Выбор позиции у черновика пишется сразу (costs-invoice-screen-12-autosave, D-12.2): пометка «запомнится» живёт до
    // ответа, а после него о запомненном говорит уведомление записи.
    await page.getByText('запомнится', { exact: true }).first().or(page.getByText(/Запомнено соответствий: 1/).first())
      .waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('у строки с выбранной позицией нет ни пометки «запомнится», ни «Запомнено соответствий»'); });

    // Строка 2 — название позиции целиком в строке, но строка другая: кандидат «по словам», не сильный.
    await addRow(2);
    await cell('Наименование в счёте', 2).fill(`${name} в бухте`);
    await cell('Количество', 2).fill('1');
    await choose().click();
    await picker().getByRole('option', { name: /название позиции целиком есть в строке/ }).first()
      .waitFor({ timeout: 15_000 })
      .catch(() => { throw new Error('у строки «по словам» нет кандидатов с причиной'); });
    if (await picker().getByText('Лучшее совпадение').count())
      throw new Error('«Лучшее совпадение» стоит у кандидата, подошедшего лишь по словам');
    await page.keyboard.press('Enter');
    // Окно обязано остаться: ждём столько, сколько хватило бы на закрытие.
    await page.waitForTimeout(500);
    if (!(await picker().isVisible())) throw new Error('Enter выбрал кандидата, подошедшего лишь по словам');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await picker().waitFor({ state: 'hidden', timeout: 10_000 })
      .catch(() => { throw new Error('↓ и Enter не выбрали первого кандидата'); });
    // Сохранённые строки запоминаются для поставщика — по ним проверяется окно соответствий.
    await saveLines();
  });

  // [COST-7] (costs-picker-sheet-reflow, вариант Е′): карточка, открывшись рядом со списком, сужает колонку. Раньше
  // от этого переносилась плашка строки и строки сдвигались на 20 px — под неподвижным курсором оказывалась соседняя,
  // и первое же движение мыши показывало в карточке не ту позицию. Курсор ставится у верхнего края строки: туда
  // сдвиг и уводил соседнюю.
  await check('карточка позиции не уводит строку из-под мыши: открытие, дрожь мыши, Esc, колесо', async () => {
    const before = page.viewportSize();
    await page.setViewportSize({ width: 1366, height: 768 });
    try {
      const number = `СЧ-ПК${stamp}`;
      await invoice(number);
      await open(number);
      await addRow(1);
      // Длинные слова поставщика: на всю ширину окна — в одну строку плашки, рядом с карточкой — в две. Имена обеих
      // позиций прогона в строке целиком — два кандидата «по словам», ни одного сильного.
      await cell('Наименование в счёте', 1)
        .fill(`${name} медная в бухте, кабель силовой медный ГОСТ 31996-2012 для прокладки в лотках и трубах`);
      await cell('Количество', 1).fill('1');
      await choose().click();
      const options = picker().getByRole('option');
      await options.nth(1).waitFor({ timeout: 15_000 })
        .catch(() => { throw new Error('у строки «по словам» меньше двух кандидатов — проверять сдвиг не на чем'); });
      if (await picker().getByText('Лучшее совпадение').count())
        throw new Error('у строки «по словам» есть «Лучшее совпадение» — карточка открыта с первого кадра, сдвига не проверить');
      const card = picker().getByRole('complementary', { name: 'Карточка позиции' });
      const search = picker().getByRole('combobox', { name: 'Поиск позиции' });
      const target = options.nth(1);
      const targetId = await target.getAttribute('id');
      const box = await target.boundingBox();
      if (!box) throw new Error('у второго кандидата нет места на экране');
      const x = box.x + 160, y = box.y + 6;

      await page.mouse.move(x, y - 1);
      await page.mouse.move(x, y);
      await card.waitFor({ timeout: 5_000 }).catch(() => { throw new Error('наведение не открыло карточку'); });
      await page.mouse.move(x, y + 2);
      await page.waitForTimeout(500);
      if ((await search.getAttribute('aria-activedescendant')) !== targetId)
        throw new Error('карточка открылась, мышь дрогнула на 2 px — и выделение ушло на соседнюю строку');
      const after = await target.boundingBox();
      if (!after || Math.abs(after.y - box.y) > 1)
        throw new Error(`карточка сдвинула строки: было ${box.y}, стало ${after?.y}`);

      // Колесо под открытой карточкой карточку само не меняет.
      await page.mouse.wheel(0, 120);
      await page.waitForTimeout(400);
      if ((await search.getAttribute('aria-activedescendant')) !== targetId)
        throw new Error('прокрутка колесом сама сменила выделение');

      // Esc закрывает сначала лист ([UI-81]); строка под курсором его снова не открывает.
      await page.mouse.wheel(0, -120);
      await search.focus();
      await page.keyboard.press('Escape');
      await card.waitFor({ state: 'hidden', timeout: 5_000 }).catch(() => { throw new Error('Esc не закрыл карточку'); });
      await page.mouse.move(x, y + 4);
      await page.mouse.move(x, y + 6);
      await page.waitForTimeout(500);
      if (await card.isVisible()) throw new Error('карточку закрыли Esc, мышь дрогнула — и она открылась снова');
      // Мышь перешла в другую строку сама — карточка открывается, как обычно.
      const first = await options.nth(0).boundingBox();
      await page.mouse.move(x, first.y + first.height / 2);
      await card.waitFor({ timeout: 5_000 }).catch(() => { throw new Error('после Esc наведение на другую строку не открыло карточку'); });

      // Крестик: под ним после закрытия оказывается список — подъехавшая строка карточку не открывает.
      const close = card.getByRole('button', { name: /Закрыть/ });
      const at = await close.boundingBox();
      await close.click();
      await card.waitFor({ state: 'hidden', timeout: 5_000 });
      await page.mouse.move(at.x + at.width / 2 - 2, at.y + at.height / 2 + 2);
      await page.waitForTimeout(500);
      if (await card.isVisible()) throw new Error('карточку закрыли крестиком — и строка, оказавшаяся под ним, открыла её снова');
    } finally {
      await page.keyboard.press('Escape');
      if (before) await page.setViewportSize(before);
    }
  });

  await check('окно соответствий: выбранная позиция — «Сейчас», среди подходящих её нет', async () => {
    await page.goto(new URL('/invoices', page.url()).href, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Соответствия', exact: true }).click();
    const matches = page.getByRole('dialog', { name: 'Соответствия наименований' });
    await matches.getByLabel('Поиск по артикулу или наименованию у поставщика').fill(name);
    const trigger = matches.getByRole('button', { name: new RegExp(`^${name}`) }).first();
    await trigger.waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('сохранённая строка не запомнилась соответствием поставщика'); });
    const current = (await trigger.innerText()).trim();
    await trigger.click();
    const now = picker().getByText(/^Сейчас:/);
    await now.waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('у соответствия не названа позиция «Сейчас»'); });
    if (!(await now.innerText()).includes(current))
      throw new Error(`«${await now.innerText()}» — не та позиция, что в соответствии («${current}»)`);
    await picker().getByText('Подбираем по словам строки…').waitFor({ state: 'detached', timeout: 15_000 });
    if (await picker().getByRole('listbox').getByText(current, { exact: true }).count())
      throw new Error('выбранная позиция повторена среди подходящих');
    await page.keyboard.press('Escape');
    await picker().waitFor({ state: 'hidden', timeout: 10_000 });
    await page.keyboard.press('Escape');
    await matches.waitFor({ state: 'hidden', timeout: 10_000 });
  });

  await check('Enter, нажатый до ответа подбора, не выбирает пришедшее после', async () => {
    const slow = '**/api/costs/nomenclature/candidates';
    try {
      await underRoute(page, slow, async route => {
        await new Promise(resolve => setTimeout(resolve, 1500));
        await route.continue().catch(() => {});
      }, async () => {
        const number = `СЧ-ПЖ${stamp}`;
        await invoice(number);
        await open(number);
        await addRow(1);
        // Своё наименование у позиции есть, соответствия для него нет — ответ придёт с «Лучшим совпадением».
        await cell('Наименование в счёте', 1).fill(`${name} медная`);
        await choose().click();
        await picker().getByText('Подбираем по словам строки…').waitFor({ timeout: 10_000 });
        await page.keyboard.press('Enter');
        await picker().getByRole('option', { name: /Лучшее совпадение/ }).waitFor({ timeout: 15_000 })
          .catch(() => { throw new Error('после ответа подбора нет «Лучшего совпадения»'); });
        await page.waitForTimeout(500);
        if (!(await picker().isVisible())) throw new Error('Enter, нажатый до ответа, выбрал позицию задним числом');
      });
    } finally {
      await page.keyboard.press('Escape');
    }
  });

  await check('подбор отказал — сказано отказом, «не нашлось» не появляется ни на кадр', async () => {
    const failing = '**/api/costs/nomenclature/candidates';
    try {
      await underRoute(page, failing, route => route.fulfill({
        status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'сбой прогона' }),
      }), async () => {
        const number = `СЧ-ПО${stamp}`;
        await invoice(number);
        await open(number);
        await addRow(1);
        await cell('Наименование в счёте', 1).fill(`Изделие без пары ${stamp}`);
        // Сторож «ни на кадр»: фраза пустого ответа, появившаяся хоть раз, остаётся записанной.
        await page.evaluate(() => {
          window.__sawNothing = false;
          new MutationObserver(() => {
            if (document.body.innerText.includes('подходящих не нашлось')) window.__sawNothing = true;
          }).observe(document.body, { subtree: true, childList: true, characterData: true });
        });
        await choose().click();
        await picker().getByText(/Подбор не сработал: сбой прогона/).waitFor({ timeout: 15_000 })
          .catch(() => { throw new Error('отказ подбора не назван отказом'); });
        if (await page.evaluate(() => window.__sawNothing))
          throw new Error('при отказе подбора мелькнуло «подходящих не нашлось»');
        // Поиск при этом работает.
        await picker().getByPlaceholder('часть наименования').fill(name);
        await picker().getByRole('option', { name: new RegExp(name) }).first().waitFor({ timeout: 10_000 })
          .catch(() => { throw new Error('при отказе подбора не работает поиск'); });
      });
    } finally {
      await page.keyboard.press('Escape');
    }
  });
}
