// Полоса сверки — только числа и тон (costs-invoice-screen-11, задача 4.3, ТЗ COST-6.4): что расхождение
// значит, говорит пункт перечня состояния, один раз. Ломается: вернуть в полосу «— сохранить всё равно можно»
// или убрать пункт о сумме из готовности — расхождение названо дважды либо ни разу.
//
// Отдельным файлом: `invoice-lines-smoke` и так велик. Едет внутри него, с его помощниками.

import { statusItems } from './invoice-status.mjs';

export async function reconciliationChecks({ page, check, stamp, invoice, open, cell, addRow, saveLines }) {
  await check('полоса сверки: расхождение — числом с тоном; что оно значит — пункт перечня', async () => {
    const number = `СЧ-С${stamp}`;
    await invoice(number, 10_000);
    await open(number);

    await addRow(1);
    await cell('Количество', 1).fill('1');
    await cell('Цена', 1).fill('7000');
    await saveLines();

    const strip = page.getByTestId('invoice-reconciliation');
    const difference = strip.getByTestId('reconciliation-difference');
    await difference.waitFor({ timeout: 10_000 });
    const { text, tone } = await difference.evaluate(el => ({ text: el.textContent, tone: el.className }));
    if (!/^расхождение:\s*[−-]?3\s000,00\s₽$/.test(text.trim())) throw new Error(`в полосе не число расхождения: «${text}»`);
    if (!tone.includes('text-warning')) throw new Error(`у расхождения в полосе нет тона предупреждения: «${tone}»`);
    const words = await strip.innerText();
    if (/сохранить всё равно|расходится/.test(words)) throw new Error(`в полосе фраза, а не числа: «${words}»`);

    const items = (await statusItems(page)).filter(i => /расход/i.test(i.text));
    if (items.length !== 1) throw new Error(`расхождение в перечне ${items.length} раз: ${JSON.stringify(items.map(i => i.text))}`);
  });
}
