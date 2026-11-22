import { statusAction } from './invoice-status.mjs';

export async function duplicatePaymentCheck(page, { api, invoice, open, site, number }) {
  const otherName = 'Стройка двойника для сверки';
  const otherSite = (await api('GET', '/constructions')).find(construction => construction.name === otherName)
    ?? await api('POST', '/constructions', { name: otherName });
  const one = await invoice(number, 100, 100);
  await open(number);
  const enterPayment = page.getByRole('button', { name: /^Внести оплату/ });
  await enterPayment.first().click();
  const payment = page.getByRole('dialog', { name: /^Оплата:/ });
  const pay = payment.getByRole('button', { name: /^Внести оплату/ });
  await pay.click({ trial: true });

  const other = await invoice(number, 190, 190, otherSite.id);
  const third = await invoice(number, 280, 280);
  await pay.click();
  await payment.getByText(/Оплатить нельзя: есть неподтверждённое совпадение/).waitFor();
  await payment.getByRole('button', { name: 'Понятно' }).click();
  await payment.waitFor({ state: 'detached' });
  if (await enterPayment.count()) throw new Error('после отказа по дублю остался вход в оплату');

  const compare = page.getByRole('dialog', { name: 'Сверка счетов', exact: true });
  const duplicate = compare.getByRole('region', { name: 'Двойник' });
  const confirm = compare.getByRole('button', { name: 'Это не дубль', exact: true });
  const reason = compare.getByRole('textbox', { name: /Причина/ });
  const status = page.getByRole('dialog', { name: 'Состояние счёта', exact: true });
  await statusAction(page, `duplicate:${other}`, 'Сверить…');
  await compare.getByRole('region', { name: 'Открытый счёт' }).getByText(/100[.,]00/).waitFor();
  await compare.getByRole('region', { name: 'Открытый счёт' }).getByText(site.name, { exact: true }).waitFor();
  await duplicate.getByText(/190[.,]00/).waitFor();
  await duplicate.getByText(otherSite.name, { exact: true }).waitFor();
  if (!await confirm.isDisabled()) throw new Error('подтверждение доступно без причины');

  const select = compare.getByRole('combobox', { name: 'Счёт для сверки' });
  await select.click();
  await page.getByRole('option', { name: /280[.,]00/ }).click();
  await duplicate.getByText(/280[.,]00/).waitFor();
  await select.click();
  await page.getByRole('option', { name: new RegExp(`190[.,]00.*${otherSite.name}`) }).click();
  await reason.fill('Отдельные поставки');
  await confirm.click();
  await compare.waitFor({ state: 'detached' });

  await status.getByTestId(`status-item-duplicate:${third}`).waitFor();
  if (await status.getByTestId(`status-item-duplicate:${other}`).count())
    throw new Error('подтверждённая пара осталась в перечне');
  await status.getByRole('button', { name: 'Закрыть', exact: true }).click();
  await status.waitFor({ state: 'detached' });
  if (await enterPayment.count()) throw new Error('вход в оплату вернулся до проверки второй пары');

  const a = await api('GET', `/costs/invoices/${one}`);
  const b = await api('GET', `/costs/invoices/${other}`);
  if (a.payment.paid || a.payment.duplicateBlockers[0]?.id !== third || b.payment.duplicateBlockers[0]?.id !== third)
    throw new Error('подтверждение пары не применилось симметрично либо не сохранило другую блокировку');

  await statusAction(page, `duplicate:${third}`, 'Сверить…');
  await reason.fill('Проверена третья поставка');
  await confirm.click();
  await compare.waitFor({ state: 'detached' });
  await status.getByRole('button', { name: 'Закрыть', exact: true }).click();
  await enterPayment.first().waitFor();
}
