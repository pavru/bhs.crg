// Рабочие пространства (issue #1001, ТЗ AUTH-16.1, AUTH-16.5) — проверки живым браузером.
//
// Отдельным файлом, а не своим набором: входы прогонов CI упёрлись в предел частоты (считает
// `suites.test.mjs`), и новый набор со своим входом туда не помещается. Проверки едут внутри
// `settings-smoke` под его входом администратора — у него доступны оба модуля стенда (id и costs),
// то есть переключатель есть.
//
// ⚠️ Пространство хранит сервер за ЭТИМ человеком, а следующие наборы входят им же: оставленное
// «Счета и накладные» сузило бы им меню, и они упали бы на чужом. Поэтому настройки пространств
// сбрасываются и до проверок, и после — в finally, адресом, а не через экран.

import { BASE, settled, callApi, railSection, accountAction } from './harness.mjs';

const RESET = { workspaces: null, workspace: null, 'workspace.default': null };
const OWN = 'Снабжение (проверка)';

async function choose(page, name) {
  await page.getByTestId('workspace-switcher').click();
  // По ТОЧНОМУ имени: состав «Все доступные модули» перечисляет имена модулей, и подстрока попала бы в него.
  await page.getByTestId('workspace-option').filter({ has: page.getByText(name, { exact: true }) }).click();
  await settled(page);
}

async function switcherName(page) {
  return (await page.getByTestId('workspace-switcher').getAttribute('aria-label')) ?? '';
}

/**
 * Выйти пунктом «Выйти» меню профиля и войти обратно в той же вкладке. Настоящий вход не помещается в предел
 * входов прогонов CI, поэтому «вход» — возврат тех же токенов: выход приложения при этом проходит
 * целиком, со всем, что он стирает. Отзыв refresh-токена на сервере перехвачен — иначе следующие
 * проверки набора остались бы без сессии.
 */
async function relogInSameTab(page) {
  const tokens = await page.evaluate(() => ['access_token', 'refresh_token'].flatMap(key => [
    ['local', key, localStorage.getItem(key)], ['session', key, sessionStorage.getItem(key)],
  ]).filter(([, , value]) => value));
  if (!tokens.length) throw new Error('токены сессии не найдены — возвращать нечего');
  await page.route('**/api/auth/logout', route => route.fulfill({ status: 204 }));
  try {
    await accountAction(page, 'Выйти');
    await page.waitForURL(/\/login/);
  } finally {
    await page.unroute('**/api/auth/logout');
  }
  await page.evaluate(saved => {
    for (const [where, key, value] of saved) (where === 'local' ? localStorage : sessionStorage).setItem(key, value);
  }, tokens);
  await page.goto(`${BASE}/document-sets`);
  await settled(page);
}

export async function workspaceChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройки пространств не сброшены: ${reset.status}`);
  try {
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('workspace-switcher-names-its-modules', async () => {
      // Два доступных модуля — переключатель есть. На кнопке только короткое имя (#1432), а состав —
      // в списке, у пространства: имя само не отвечает на вопрос «почему я не вижу сметы».
      if (!(await switcherName(page)).includes('Все доступные модули'))
        throw new Error(`открыто не «Все доступные модули»: ${await switcherName(page)}`);
      const button = page.getByTestId('workspace-switcher');
      if ((await button.innerText()).trim() !== 'Все доступные модули')
        throw new Error(`на кнопке пространства: «${await button.innerText()}»`);
      await button.click();
      const option = page.getByTestId('workspace-option').filter({ has: page.getByText('Все доступные модули', { exact: true }) });
      const composition = await option.getByTestId('workspace-composition').innerText();
      await page.keyboard.press('Escape');
      if (!/^2 модуля: .+, .+/.test(composition)) throw new Error(`состав в списке: «${composition}»`);
    });

    await check('workspace-narrows-the-menu-and-names-what-is-hidden', async () => {
      await choose(page, 'Счета и накладные');
      if (!(await railSection(page, 'Стройки').count()))
        throw new Error('нет «Строек» — раздела ядра, который объявил модуль счетов');
      await railSection(page, 'Затраты').click();
      await settled(page);
      const panel = page.getByTestId('section-panel');
      if (!(await panel.getByRole('link', { name: 'Счета', exact: true }).count()))
        throw new Error('в пространстве «Счета и накладные» нет пункта «Счета»');
      // «Данные» остаются разделом — «Общие данные» объявил модуль счетов, — но без страниц модуля ИД.
      await railSection(page, 'Данные').click();
      await settled(page);
      if (await panel.getByRole('link', { name: 'Документы качества', exact: true }).count())
        throw new Error('пункт модуля ИД виден в пространстве счетов');
      const more = await page.getByTestId('outside-nav').locator('button').first().innerText();
      if (!/^Ещё \d+ раздел/.test(more) || !more.includes('вне пространства'))
        throw new Error(`строка скрытых разделов: «${more}»`);
    });

    await check('outside-section-opens-without-switching-the-workspace', async () => {
      const outside = page.getByTestId('outside-nav');
      await outside.locator('button').first().click();
      await outside.getByRole('link', { name: 'Документы качества', exact: true }).click();
      await settled(page);
      if (!page.url().includes('/quality-docs')) throw new Error(`открыт не тот адрес: ${page.url()}`);
      const banner = await page.getByTestId('outside-banner').innerText();
      if (!banner.includes('вне пространства «Счета и накладные»')) throw new Error(`плашка: «${banner}»`);
      if (!(await switcherName(page)).includes('Счета и накладные'))
        throw new Error(`пространство сменилось от перехода: ${await switcherName(page)}`);
    });

    await check('palette-finds-sections-outside-the-workspace', async () => {
      await page.keyboard.press('Control+k');
      const palette = page.getByRole('dialog');
      await palette.locator('input').fill('Наборы данных');
      const text = await palette.innerText();
      await page.keyboard.press('Escape');
      if (!text.includes('Наборы данных') || !text.includes('Вне пространства'))
        throw new Error(`палитра: «${text.replace(/\s+/g, ' ').slice(0, 200)}»`);
    });

    await check('own-workspace-is-built-and-opens-after-login', async () => {
      // «С другого компьютера» заводят своё пространство — адресом, мимо кэша этой страницы. Создание
      // здесь обязано его сохранить (ревью PR #1330): запись по кэшу стёрла бы его молча.
      const elsewhere = await callApi(page, 'PUT', '/account/settings',
        { workspaces: JSON.stringify([{ id: 'welsewhere', name: 'Склад (проверка)', modules: ['id'] }]) });
      if (elsewhere.status !== 200) throw new Error(`пространство «с другого компьютера» не заведено: ${elsewhere.status}`);
      await page.getByTestId('workspace-switcher').click();
      await page.getByRole('menuitem', { name: 'Настроить пространства…' }).click();
      const dialog = page.getByRole('dialog');
      const form = dialog.getByTestId('workspace-form');
      // Сохранение заперто с причиной под кнопкой, а не отказывает после нажатия.
      if (!(await form.getByTestId('workspace-problem').innerText()).includes('Назовите'))
        throw new Error('у пустой формы нет причины, почему сохранить нельзя');
      await form.getByLabel('Имя').fill(OWN);
      await form.getByLabel('Счета и накладные').check();
      const together = await form.getByTestId('workspace-together').innerText();
      if (!together.includes('Стройки')) throw new Error(`строка «Вместе с ними»: «${together}»`);
      await form.getByLabel('Открывать при входе').check();
      await form.getByRole('button', { name: 'Создать' }).click();
      await settled(page);
      // Создав, окно закрывается само: новое заводят, чтобы в нём работать. Ожидание без «поймать и
      // нажать Esc» (ревью PR #1330): с ним проверка зеленела на окне, которое не закрывалось вовсе.
      await dialog.waitFor({ state: 'hidden', timeout: 5000 })
        .catch(() => { throw new Error('после «Создать» окно пространств не закрылось'); });
      if (!(await switcherName(page)).includes(OWN)) throw new Error(`созданное не открыто: ${await switcherName(page)}`);
      const names = JSON.parse((await callApi(page, 'GET', '/account/settings')).body.workspaces).map(w => w.name);
      if (!names.includes('Склад (проверка)') || !names.includes(OWN))
        throw new Error(`после создания свои пространства: ${names.join(', ')} — заведённое с другого компьютера потеряно`);

      // Уходим в другое пространство — «последнее выбранное» теперь не своё, и вкладка его помнит.
      // Повторный вход В ТОЙ ЖЕ вкладке обязан открыть УМОЛЧАНИЕ: выход стирает выбор вкладки (ревью
      // PR #1330 — раньше здесь стояла ручная очистка хранилища, и забытая очистка в выходе прошла бы).
      await choose(page, 'Все доступные модули');
      await relogInSameTab(page);
      if (!(await switcherName(page)).includes(OWN))
        throw new Error(`после входа открыто не своё пространство: ${await switcherName(page)}`);
    });

    await check('profile-names-the-default-workspace', async () => {
      await page.goto(`${BASE}/profile`);
      await settled(page);
      const section = await page.getByTestId('default-workspace').innerText();
      if (!section.includes(OWN)) throw new Error(`в профиле умолчание: «${section}»`);
    });
  } finally {
    await callApi(page, 'PUT', '/account/settings', RESET);
  }
}
