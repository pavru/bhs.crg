// Блок «Подходящие для этой строки» в окне выбора позиции (issue #1518, эпик #1511).
//
// Проверяется глазами человека, а не ответом сервера: кандидат виден с причиной; «Лучшее совпадение» —
// только у сильного, и Enter выбирает его, а строка обещает «запомнится»; строку, подходящую лишь по
// словам, Enter НЕ выбирает (выбор запоминается для поставщика — догадка запомнилась бы вместе с ним);
// отказ подбора сказан отказом, и «не нашлось» не мелькает ни на кадр.

/**
 * @param ctx то, чем живёт прогон строк счёта: страница, проверка, свои помощники.
 */
export async function candidateChecks({ page, check, stamp, invoice, open, cell, addRow, position }) {
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
    await page.getByText('запомнится', { exact: true }).first().waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('у строки с выбранной позицией нет пометки «запомнится»'); });

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
  });

  await check('подбор отказал — сказано отказом, «не нашлось» не появляется ни на кадр', async () => {
    const failing = '**/api/costs/nomenclature/candidates';
    await page.route(failing, route => route.fulfill({
      status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'сбой прогона' }),
    }));
    try {
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
    } finally {
      await page.unroute(failing);
      await page.keyboard.press('Escape');
    }
  });
}
