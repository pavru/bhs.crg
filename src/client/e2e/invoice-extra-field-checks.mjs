// Поле, дописанное в тип «Счёт», видно и правится в форме счёта (issue #1482).
//
// Тип расширяемый: администратор дописывает свои необязательные поля, сервер их хранит. Форма раньше их
// не показывала — значение, положенное распознаванием, было невидимо. Проверяется ролью снабженца, а не
// администратора: схему типа форма читает правом `core.types.read`, и именно у рабочей роли его отказ
// спрятал бы поле. Дописанное поле прогон убирает за собой.

import { login, callApi } from './harness.mjs';

const INVOICE_TYPE = 'СчётНаОплату';
const KEY = 'ЦентрЗатратПрогона';
const TITLE = 'Центр затрат (прогон)';

async function ok(page, method, path, body) {
  const answer = await callApi(page, method, path, body);
  if (answer.status >= 400) throw new Error(`${method} ${path} → ${answer.status} ${String(answer.body).slice(0, 200)}`);
  return answer.body;
}

/** Схема типа «Счёт» без поля прогона и с ним — правит администратор. */
async function placeField(admin, present) {
  const listed = (await ok(admin, 'GET', '/document-types')).find(t => t.code === INVOICE_TYPE);
  if (!listed) throw new Error(`типа «${INVOICE_TYPE}» нет — модуль счетов не включён`);
  const type = await ok(admin, 'GET', `/document-types/${listed.id}`);
  const schema = typeof type.schema === 'string' ? JSON.parse(type.schema) : type.schema;
  const others = (schema.fields ?? []).filter(f => f.key !== KEY);
  schema.fields = present ? [...others, { key: KEY, title: TITLE, type: 'string', required: false }] : others;
  await ok(admin, 'PUT', `/document-types/${type.id}/schema`, { schema: JSON.stringify(schema) });
  // Своих полей у заказчика до прогона не было — тогда и блока «Дополнительно» не было.
  return others.every(f => f.origin === 'module');
}

/**
 * @param seat снабженец: `{ page }` с открытым модулем; `open(id)` открывает счёт.
 */
export async function extraFieldChecks({ browser, check, skip, seat, invoiceId, open }) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
  const admin = await context.newPage();
  try {
    await login(admin);
    const page = seat.page;
    const section = () => page.locator('section').filter({ has: page.getByRole('heading', { name: 'Дополнительно', exact: true }) });

    let clean = false;
    await check('invoice-type-schema-is-editable', async () => { clean = await placeField(admin, false); });
    // Заказчик уже дописал поля — сравнивать «как раньше» не с чем. Пропуск ЗАЯВЛЕН, а не зелёная галка
    // без проверки (ревью PR #1506).
    if (!clean) skip('invoice-without-extra-fields-has-no-extra-block', 'в типе «Счёт» уже есть поля заказчика');
    else await check('invoice-without-extra-fields-has-no-extra-block', async () => {
      await open(invoiceId);
      if (await section().count()) throw new Error('блок «Дополнительно» есть у счёта, хотя в тип ничего не дописано');
    });

    await check('invoice-shows-and-saves-a-field-added-to-the-type', async () => {
      await placeField(admin, true);
      await open(invoiceId);
      const input = section().getByLabel(TITLE);
      await input.waitFor({ timeout: 10_000 }).catch(() => { throw new Error('дописанного в тип поля нет в форме счёта'); });
      await input.fill('ЦЗ-7');
      await Promise.all([
        page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith(`/costs/invoices/${invoiceId}`) && r.ok()),
        page.getByRole('button', { name: 'Сохранить', exact: true }).click(),
      ]);
      const stored = (await ok(page, 'GET', `/costs/invoices/${invoiceId}`)).requisites[KEY];
      if (stored !== 'ЦЗ-7') throw new Error(`в реквизитах счёта «${stored}», ждали «ЦЗ-7»`);
      await open(invoiceId);
      const shown = await section().getByLabel(TITLE).inputValue();
      if (shown !== 'ЦЗ-7') throw new Error(`после перечитывания в поле «${shown}»`);
    });
  } finally {
    await placeField(admin, false).catch(e => console.log('  ! поле прогона из типа не убрано:', e.message));
    await context.close();
  }
}
