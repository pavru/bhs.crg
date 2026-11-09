// Посчитанная сумма строки остаётся посчитанной ПОСЛЕ сохранения (issue #1654, change
// `costs-invoice-line-computed-amount`, ТЗ COST-7, COST-7.2) — в обоих режимах НДС.
//
// Сервер хранит только досчитанное: 1 × 100 ложится суммой 100. До исправления форма брала её в черновик как
// набранную, и правка количества после сохранения сумму больше не трогала — 1 × 100 → сохранить → 10 давало
// 100, и сверка с бумагой расходилась молча. Юнит-тесты держат правило (`invoiceLineCases.json`, `afterSave`);
// здесь — что оно доходит до экрана и до ответа сервера целиком: запись, ответ, правка, вторая запись.
//
// Посчитанное в клетке — ПОДСКАЗКОЙ поля (placeholder), а не значением: пустое поле и значит «посчитай».
import { commitInvoice, confirmVatMode } from './invoice-save.mjs';


/**
 * Что показывает клетка — набранное либо посчитанное подсказкой, числом в записи «1000»: подсказка пишет
 * посчитанное с копейками и разрядами («1 000,00»), набранное — как набрано.
 */
export async function shown(field) {
  const text = ((await field.inputValue()) || (await field.getAttribute('placeholder')) || '').replace(/\s/g, '');
  const number = Number(text.replace(',', '.'));
  return text !== '' && Number.isFinite(number) ? String(number) : text;
}

/**
 * @param ctx то, чем живёт прогон строк счёта: страница, проверка и свои помощники.
 */
export async function computedAmountChecks({ page, check, stamp, invoice, open, cell, addRow }) {
  const box = () => page.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' });

  /** Уход из строки (запись строк, D-12.2) — и ответ записи: по нему сверяется сумма, которую записал сервер. */
  async function saveLines() {
    const answered = page.waitForResponse(
      r => r.request().method() === 'PUT' && r.url().endsWith('/lines'), { timeout: 15_000 });
    await commitInvoice(page);
    const answer = await answered;
    if (!answer.ok()) throw new Error(`строки не сохранились: ${answer.status()} ${(await answer.text()).slice(0, 200)}`);
    return (await answer.json()).lines;
  }

  const modes = [
    { mode: 'Included', title: '«в том числе»', price: 'Цена', sum: 'Сумма', letter: 'В' },
    { mode: 'OnTop', title: 'с НДС сверху', price: 'Цена без НДС', sum: 'Сумма без НДС', letter: 'С' },
  ];
  for (const { mode, title, price, sum, letter } of modes) {
    await check(`счёт ${title}: 1 × 100 → сохранить → количество 10 — сумма 1000 в форме и в ответе (#1654)`, async () => {
      const number = `СЧ-П${letter}${stamp}`;
      await invoice(number, 1_200);
      await open(number);
      if (mode === 'OnTop') {
        await box().click();
        await confirmVatMode(page);
        if (!(await box().isChecked())) throw new Error('флажок «Цены без НДС, НДС сверху» не поставился');
      }

      await addRow(1);
      await cell('Наименование в счёте', 1).fill(`Кабель посчитанный ${stamp}`);
      await cell('Количество', 1).fill('1');
      await cell(price, 1).fill('100');
      if (mode === 'OnTop') await cell('Ставка НДС', 1).fill('20');
      const [first] = await saveLines();
      const firstSum = mode === 'OnTop' ? first.netAmount : first.amount;
      if (firstSum !== 100) throw new Error(`первая запись: сервер досчитал ${firstSum}, ожидалось 100`);

      await cell('Количество', 1).fill('10');
      const inForm = await shown(cell(sum, 1));
      if (inForm !== '1000')
        throw new Error(`после сохранения и правки количества на 10 сумма в форме «${inForm}», ожидалась 1000 — `
          + 'досчитанная сервером сумма легла в черновик как набранная');

      const [second] = await saveLines();
      const expected = mode === 'OnTop' ? { netAmount: 1000, vatAmount: 200, amount: 1200 } : { amount: 1000 };
      for (const [key, value] of Object.entries(expected))
        if (second[key] !== value) throw new Error(`ответ второй записи: ${key} = ${second[key]}, ожидалось ${value}`);
    });
  }
}
