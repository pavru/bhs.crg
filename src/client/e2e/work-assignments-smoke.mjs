// Smoke панели «Назначения» модуля «Учёт работ» (change `work-12-assignments-ui`, задачи 3.5 и 4.5, ТЗ WORK-21, WORK-46).
//
// ЗАЧЕМ ОН ЕСТЬ. Панель и окна проверены vitest на подделанных ответах; здесь — живой сервер: что кнопки рисуются
// по его признаку «можно назначать на этой стройке», что пачка заводится и видна в обоих видах панели, что пометка
// учётной записи приходит полем кандидата, а пункт панели закрыт правами роли.
//
//   1. `pm-sees-assignments-panel` — «Менеджер проекта», менеджер стройки посева: панель с его назначением
//      «без срока» и кнопкой «Назначить на период».
//   2. `pm-assigns-two-for-a-period` — окно пачки: период, двое кандидатов без учётных записей (пометка «нет
//      учётной записи» — поле сервера), «Назначить · 2» — и оба в таблице.
//   3. `pm-ribbon-shows-same-rows` — «Лента»: те же назначения полосами, полоса открывает «Изменить период».
//   4. `foreign-pm-sees-no-buttons` — второй «Менеджер проекта» без назначения на этой стройке: назначения видны,
//      кнопок правки нет в разметке, причина сервера — строкой над списком.
//   5. `installer-has-no-assignments-item` — «Монтажник»: пункта «Назначения» в карточке стройки нет.
//
// ⚠️ Данные — посев (`seed-assignments.mjs`): менеджер стройки, второй менеджер, монтажник и двое кандидатов.
// Назначения кандидатов прогон снимает и до начала, и уборкой: иначе второй запуск застал бы их «уже назначен».
//
// Требует поднятых фронта и бэка, посева и ВКЛЮЧЁННОГО модуля `work`.
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/work-assignments-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks, settled, callApi, clearSession } from './harness.mjs';
import { ASSIGNMENT_PM, CANDIDATES, FOREIGN_PM, INSTALLER } from './seed-assignments.mjs';

const PASSWORD = process.env.SMOKE_USER_PASSWORD || 'Demo12345!';
const CONSTRUCTION = process.env.SMOKE_CONSTRUCTION_ID;
if (!CONSTRUCTION) throw new Error('нет SMOKE_CONSTRUCTION_ID — запустите посев (e2e/seed.mjs) и передайте его строки');

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

const panel = () => page.goto(`${BASE}/document-sets/${CONSTRUCTION}/assignments`, { waitUntil: 'networkidle' });
const dialog = name => page.getByRole('dialog', { name });

/** Назначения стройки тем же токеном, что у страницы; модуль выключен — отказ с причиной, а не пустота. */
async function siteAssignments() {
  const r = await callApi(page, 'GET', `/work/assignments?constructionId=${CONSTRUCTION}`);
  if (r.status === 501) throw new Error('модуль work выключен — включите его (Modules__Enabled) и повторите посев');
  if (r.status >= 400) throw new Error(`назначения не прочитались: ${r.status} ${String(r.body).slice(0, 200)}`);
  return r.body;
}

/** Снять назначения кандидатов посева: прогон начинает с чистого листа и после себя его оставляет. */
async function removeCandidates() {
  const names = new Set(CANDIDATES.map(c => c.name));
  for (const a of (await siteAssignments()).items.filter(x => names.has(x.employeeName))) {
    const status = await page.evaluate(async ([url, version]) => {
      const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
      return (await fetch(url, { method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': version } })).status;
    }, [`/api/work/assignments/${a.id}`, a.version]);
    if (status >= 400) throw new Error(`назначение ${a.employeeName} не снялось: ${status}`);
  }
}

/** Дата через `days` дней от ISO-даты — цифрами для поля маски: «ДДММГГГГ». */
function typedDate(iso, days) {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  const p = n => String(n).padStart(2, '0');
  return `${p(d.getUTCDate())}${p(d.getUTCMonth() + 1)}${d.getUTCFullYear()}`;
}

try {
  // ── 1–3. Менеджер своей стройки ───────────────────────────────────────────────────────────────
  await login(page, ASSIGNMENT_PM.email, PASSWORD);
  await panel();
  await removeCandidates();
  const today = (await siteAssignments()).construction.today;

  await check('pm-sees-assignments-panel', async () => {
    await panel();
    await page.getByRole('heading', { name: 'Назначения' }).waitFor({ timeout: 15_000 });
    const row = page.getByRole('row', { name: new RegExp(ASSIGNMENT_PM.name) });
    await row.waitFor({ timeout: 10_000 });
    if (!/без срока/.test(await row.innerText())) throw new Error('у назначения менеджера нет «без срока»');
    await page.getByRole('button', { name: /Назначить на период/ }).waitFor({ timeout: 5_000 });
  });

  await check('pm-assigns-two-for-a-period', async () => {
    if (!today) throw new Error('у стройки посева нет «сегодня» — пояс не определён');
    await page.getByRole('button', { name: /Назначить на период/ }).click();
    const d = dialog('Назначить на период');
    await d.waitFor({ timeout: 5_000 });
    // «с» — сегодня стройки по умолчанию; «по» — через три недели.
    await d.getByRole('group', { name: 'Период, по', exact: true }).getByRole('textbox').first().click();
    await page.keyboard.type(typedDate(today, 21));
    for (const c of CANDIDATES) {
      const box = d.getByRole('checkbox', { name: `Отметить: ${c.name}` });
      await box.waitFor({ timeout: 10_000 });
      await box.check();
    }
    const marks = await d.getByText('нет учётной записи').count();
    if (marks < CANDIDATES.length) throw new Error(`пометок «нет учётной записи» ${marks}, а кандидатов без учётных записей ${CANDIDATES.length}`);
    await d.getByRole('button', { name: `Назначить · ${CANDIDATES.length}` }).click();
    await d.waitFor({ state: 'detached', timeout: 15_000 })
      .catch(async () => { throw new Error(`окно не закрылось: ${(await d.innerText()).slice(0, 300)}`); });
    for (const c of CANDIDATES) await page.getByRole('row', { name: new RegExp(c.name.replace(/[()]/g, '\\$&')) }).waitFor({ timeout: 10_000 });
  });

  await check('pm-ribbon-shows-same-rows', async () => {
    await page.getByRole('tab', { name: 'Лента' }).click();
    const bar = page.getByRole('button', { name: new RegExp(`${CANDIDATES[0].name.replace(/[()]/g, '\\$&')}.*изменить период`) });
    await bar.waitFor({ timeout: 10_000 });
    await bar.click();
    await dialog('Изменить период').waitFor({ timeout: 5_000 });
    await page.keyboard.press('Escape');
    await dialog('Изменить период').waitFor({ state: 'detached', timeout: 5_000 });
    // Вид запоминается в браузере — вернуть таблицу, как без выбора.
    await page.getByRole('tab', { name: 'Таблица' }).click();
  });

  await removeCandidates();

  // ── 4. Менеджер чужой стройки ─────────────────────────────────────────────────────────────────
  await clearSession(page);
  await login(page, FOREIGN_PM.email, PASSWORD);
  await check('foreign-pm-sees-no-buttons', async () => {
    await panel();
    await page.getByRole('row', { name: new RegExp(ASSIGNMENT_PM.name) }).waitFor({ timeout: 15_000 });
    if (await page.getByRole('button', { name: /Назначить на период/ }).count()) throw new Error('у менеджера чужой стройки есть «Назначить на период»');
    if (await page.getByRole('button', { name: /^Действия назначения/ }).count()) throw new Error('у менеджера чужой стройки есть меню строки');
    const reason = (await page.getByTestId('assignments-reason').innerText({ timeout: 5_000 })).trim();
    if (!reason) throw new Error('причины над списком нет');
  });

  // ── 5. Монтажник ──────────────────────────────────────────────────────────────────────────────
  await clearSession(page);
  await login(page, INSTALLER.email, PASSWORD);
  await check('installer-has-no-assignments-item', async () => {
    await page.goto(`${BASE}/document-sets/${CONSTRUCTION}`, { waitUntil: 'networkidle' });
    await settled(page);
    await page.getByRole('heading', { name: /Раздел «Стройки» недоступен/ }).waitFor({ timeout: 10_000 });
    for (const role of ['link', 'button'])
      if (await page.getByRole(role, { name: /^Назначения/ }).count()) throw new Error('у «Монтажника» в карточке стройки есть пункт «Назначения»');
  });
} finally {
  await browser.close();
}

process.exitCode = summarize('Назначения');
