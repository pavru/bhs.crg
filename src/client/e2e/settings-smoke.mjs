// Smoke по формам настроек и профиля (issue #858, порция 4).
//
// Все они устроены одинаково: сервер отдаёт текущее состояние, человек правит, пришедший заново
// ответ обязан правку заместить. Раньше это делал эффект «пришли данные — перелей их в форму»,
// теперь — общий useServerForm (правка хранится вместе с тем ответом, от которого начата).
// Ошибка в такой замене не падает, а показывает ПУСТУЮ форму там, где данные есть, — экран
// выглядит «ещё не загрузился», и заметить это можно только глазами.
//
// Ничего не сохраняет: формы заполняются и бросаются вместе с браузером.
//
// Требует поднятых фронта (:5173) и бэка (:5000). Учётка — админская (настройки под AdminRoute).
//
// Запуск (Git Bash):  MSYS_NO_PATHCONV=1 node e2e/settings-smoke.mjs
// Код возврата: 0 — все проверки прошли, 1 — есть провал.

import { BASE, launchBrowser, login, createChecks } from './harness.mjs';

const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
page.on('pageerror', e => console.log('  ! ошибка страницы:', e.message));
const { check, summarize } = createChecks();

await login(page);

/**
 * Раскрывает раздел настроек, предварительно свернув предыдущий, и возвращает текст страницы.
 *
 * <p>Сворачивать обязательно: поля ищутся по странице целиком, и при двух раскрытых разделах
 * проверка «форма собралась из ответа» находила бы чужие заполненные поля — то есть оставалась бы
 * зелёной на пустой форме (проверено: со сломанным smtpForm она проходила).</p>
 */
let openTitle = null;
async function openSection(title) {
  if (openTitle) {
    await page.locator('button').filter({ hasText: openTitle }).first().click();
    await page.waitForTimeout(700);
  }
  await page.locator('button').filter({ hasText: title }).first().click();
  await page.waitForTimeout(1500);
  openTitle = title;
  return page.locator('body').innerText();
}

try {

await page.goto(`${BASE}/settings`);
await page.waitForTimeout(3000);

// ── Интеграции: пять кусков формы поверх одного ответа ─────────────────────────
await check('integrations-form-shows-saved-engines', async () => {
  const t = await openSection('ПОИСК И РАСПОЗНАВАНИЕ');
  for (const engine of ['Google Gemini', 'Anthropic Claude', 'Ollama']) {
    if (!t.includes(engine)) throw new Error(`движка «${engine}» на форме нет`);
  }
  // Хотя бы один включённый движок — признак того, что форма собралась из ОТВЕТА, а не из пустышки.
  const checked = await page.locator('input[type=checkbox]:checked').count();
  if (checked < 1) throw new Error('ни один движок не отмечен — форма собралась не из ответа сервера');
});

await check('integrations-domain-list-keeps-typed-text', async () => {
  const areas = page.locator('textarea');
  if ((await areas.count()) < 1) throw new Error('списков доменов на форме нет');
  const area = areas.first();
  const before = await area.inputValue();
  if (!before.trim()) throw new Error('список доменов пуст — проверять сохранение ввода не на чем');
  // Хвостовой перевод строки — то, ради чего список и хранит «сырой» текст: очищенный список от
  // него не меняется, и текст обязан остаться на экране как набран.
  await area.fill(`${before}\n`);
  await page.waitForTimeout(600);
  const after = await area.inputValue();
  if (after !== `${before}\n`) throw new Error(`набранное не удержалось: «${JSON.stringify(after)}»`);
  await area.fill(before);   // возвращаем как было (наружу и так уходит тот же список)
  await page.waitForTimeout(400);
});

// ── Почта, обновления, резервное копирование ──────────────────────────────────
await check('smtp-form-shows-saved-settings', async () => {
  const t = await openSection('ПОЧТА (SMTP)');
  if (!/SMTP|Хост|Порт/i.test(t)) throw new Error(`раздел почты не раскрылся: ${t.slice(-200)}`);
  // Проверяем ИМЕННО сохранённые значения (адрес отправителя), а не порт: у порта есть разумное
  // умолчание, и пустая форма выглядела бы заполненной.
  const values = await page.locator('input:not([type])').evaluateAll(els => els.map(e => e.value));
  if (!values.some(v => v && v.includes('@')))
    throw new Error(`форма почты собралась не из ответа сервера: ${JSON.stringify(values)}`);
});

await check('updates-toggle-reflects-server', async () => {
  const t = await openSection('ОБНОВЛЕНИЯ');
  if (!/Проверять обновления|Последняя проверка|обновлени/i.test(t))
    throw new Error(`раздел обновлений не раскрылся: ${t.slice(-200)}`);
});

await check('backup-schedule-form-shows-saved-values', async () => {
  const t = await openSection('РЕЗЕРВНОЕ КОПИРОВАНИЕ');
  if (!/расписан|Хранить|копи/i.test(t)) throw new Error(`раздел копий не раскрылся: ${t.slice(-200)}`);
  const times = await page.locator('input[type=time]').evaluateAll(els => els.map(e => e.value));
  if (times.length && !times.some(v => /^\d{2}:\d{2}/.test(v)))
    throw new Error(`время в расписании пусто: ${JSON.stringify(times)}`);
});

// ── Профиль ───────────────────────────────────────────────────────────────────
await check('profile-shows-account-name-and-keeps-typing', async () => {
  await page.goto(`${BASE}/profile`);
  await page.waitForTimeout(2500);
  // Первый input на странице — скрытый выбор аватара; имя лежит в текстовом, а TextField атрибут
  // type не проставляет — потому `:not([type])`, а не `[type=text]`.
  const field = page.locator('input:not([type])').first();
  const before = await field.inputValue();
  if (!before.trim()) throw new Error('имя учётной записи в форме пусто');
  await field.fill(`${before} X`);
  await page.waitForTimeout(1200);   // за это время успевает пройти фоновое перечитывание
  const after = await field.inputValue();
  if (after !== `${before} X`) throw new Error(`набранное имя не удержалось: «${after}»`);
  await field.fill(before);
  await page.waitForTimeout(400);
});

// ── Профили распознавания ─────────────────────────────────────────────────────
await check('recognition-profile-detail-shows-fields', async () => {
  await page.goto(`${BASE}/recognition-profiles`);
  await page.waitForTimeout(3000);
  const rows = page.locator('button').filter({ hasText: /штамп|счёт|обложк|титул|таблиц/i });
  if ((await rows.count()) < 1) throw new Error('в списке профилей нечего открыть');
  await rows.first().click();
  await page.waitForTimeout(2000);
  const inputs = await page.locator('input[type=text], input:not([type])').evaluateAll(els => els.map(e => e.value));
  if (!inputs.some(v => v && v.trim())) throw new Error('поля профиля пусты — форма собралась не из ответа');
});

} finally {
  await browser.close();
}

process.exitCode = summarize('Smoke форм настроек');
