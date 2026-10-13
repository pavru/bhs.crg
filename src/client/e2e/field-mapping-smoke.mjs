// Smoke экрана «Сопоставление полей» (issue #1101).
//
// Экран показывает, что модуль счетов читает из справочников ядра, и назначает поле — правкой схемы
// типа, тем же адресом, что редактор типов. Проверяется главное: «назначено» на экране совпадает с
// тем, что отдаёт сервер, а выбор другого поля ДЕЙСТВИТЕЛЬНО переносит тэг — и обратно.
//
// ⚠️ Прогон ПИШЕТ: переносит тэг «ИНН организации» на другое поле и возвращает на место. На время
// между двумя сохранениями поиск поставщика по ИНН смотрит в другое поле. Возврат продублирован в
// `finally` запросом к приложению — на случай, если прогон упал посередине.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и справочника организаций с ролью (посев его
// заводит). Учётка — админская.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/field-mapping-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, until } from './harness.mjs';

const TAX_ID = 'org.inn';
const ORG = 'role.organization';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, skip, summarize } = createChecks();

await login(page);

/** Строка «ИНН организации» модуля счетов — как её отдаёт сервер. */
async function taxIdRow() {
  const { status, body } = await callApi(page, 'GET', '/field-mapping');
  if (status !== 200) throw new Error(`адрес сопоставления ответил ${status}`);
  const costs = body.modules.find(m => m.code === 'costs');
  if (!costs) throw new Error('модуля счетов в ответе нет — он выключен?');
  const group = costs.groups.find(g => g.role === ORG);
  const row = group?.rows.find(r => r.tag === TAX_ID);
  if (!row) throw new Error(`справочник организаций не назначен (${group?.state}) — проверять не на чем`);
  return { costs, group, row };
}

/** Поставить тэг ИНН на поле запросом к приложению — возврат на место, если экран не справился. */
async function placeTaxId(field) {
  const type = (await callApi(page, 'GET', `/document-types/${field.declaredIn.id}`)).body;
  const schema = type.schema;
  schema.fields = schema.fields.map(f => {
    const tags = (f.tags ?? []).filter(t => t !== TAX_ID);
    return { ...f, tags: f.key === field.key ? [...tags, TAX_ID] : tags };
  });
  await callApi(page, 'PUT', `/document-types/${type.id}/schema`, { schema: JSON.stringify(schema) });
}

const start = await taxIdRow();
const original = start.row.fields.find(f => f.key === start.row.key);
// Другое подходящее поле ТОГО ЖЕ типа: перенос между типами — отдельный путь, здесь не он.
const other = original && start.row.fields.find(f =>
  f.fits && !f.tagged && f.declaredIn.id === original.declaredIn.id);

const tableRow = page.locator(`[data-role="${ORG}"] [data-tag="${TAX_ID}"]`);
const select = tableRow.getByRole('combobox');

async function pick(field) {
  await select.click();
  await page.getByRole('option').filter({ hasText: field.title }).first().click();
  await settled(page);
}

async function save() {
  await page.locator(`[data-role="${ORG}"]`).getByRole('button', { name: 'Сохранить' }).click();
  await settled(page);
}

try {

await page.goto(`${BASE}/field-mapping`);
await settled(page);

await check('screen-shows-what-server-resolves', async () => {
  if (!original) throw new Error(`ИНН не назначен (${start.row.state}) — посев обязан заводить справочник с тэгами`);
  const text = await page.locator('body').innerText();
  for (const expected of ['Сопоставление полей', start.group.label, `тип «${start.group.type.name}»`,
    'ИНН организации', 'Название организации']) {
    if (!text.includes(expected)) throw new Error(`на экране нет «${expected}»`);
  }
  const shown = (await select.innerText()).trim();
  if (!shown.startsWith(original.title)) throw new Error(`в списке стоит «${shown}», а сервер назвал «${original.title}»`);
  // У назначенной строки состояние пусто и сохранять нечего — кнопки нет вовсе.
  if (await page.getByRole('button', { name: 'Сохранить' }).count())
    throw new Error('кнопка «Сохранить» видна без единой правки');
});

if (!original || !other) {
  skip('pick-moves-the-tag-and-back', 'в справочнике организаций нет второго поля, подходящего для ИНН');
} else {
  await check('pick-moves-the-tag-and-back', async () => {
    await pick(other);
    if (!(await tableRow.innerText()).includes('не сохранено')) throw new Error('правка не помечена «не сохранено»');
    await save();
    await until(async () => {
      const now = (await taxIdRow()).row;
      if (now.key !== other.key) throw new Error(`тэг не переехал: сервер называет «${now.key}»`);
    });

    await pick(original);
    await save();
    await until(async () => {
      const now = (await taxIdRow()).row;
      if (now.key !== original.key || now.state !== 'mapped')
        throw new Error(`тэг не вернулся: «${now.key}», состояние ${now.state}`);
    });
    if (await page.getByRole('button', { name: 'Сохранить' }).count())
      throw new Error('после сохранения кнопка «Сохранить» осталась');
  });
}

await check('settings-carry-the-same-summary', async () => {
  const { costs } = await taxIdRow();
  await page.goto(`${BASE}/settings`);
  await settled(page);
  const section = page.locator('button').filter({ hasText: 'Счета и накладные' }).first();
  if ((await section.getAttribute('aria-expanded')) !== 'true') { await section.click(); await settled(page); }
  const signal = (await page.locator('[data-mapping-signal]').innerText()).trim();
  const expected = costs.unmet === 0
    ? `назначены все ${costs.total}`
    : `не назначено ${costs.unmet} из ${costs.total}`;
  if (!signal.includes(expected)) throw new Error(`в настройках «${signal}», а экран считает «${expected}»`);
  // Ссылка ведёт на экран — у администратора право на типы есть.
  await page.locator('[data-mapping-signal]').getByRole('button', { name: 'Сопоставление полей' }).click();
  await settled(page);
  if (!page.url().endsWith('/field-mapping')) throw new Error(`ссылка увела на ${page.url()}`);
});

// ─── Дальше — на ПОДПРАВЛЕННОМ ответе адреса `/api/field-mapping`; в базу ничего не пишется ───
//
// Состояний, которые держат эти проверки, на стенде нет (посев заводит справочник с тэгами), а
// ломать ради них назначение справочника незачем. Ответ берётся НАСТОЯЩИЙ и правится — тот же
// приём, что `withAnswers` в table-view-smoke: написанная руками копия контракта разошлась бы с
// формой, которую сервер отдаёт на самом деле, и проверка либо краснела бы не по делу, либо
// зеленела на форме, которой больше нет. Правка — переменная: проверка меняет её по ходу, изображая
// коллегу, который тем временем правит тип.
//
// ⚠️ После такой проверки экран остаётся на подправленном ответе — следующая обязана начинаться
// с `goto`.
let change = body => body;
// Адрес отвечает отказом — так выглядит перечитывание, попавшее на перезапуск сервера.
let failing = false;
async function withChangedAnswers(run) {
  const pattern = '**/api/field-mapping';
  await page.route(pattern, async route => {
    if (failing)
      return route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"стенд перезапускается"}' });
    const response = await route.fetch();
    if (!response.ok()) return route.fulfill({ response });
    await route.fulfill({ response, json: change(await response.json()) });
  });
  try { await run(); } finally {
    await page.unroute(pattern);
    change = body => body;
    failing = false;
  }
}

/** Правка ответа: группу организаций модуля счетов заменить на то, что вернёт `make`. */
const orgGroups = make => body => ({
  ...body,
  modules: body.modules.map(m => m.code !== 'costs' ? m : {
    ...m, groups: m.groups.flatMap(g => g.role === ORG ? make(g) : [g]),
  }),
});

/** Группа, в которой ИНН «не назначен»: тэг не стоит ни на одном поле. `fields` — что ещё сделать
 *  с полями строки. */
const unassigned = (group, fields = all => all) => ({
  ...group,
  rows: group.rows.map(r => r.tag !== TAX_ID ? r : {
    ...r, state: 'absent', fault: 'Absent', key: null,
    reason: 'в справочнике не назначено поле «ИНН организации»',
    fields: fields(r.fields.map(f => ({ ...f, tagged: false }))),
  }),
});

// Вторая группа того же вида под своей ролью. Её «сохранение» ничего не пишет — в базе тэг уже
// стоит на выбранном поле, и запись видит, что менять нечего, — но экран после него перечитывается.
// Так проверка получает перечитывание посреди чужой несохранённой правки без единой записи.
const TWIN = 'role.e2e-twin';
const twin = group => ({ ...unassigned(group), role: TWIN, label: 'Двойник для прогона' });

async function rereadByTwin() {
  const twinGroup = page.locator(`[data-role="${TWIN}"]`);
  await twinGroup.locator(`[data-tag="${TAX_ID}"]`).getByRole('combobox').click();
  await page.getByRole('option').filter({ hasText: original.title }).first().click();
  // Вместе, а не «сначала подписаться, потом нажать»: не нажмись кнопка, оставшееся без присмотра
  // ожидание ответа истекло бы позже необработанным отказом — и уронило бы весь набор, а не одну
  // проверку.
  await Promise.all([
    page.waitForResponse(r => r.url().endsWith('/api/field-mapping')),
    twinGroup.getByRole('button', { name: 'Сохранить' }).click(),
  ]);
  await settled(page);
}

/** Записи в схемы типов, пока идёт `run`: проверки на подправленном ответе писать не должны. */
async function withoutWrites(run) {
  const wrote = [];
  const onRequest = r => {
    if (r.method() !== 'GET' && r.url().includes('/api/document-types/')) wrote.push(`${r.method()} ${r.url()}`);
  };
  page.on('request', onRequest);
  try { await run(); } finally { page.off('request', onRequest); }
  if (wrote.length) throw new Error(`проверка обещала не писать, а в схему ушло: ${wrote.join(', ')}`);
}

const orgGroup = page.locator(`[data-role="${ORG}"]`);
const shown = async () => (await select.innerText()).trim();
const offersSave = async () => (await orgGroup.getByRole('button', { name: 'Сохранить' }).count()) > 0;

// Поведение списка, которого тест логики не видит: после «Отменить» список обязан вернуться к
// пустому, и тот же пункт — выбираться заново. С `undefined` вместо пустой строки Radix помнил
// отменённый выбор сам и на повтор не отвечал; держит это общая обёртка списка (`shared/ui/Select`).
if (!original) {
  skip('cancelled-pick-can-be-picked-again', 'ИНН на стенде не назначен — поле для выбора взять неоткуда');
  skip('dropped-pick-is-said-and-stays-dropped', 'ИНН на стенде не назначен — поле для выбора взять неоткуда');
  skip('vanished-type-leaves-no-pick', 'ИНН на стенде не назначен — перечитать экран без записи нечем');
  skip('failed-reread-keeps-the-pick', 'ИНН на стенде не назначен — перечитать экран без записи нечем');
} else {
  await check('cancelled-pick-can-be-picked-again', async () => {
    change = orgGroups(g => [unassigned(g)]);
    await withChangedAnswers(async () => {
      await page.goto(`${BASE}/field-mapping`);
      await settled(page);

      if (!(await shown()).startsWith('Выберите поле')) throw new Error(`у неназначенной строки в списке «${await shown()}»`);
      await pick(original);
      if (!(await shown()).startsWith(original.title)) throw new Error(`после выбора в списке «${await shown()}»`);

      await orgGroup.getByRole('button', { name: 'Отменить' }).click();
      await settled(page);
      if (!(await shown()).startsWith('Выберите поле')) throw new Error(`после «Отменить» в списке осталось «${await shown()}»`);
      if (await offersSave()) throw new Error('после «Отменить» кнопка «Сохранить» осталась');

      await pick(original);
      if (!(await offersSave())) throw new Error('тот же пункт после «Отменить» не выбирается: кнопки «Сохранить» нет');
      // Уходим без сохранения — иначе охрана ухода остановила бы закрытие страницы вопросом.
      await orgGroup.getByRole('button', { name: 'Отменить' }).click();
    });
  });

  // Выбор, который список больше не предлагает, экран снимает САМ — и обязан сказать об этом:
  // пометка «не сохранено» и кнопки иначе пропали бы без причины. И снимает насовсем: вернись поле
  // в список, строка не должна стать «не сохранено» с выбором, которого человек уже не делал.
  await check('dropped-pick-is-said-and-stays-dropped', async () => {
    const both = fields => orgGroups(g => [unassigned(g, fields), twin(g)]);
    change = both();
    await withChangedAnswers(() => withoutWrites(async () => {
      await page.goto(`${BASE}/field-mapping`);
      await settled(page);
      await pick(original);
      if (!(await offersSave())) throw new Error('после выбора поля кнопки «Сохранить» нет — проверять нечего');

      // Коллега сделал поле расчётным: в типе оно осталось, но выбрать его уже нельзя.
      change = both(all => all.map(f => f.key === original.key ? { ...f, fits: false } : f));
      await rereadByTwin();
      const text = await orgGroup.innerText();
      if (!text.includes('Выбор для «ИНН организации» отменён'))
        throw new Error('выбор снят молча: под группой нет ни слова о том, что он отменён');
      if (text.includes('не сохранено')) throw new Error('строка осталась «не сохранено» с выбором, которого список не предлагает');
      if (await offersSave()) throw new Error('кнопка «Сохранить» осталась у группы без действительного выбора');

      // Поле вернули таким, каким оно было.
      change = both();
      await rereadByTwin();
      if ((await offersSave()) || (await orgGroup.innerText()).includes('не сохранено'))
        throw new Error('снятый экраном выбор ожил, когда поле вернулось в список');
      if (!(await shown()).startsWith('Выберите поле')) throw new Error(`в списке стоит «${await shown()}», а выбор снят`);

      await orgGroup.getByRole('button', { name: 'Понятно' }).click();
      if ((await orgGroup.innerText()).includes('отменён')) throw new Error('прочитанное сообщение не убирается');
    }));
  });

  // То же у выбора типа справочника: тип, ушедший из предлагаемых, не остаётся выбранным, не
  // оживает с возвращением и выбирается заново.
  await check('vanished-type-leaves-no-pick', async () => {
    const type = start.group.type;
    const second = { ...type, id: '00000000-0000-0000-0000-0000000e2e00', name: 'Второй тип прогона' };
    const absent = candidates => orgGroups(g => [
      { ...g, state: 'absent', reason: 'справочник не назначен', type: null, rows: [], candidates },
      twin(g),
    ]);
    change = absent([type, second]);
    await withChangedAnswers(() => withoutWrites(async () => {
      await page.goto(`${BASE}/field-mapping`);
      await settled(page);
      const types = orgGroup.getByRole('combobox');
      const shownType = async () => (await types.innerText()).trim();
      const offersAssign = async () => (await orgGroup.getByRole('button', { name: 'Назначить справочником' }).count()) > 0;
      const pickType = async () => {
        await types.click();
        await page.getByRole('option', { name: type.name, exact: true }).click();
        await settled(page);
      };

      if (!(await shownType()).startsWith('Выберите тип')) throw new Error(`до выбора в списке типов «${await shownType()}»`);
      await pickType();
      if (!(await offersAssign())) throw new Error('после выбора типа кнопки «Назначить справочником» нет — проверять нечего');

      // Тип тем временем назначили другим справочником — из предлагаемых он ушёл.
      change = absent([second]);
      await rereadByTwin();
      if (!(await shownType()).startsWith('Выберите тип')) throw new Error(`тип ушёл из списка, а в нём осталось «${await shownType()}»`);
      if (await offersAssign()) throw new Error('кнопка «Назначить справочником» осталась для типа, которого в списке нет');

      change = absent([type, second]);
      await rereadByTwin();
      if (!(await shownType()).startsWith('Выберите тип') || (await offersAssign()))
        throw new Error('снятый выбор типа ожил, когда тип вернулся в список');
      await pickType();
      if (!(await offersAssign())) throw new Error('вернувшийся тип не выбирается заново: кнопки «Назначить справочником» нет');
    }));
  });

  // Перечитывание упало: сервер перезапускают, сеть моргнула. Экран с несохранённым выбором
  // обязан остаться и сказать об отказе полосой. Раньше он сносил сам себя — вместе с выбором и
  // со строкой отказа сохранения, — потому что упавший перезапрос оставляет прежний ответ, а
  // состояние запроса уже «ошибка».
  await check('failed-reread-keeps-the-pick', async () => {
    change = orgGroups(g => [unassigned(g), twin(g)]);
    await withChangedAnswers(() => withoutWrites(async () => {
      await page.goto(`${BASE}/field-mapping`);
      await settled(page);
      await pick(original);
      const kept = async () => (await offersSave()) && (await tableRow.innerText()).includes('не сохранено');
      if (!(await kept())) throw new Error('после выбора поля строка не помечена — проверять нечего');

      failing = true;
      await rereadByTwin();
      const strip = page.getByTestId('reread-failed');
      // Запрос повторяется один раз, через секунду, — полоса появляется после второго отказа.
      await strip.waitFor({ timeout: 15000 })
        .catch(() => { throw new Error('перечитывание упало, а полосы об этом нет: экран промолчал либо снёс сам себя'); });
      if (!(await kept())) throw new Error('упавшее перечитывание стёрло несохранённый выбор');

      failing = false;
      await strip.getByRole('button', { name: 'Повторить' }).click();
      await strip.waitFor({ state: 'detached', timeout: 15000 })
        .catch(() => { throw new Error('после удачного повтора полоса осталась'); });
      if (!(await kept())) throw new Error('после удачного повтора несохранённый выбор пропал');
      // Уходим без сохранения — иначе охрана ухода остановила бы закрытие страницы вопросом.
      await orgGroup.getByRole('button', { name: 'Отменить' }).click();
    }));
  });
}

} finally {
  // Упали между двумя сохранениями — тэг остался бы на чужом поле, и у соседних прогонов
  // перестал бы находиться поставщик по ИНН.
  if (original) {
    const now = await taxIdRow().catch(() => null);
    if (now && now.row.key !== original.key) {
      console.log('  ! тэг ИНН не на своём поле — возвращаю запросом');
      await placeTaxId(original);
    }
  }
  await browser.close();
}

process.exitCode = summarize('Smoke сопоставления полей');
