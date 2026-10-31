// Smoke по экранам, где выбор зависел от эффекта (issue #858, порция 5 — последняя по правилу).
//
// Здесь эффекты не переливали форму, а вели ВЫБОР: сбрасывали выбранный шаблон при смене типа,
// раскрывали группу выбранного, открывали документ по адресу (с #1384 — страница), разбирали `?view=` на
// странице сверки, подставляли первого кандидата в диалоге источника. Всё это стало вычислением,
// и ломается оно тихо: не то открыто, не то подставлено, выбор от прошлого типа.
//
// Ничего не сохраняет.
//
// Требует поднятых фронта (:5173) и бэка (:5000) и демо-данных — см. e2e/README.md.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/pages-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, closeDialog, emulateTouch } from './harness.mjs';

const SET = process.env.SMOKE_SET_ID || 'e9d618fb-1035-4938-96a1-ffca6c857dc1';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID || '66b75946-5954-4505-a7e8-535b868bff6f';
const AOSR_INSTANCE = process.env.SMOKE_INSTANCE_ID || 'b1de57a0-6c14-4bbc-9cad-1dda592c9c66';
const SYSTEM_DATASET = 'Данные системы';
/** Тип документа, у которого шаблонов НЕТ, — на нём и видно, сбросился ли выбор от прежнего типа. */
const EMPTY_TYPE = process.env.SMOKE_EMPTY_TYPE || 'Приказ';

/**
 * Строка → безопасный образец regexp. Имя типа может содержать скобки («АОСР (форма 3)»), и без
 * экранирования `new RegExp` либо падает, либо ищет не то. Пустая строка сюда попадать не должна
 * вовсе — проверка ниже об этом и говорит (поймано ревью PR #865).
 */
const rx = (text) => new RegExp(text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

try {

// ── Шаблоны: выбор привязан к типу документа ──────────────────────────────────
await page.goto(`${BASE}/templates`);
await settled(page);

let firstTypeName = '';
await check('templates-auto-select-after-type-pick', async () => {
  const selector = page.locator('button').filter({ hasText: /Тип документа|Выберите тип/ }).first();
  if (!(await selector.count())) throw new Error('селектора типа на странице шаблонов нет');
  await selector.click();
  await settled(page);
  const picker = page.locator('[role=dialog]').last();
  const option = picker.locator('button').filter({ hasText: /АОСР|Кабельный журнал|Титульный/ }).first();
  if (!(await option.count())) throw new Error('в пикере типов нет знакомых типов');
  firstTypeName = (await option.innerText()).split(/\r?\n/)[0].trim();
  await option.click();
  await settled(page);
  const t = await page.locator('body').innerText();
  if (/Выберите тип документа/.test(t)) throw new Error('тип не выбрался');
  // Шаблон выбирается сам: без этого справа пусто, а слева список — то есть экран «ничего не открыто».
  if (!/версия|Активн|Черновик|Сохранить/i.test(t))
    throw new Error(`шаблон не выбрался сам: ${t.slice(-300)}`);
});

// Половины split-кнопки при касании ([UI-68]): зазор 2 px, и цель каждой расширена только по вертикали —
// касание у самого края основной части достаётся ей, а не стрелке.
await check('split-button-halves-keep-their-edges-on-touch', async () => {
  const main = page.getByRole('button', { name: /^Сохранить/ }).first();
  // Нет кнопки — проверка не сработала, а не прошла: выбранный сам шаблон должен быть активным.
  if (!(await main.count())) throw new Error('у выбранного шаблона нет split-кнопки «Сохранить» — шаблон не активен?');
  await emulateTouch(page);
  try {
    const ok = await main.evaluate(el => {
      const box = el.getBoundingClientRect();
      return document.elementFromPoint(box.right - 2, box.top + box.height / 2)?.closest('button') === el;
    });
    if (!ok) throw new Error('касание в 2 px внутри края основной части досталось не ей');
  } finally {
    await emulateTouch(page, false);
  }
});

/**
 * Переключение на тип БЕЗ шаблонов: справа обязано быть приглашение, а не редактор.
 *
 * <p>Честно про силу этой проверки: сломанную привязку выбора к типу она НЕ ловит. Снимите метку
 * типа — она всё равно зелёная, потому что обработчик смены типа сбрасывает выбор и сам, а другого
 * пути сменить тип, не размонтировав страницу, у человека нет. Проверено. То есть она сторожит
 * экран, а не замену эффекта; замена проверена чтением.</p>
 */
await check('templates-selection-does-not-leak-to-other-type', async () => {
  if (!firstTypeName) throw new Error('тип не выбран предыдущей проверкой — проверять нечего');
  const selector = page.locator('button').filter({ hasText: rx(firstTypeName.slice(0, 12)) }).first();
  await selector.click();
  await settled(page);
  const picker = page.locator('[role=dialog]').last();
  const empty = picker.locator('button').filter({ hasText: rx(EMPTY_TYPE) }).first();
  if (!(await empty.count())) throw new Error(`типа «${EMPTY_TYPE}» в пикере нет`);
  await empty.click();
  await settled(page);
  const t = await page.locator('body').innerText();
  // Утверждение ПОЛОЖИТЕЛЬНОЕ: у типа без шаблонов правая панель обязана предлагать выбрать или
  // создать. Останься там шаблон прежнего типа — вместо приглашения был бы редактор.
  if (!/Выберите шаблон или создайте новый/.test(t))
    throw new Error(`у типа без шаблонов справа не приглашение, а что-то другое: ${t.slice(-300)}`);
});

// ── Документ — страница со своим адресом (issue #1384) ─────────────────────────
const DOC_PATH = `/document-sets/${CONSTRUCTION}/sets/${SET}/docs/${AOSR_INSTANCE}`;
const editorText = async () => {
  const editor = page.getByTestId('document-editor');
  await editor.waitFor({ timeout: 20000 }).catch(() => { throw new Error('страница документа не открылась'); });
  await settled(page);
  return editor.innerText();
};

await check('set-detail-old-doc-link-opens-document-page', async () => {
  // Старый адрес `?doc=` (закладки, письма) ведёт на страницу документа, заменяя себя в истории.
  await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}?doc=${AOSR_INSTANCE}`);
  const t = await editorText();
  if (!/АОСР/.test(t)) throw new Error(`открылось не то: ${t.slice(0, 200)}`);
  if (new URL(page.url()).pathname !== DOC_PATH) throw new Error(`адрес не стал адресом документа: ${page.url()}`);
});

await check('document-page-survives-reload', async () => {
  await page.reload();
  if (!/АОСР/.test(await editorText())) throw new Error('после обновления открылось не то');
});

await check('document-page-back-returns-to-set', async () => {
  await page.getByTestId('document-editor').getByRole('button', { name: 'К комплекту' }).click();
  // Признак комплекта — строки его документов, а не только адрес: пустой экран по верному адресу
  // утверждение об адресе прошёл бы.
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  const path = new URL(page.url()).pathname;
  if (path !== `/document-sets/${CONSTRUCTION}/sets/${SET}`) throw new Error(`вернулись не к комплекту: ${path}`);
});

// Уход из документа с правками спрашивает (issue #1384): охрана страниц, а не вопрос окна. Правка
// не сохраняется — «Не сохранять».
await check('document-page-asks-before-leaving-with-edits', async () => {
  await page.goto(`${BASE}${DOC_PATH}`);
  await editorText();
  const editor = page.getByTestId('document-editor');
  await editor.getByRole('button', { name: /Прочее/ }).first().click();
  const input = editor.getByLabel('Количество экземпляров');
  await input.fill(String(Number(await input.inputValue() || 0) + 7));
  // Охрана включается эффектом после ввода — ждём её признака на вкладке, иначе уход опередит её.
  await editor.locator('[aria-description="Есть несохранённые изменения"]').waitFor({ timeout: 5000 });

  const back = editor.getByRole('button', { name: 'К комплекту' });
  const ask = page.getByRole('dialog', { name: 'Несохранённые изменения' });
  await back.click();
  await ask.waitFor({ timeout: 5000 }).catch(() => { throw new Error('ушли из документа с правками без вопроса'); });
  await ask.getByRole('button', { name: 'Отмена' }).click();
  await ask.waitFor({ state: 'detached' });
  if (new URL(page.url()).pathname !== DOC_PATH) throw new Error(`«Отмена» увела из документа: ${page.url()}`);

  await back.click();
  await ask.getByRole('button', { name: 'Не сохранять' }).click();
  await page.waitForSelector('tbody tr', { timeout: 15000 });
});

// «Назад» браузера с правками тоже спрашивает (ревью PR #1388): окно редактора переживало его,
// страница ушла бы молча. Документ открыт щелчком в комплекте: «Назад» к записи от ДРУГОЙ загрузки
// страницы (`page.goto`) — уход из приложения, его сторожит `beforeunload`, а не охрана шага.
await check('document-page-browser-back-asks-with-edits', async () => {
  await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  await page.locator('tbody tr').filter({ hasText: /АОСР/ }).first().click();
  await editorText();
  if (new URL(page.url()).pathname !== DOC_PATH) throw new Error(`открылся не тот документ: ${page.url()}`);
  const editor = page.getByTestId('document-editor');
  await editor.getByRole('button', { name: /Прочее/ }).first().click();
  const input = editor.getByLabel('Количество экземпляров');
  await input.fill(String(Number(await input.inputValue() || 0) + 7));
  // Охрана включается эффектом после ввода — ждём её признака на вкладке, иначе уход опередит её.
  await editor.locator('[aria-description="Есть несохранённые изменения"]').waitFor({ timeout: 5000 });

  const ask = page.getByRole('dialog', { name: 'Несохранённые изменения' });
  await page.evaluate(() => history.back());
  await ask.waitFor({ timeout: 5000 }).catch(() => { throw new Error('«Назад» увёл из документа с правками без вопроса'); });
  if (new URL(page.url()).pathname !== DOC_PATH) throw new Error(`адрес ушёл с документа под вопросом: ${page.url()}`);
  await ask.getByRole('button', { name: 'Отмена' }).click();
  await ask.waitFor({ state: 'detached' });
  if (!(await input.inputValue())) throw new Error('после «Отмены» правка пропала');

  await page.evaluate(() => history.back());
  await ask.waitFor({ timeout: 5000 }).catch(() => { throw new Error('второй «Назад» после «Отмены» ушёл без вопроса'); });
  await ask.getByRole('button', { name: 'Не сохранять' }).click();
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  const path = new URL(page.url()).pathname;
  if (path !== `/document-sets/${CONSTRUCTION}/sets/${SET}`) throw new Error(`«Назад» увёл не к комплекту: ${path}`);
});

// То же на телефоне (#1418): там окна кладут записи в историю, а вопрос ухода, заданный на шаге
// истории, — нет. Иначе «Не сохранять» ушло бы на его же запись, а не к комплекту; «Назад» при вопросе —
// снова шаг под охраной, вопрос остаётся. Поймано живым прогоном: StrictMode монтирует вопрос дважды.
await check('document-page-browser-back-asks-on-phone', async () => {
  const desktop = page.viewportSize();
  try {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
    await page.locator('tbody tr, [data-testid=ld-nav] button').filter({ hasText: /АОСР/ }).first().click();
    await page.waitForURL(url => url.pathname === DOC_PATH, { timeout: 15000 });
    const editor = page.getByTestId('document-editor');
    // Первое поле ввода текста: строковое поле с подсказкой по введённому ранее (#1533) — поле со
    // списком (роль combobox, а не textbox), поэтому ищутся обе роли; кнопка-список под `input` не попадёт.
    const input = editor.getByRole('textbox').or(editor.locator('input[role="combobox"]')).first();
    await input.fill(`${await input.inputValue()} 7`);
    await editor.locator('[aria-description="Есть несохранённые изменения"]').waitFor({ timeout: 5000 });

    const ask = page.getByRole('dialog', { name: 'Несохранённые изменения' });
    await page.goBack();
    await ask.waitFor({ timeout: 5000 }).catch(() => { throw new Error('«Назад» увёл из документа с правками без вопроса'); });
    if (await page.evaluate(() => history.state?.layer)) throw new Error('вопрос ухода положил запись в историю');
    await page.goBack();
    await page.waitForTimeout(300);
    if (!(await ask.isVisible())) throw new Error('«Назад» при вопросе ухода убрала вопрос');
    if (new URL(page.url()).pathname !== DOC_PATH) throw new Error(`«Назад» при вопросе увела: ${page.url()}`);
    await ask.getByRole('button', { name: 'Не сохранять' }).click();
    await page.waitForURL(url => url.pathname === `/document-sets/${CONSTRUCTION}/sets/${SET}`, { timeout: 5000 })
      .catch(() => { throw new Error(`«Не сохранять» увело не к комплекту: ${page.url()}`); });
  } finally {
    if (desktop) await page.setViewportSize(desktop);
  }
});

// Пришли из комплекта — «К комплекту» шагает назад по истории, а не кладёт комплект новой записью:
// иначе «Назад» с комплекта снова открывал бы документ (ревью PR #1388). Признак шага — «Вперёд»
// возвращает документ.
await check('document-page-from-set-steps-back', async () => {
  await page.goto(`${BASE}/document-sets/${CONSTRUCTION}/sets/${SET}`);
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  await page.locator('tbody tr').filter({ hasText: /АОСР/ }).first().click();
  await editorText();
  const length = await page.evaluate(() => history.length);
  await page.getByTestId('document-editor').getByRole('button', { name: 'К комплекту' }).click();
  await page.waitForSelector('tbody tr', { timeout: 15000 });
  if (await page.evaluate(() => history.length) !== length) throw new Error('выход положил комплект новой записью');
  await page.evaluate(() => history.forward());
  await editorText();
});

// Источники данных — вкладка редактора, а не окно над страницей (issue #1384): «Из шаблона» над ней —
// окно ПЕРВОГО уровня. Ломается: вернуть кнопку с окном в шапку — вкладки нет, а шаблон открывался бы
// окном над окном.
await check('document-sources-is-an-editor-tab', async () => {
  await page.goto(`${BASE}${DOC_PATH}`);
  await editorText();
  const editor = page.getByTestId('document-editor');
  if (await editor.getByRole('button', { name: /^Источники/ }).count())
    throw new Error('в шапке документа осталась кнопка «Источники» — источники обязаны быть вкладкой');
  await editor.getByRole('tab', { name: /^Источники данных/ }).click();
  const panel = editor.getByRole('tabpanel', { name: /^Источники данных/ });
  await panel.getByRole('button', { name: 'Из шаблона' }).waitFor({ timeout: 10000 })
    .catch(() => { throw new Error('вкладка «Источники данных» не показала свои действия'); });
  if (await page.getByRole('dialog').count()) throw new Error('вкладка источников открыла окно');
  await panel.getByRole('button', { name: 'Из шаблона' }).click();
  await page.getByRole('dialog').first().waitFor({ timeout: 5000 });
  const dialogs = await page.getByRole('dialog').count();
  await page.keyboard.press('Escape');
  await settled(page);
  if (dialogs !== 1) throw new Error(`«Из шаблона» открылось ${dialogs}-м окном, а должно — единственным`);
});

// ── Сверка: секция из адреса ──────────────────────────────────────────────────
await check('reconciliations-view-param-opens-section', async () => {
  await page.goto(`${BASE}/reconciliations?view=aliases`);
  await settled(page);
  const t = await page.locator('body').innerText();
  if (!/Алиас|алиас/i.test(t)) throw new Error(`секция алиасов не открылась: ${t.slice(-300)}`);
});

// ── Учётный период: границы и диалог закрытия ─────────────────────────────────
// Закрытие НЕ подтверждаем: запись о нём не удаляется, а база прогонов общая на все наборы —
// закрытый здесь период остался бы соседям. Сама запись проверена на сервере (PeriodClosureTests);
// здесь — что экран дочитал границы и диалог предлагает период, а не пустые поля.
await check('periods-page-offers-a-period-to-close', async () => {
  await page.goto(`${BASE}/periods`);
  await settled(page);
  const body = await page.locator('main').innerText();
  if (!/Компания/.test(body)) throw new Error(`строки компании нет: ${body.slice(0, 300)}`);
  if (!/Сегодня по часам компании — \d{2}\.\d{2}\.\d{4}/.test(body))
    throw new Error('экран не назвал «сегодня» — границы с сервера не пришли');

  await page.locator('button').filter({ hasText: /Закрыть период/ }).first().click();
  const dialog = page.locator('[role=dialog]').first();
  await dialog.waitFor({ timeout: 10000 });
  const text = await dialog.innerText();
  if (!/Закрыть период: Компания/.test(text)) throw new Error(`диалог не про компанию: ${text.slice(0, 200)}`);
  if (!/Будет закрыто всё по \d{2}\.\d{2}\.\d{4} включительно/.test(text))
    throw new Error(`диалог не предложил конец периода: ${text.slice(0, 300)}`);
  await page.keyboard.press('Escape');
});

// ── Диалог источника: подставлен первый кандидат ──────────────────────────────
// Набор системных данных выбран за то, что у него ЕСТЬ свободные кандидаты: у демо-PDF все
// проекции уже добавлены источниками, и подставлять диалогу нечего — проверка была бы пустой.
await check('source-editor-prefills-first-candidate', async () => {
  await page.goto(`${BASE}/datasets`);
  await settled(page);
  await page.getByText(SYSTEM_DATASET, { exact: false }).first().click();
  await settled(page);
  await page.locator('button').filter({ hasText: /Добавить источник/ }).first().click();
  await settled(page);
  const dlg = page.locator('[role=dialog]').last();
  const text = await dlg.innerText();
  if (/— выберите —/.test(text)) throw new Error('кандидат не подставлен — список стоит на плейсхолдере');
  const value = await dlg.locator('input').first().inputValue();
  if (!value.trim()) throw new Error('имя источника не подставлено — поле пусто');
  // Имя подставляется от ПЕРВОГО показанного кандидата: оно обязано его называть.
  if (!text.includes(value.replace(/ \(\d+\)$/, '')))
    throw new Error(`имя «${value}» не отвечает ни одному кандидату в списке`);
  await page.keyboard.press('Escape');
  await settled(page);
});

// Подстановка обязана уступать человеку: стёртое поле остаётся пустым, набранное — набранным.
// Пока «наше ли имя» вычислялось по виду («пустое — значит наше»), стереть его было нельзя:
// поле тут же заполнялось обратно, и следующая буква приписывалась к подставленному.
await check('source-editor-name-can-be-cleared-and-typed', async () => {
  await page.locator('button').filter({ hasText: /Добавить источник/ }).first().click();
  await settled(page);
  const dlg = page.locator('[role=dialog]').last();
  const field = dlg.locator('input').first();
  await field.fill('');
  await settled(page);
  const cleared = await field.inputValue();
  if (cleared !== '') throw new Error(`очищенное поле заполнилось само: «${cleared}»`);
  await field.fill('Мой источник');
  await settled(page);
  const typed = await field.inputValue();
  if (typed !== 'Мой источник') throw new Error(`набранное подменено: «${typed}»`);
  await closeDialog(page);
  await settled(page);
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke экранов с выбором');
