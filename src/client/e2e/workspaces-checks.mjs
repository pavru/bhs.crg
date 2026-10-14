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

import { BASE, settled, callApi } from './harness.mjs';

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

export async function workspaceChecks(page, check) {
  const reset = await callApi(page, 'PUT', '/account/settings', RESET);
  if (reset.status !== 200) throw new Error(`настройки пространств не сброшены: ${reset.status}`);
  try {
    await page.goto(`${BASE}/document-sets`);
    await settled(page);

    await check('workspace-switcher-names-its-modules', async () => {
      // Два доступных модуля — переключатель есть, и рядом с именем состав: имя само не отвечает
      // на вопрос «почему я не вижу сметы».
      const composition = await page.getByTestId('workspace-composition').innerText();
      if (!/^2 модуля: .+, .+/.test(composition)) throw new Error(`состав в шапке: «${composition}»`);
      if (!(await switcherName(page)).includes('Все доступные модули'))
        throw new Error(`открыто не «Все доступные модули»: ${await switcherName(page)}`);
    });

    await check('workspace-narrows-the-menu-and-names-what-is-hidden', async () => {
      await choose(page, 'Счета и накладные');
      const nav = page.locator('aside nav');
      if (!(await nav.getByRole('link', { name: 'Счета', exact: true }).count()))
        throw new Error('в пространстве «Счета и накладные» нет пункта «Счета»');
      if (!(await nav.getByRole('link', { name: 'Стройки', exact: true }).count()))
        throw new Error('нет «Строек» — раздела ядра, который объявил модуль счетов');
      if (await nav.getByRole('link', { name: 'Документы качества', exact: true }).count())
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
      await dialog.waitFor({ state: 'hidden' }).catch(() => page.keyboard.press('Escape'));
      if (!(await switcherName(page)).includes(OWN)) throw new Error(`созданное не открыто: ${await switcherName(page)}`);

      // Уходим в другое пространство — «последнее выбранное» теперь не своё. Новая вкладка после
      // входа обязана открыть УМОЛЧАНИЕ: его хранит сервер, а вкладка своего выбора ещё не имеет.
      await choose(page, 'Все доступные модули');
      await page.evaluate(() => sessionStorage.clear());
      await page.goto(`${BASE}/document-sets`);
      await settled(page);
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
