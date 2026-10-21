// Счёт с НДС сверху — от бумаги до «Разобран» без перебивки строк (эпик #1496, issue #1499, #1500).
//
// Бумага (числа выдуманные): две строки без НДС — 2 × 500 и 1 × 500, «НДС 22 %» 330,00 и «Всего к оплате»
// 1 830,00. Строки вводятся так, как напечатаны, — ценами без НДС. Раньше такой счёт не сходился ровно на
// НДС, и выход был один: перебить каждую строку × 1,22 (владелец этот обход отверг).
//
// Проверяется целиком: подсказка сервера «похоже, цены без НДС» и кнопка, которая ставит флажок и только
// его (#1500); подписи колонок «без НДС»; «сходится» в полосе сверки; бумажная сумма, пережившая
// сохранение и перечитывание (а не превращённая в сумму с НДС); разноска «с НДС»; и «Разобран».

/**
 * @param ctx то, чем живёт прогон строк счёта: страница, проверка, свои помощники и позиция номенклатуры.
 */
export async function vatOnTopChecks({ page, check, stamp, invoice, open, cell, addRow, saveLines, cable, pickOption }) {
  const number = `СЧ-НС${stamp}`;
  const box = () => page.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' });
  const strip = () => page.getByText('сходится с суммой к оплате', { exact: true });

  await check('счёт с НДС сверху: подсказка ставит флажок, сверка сходится без перебивки строк', async () => {
    await invoice(number, 1_830, 330);
    await open(number);

    for (const [row, quantity] of [[1, '2'], [2, '1']]) {
      await addRow(row);
      await cell('Наименование в счёте', row).fill(`Кабель без НДС ${stamp}-${row}`);
      await cell('Количество', row).fill(quantity);
      await cell('Цена', row).fill('500');
      await cell('Ставка НДС', row).fill('22');
      await page.getByRole('button', { name: 'выбрать позицию' }).first().click();
      await page.getByPlaceholder('часть наименования').fill(stamp);
      await page.getByRole('option', { name: cable }).click();
    }
    await saveLines();

    // Сервер видит расхождение ровно на НДС и подсказывает — но режим не меняет сам (#1500).
    const hint = page.getByRole('button', { name: 'Считать НДС сверху' });
    await hint.waitFor({ timeout: 10_000 })
      .catch(() => { throw new Error('расхождение ровно на НДС, а подсказки «Считать НДС сверху» нет'); });
    if (await box().isChecked()) throw new Error('флажок «Цены без НДС» поставлен без нажатия');

    await hint.click();
    if (!(await box().isChecked())) throw new Error('подсказка не поставила флажок');
    // Фокус — на флажке: кнопка подсказки пропала с нажатием.
    await page.waitForFunction(() => document.activeElement?.getAttribute('type') === 'checkbox', null, { timeout: 5_000 })
      .catch(() => { throw new Error('после подсказки фокус не на флажке'); });
    await cell('Цена без НДС', 1).waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('колонка цены не подписана «без НДС»'); });
    await strip().waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('после флажка сверка не сказала «сходится с суммой к оплате»'); });
  });

  await check('счёт с НДС сверху: режим и бумажные суммы переживают сохранение, «Разобран» проходит', async () => {
    await saveLines();
    await page.reload({ waitUntil: 'networkidle' });
    await open(number);

    if (!(await box().isChecked())) throw new Error('после перечитывания флажок «Цены без НДС» снят');
    // Бумажная сумма — как напечатана, а не сумма с НДС: иначе следующее сохранение прибавило бы НДС снова.
    const paper = await cell('Сумма без НДС', 1).inputValue();
    if (paper !== '1000') throw new Error(`сумма без НДС строки 1 — «${paper}», ожидалась бумажная 1000`);
    await strip().waitFor({ timeout: 5_000 })
      .catch(() => { throw new Error('после перечитывания сверка не сходится'); });

    for (const [row, quantity] of [[1, '2'], [2, '1']]) {
      await page.getByRole('button', { name: `Разноска, строка ${row}` }).click();
      // Делится сумма с НДС, и окно это называет.
      await page.getByText(/на .+ с НДС\. Делится количество/).waitFor({ timeout: 5_000 })
        .catch(() => { throw new Error(`окно разноски строки ${row} не говорит «с НДС»`); });
      await pickOption(page.getByLabel('Куда, часть 1', { exact: true }), 'Демо-стройка');
      await page.getByLabel('Количество, часть 1', { exact: true }).fill(quantity);
      const allocated = page.waitForResponse(
        r => r.request().method() === 'PUT' && r.url().endsWith('/allocation'), { timeout: 15_000 });
      await page.getByRole('button', { name: 'Сохранить разноску' }).click();
      if (!(await allocated).ok()) throw new Error(`разноска строки ${row} не сохранилась`);
    }

    await page.waitForFunction(
      () => (document.querySelector('[data-testid="invoice-readiness"]')?.textContent ?? '').includes('Всё готово'),
      null, { timeout: 10_000 })
      .catch(async () => {
        const said = await page.getByTestId('invoice-readiness').innerText().catch(() => 'строки нет');
        throw new Error(`строка готовности не «Всё готово»: «${said.replace(/\s+/g, ' ')}»`);
      });
    const passed = page.waitForResponse(
      r => r.request().method() === 'POST' && r.url().endsWith('/parsed'), { timeout: 15_000 });
    await page.getByRole('button', { name: 'Разобран', exact: true }).click();
    const answer = await passed;
    if (!answer.ok()) throw new Error(`«Разобран» отказал счёту с НДС сверху: ${answer.status()} ${(await answer.text()).slice(0, 200)}`);
  });
}
