// Профили печати (issue #1455) — проверки живым браузером страницы «Система → Профили печати».
//
// Отдельным файлом, а не своим набором, по той же причине, что избранное: входы прогонов CI
// упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke` под его входом
// администратора.
//
// ⚠️ Профили — настройка ЭКЗЕМПЛЯРА: по профилю по умолчанию строится вид каждой книги на стенде.
// Его проверки не правят вовсе; свои профили заводят под узнаваемым именем и убирают за собой —
// и до проверок (остаток упавшего прогона), и после, в finally, адресом, а не через экран.

import { BASE, settled, callApi, until } from './harness.mjs';

const PREFIX = 'Проверка профиля печати';
const URL = '/settings/print-profiles';

async function sweep(page) {
  const list = await callApi(page, 'GET', URL);
  if (list.status !== 200) throw new Error(`список профилей печати не прочитан: ${list.status}`);
  for (const p of list.body.filter(p => !p.isSystem && p.name.startsWith(PREFIX))) {
    await callApi(page, 'DELETE', `${URL}/${p.id}`);
  }
  return list.body;
}

/**
 * Ждать, пока условие станет истинным. ⚠️ `until` повторяет пробу, только пока она БРОСАЕТ: проба,
 * вернувшая `false`, заканчивает ожидание сразу — так проверка была бы зелёной, ничего не дождавшись.
 */
const waitFor = (cond, what) => until(async () => { if (!(await cond())) throw new Error(`не дождались: ${what}`); });

const row = (page, name) => page.getByTestId('print-profiles-list').getByRole('button').filter({ hasText: name });

export async function printProfilesChecks(page, check) {
  await page.goto(`${BASE}/print-profiles`);
  await settled(page);
  await sweep(page);
  try {
    // Профиль по умолчанию — первым, с пометкой; удалить его нечем, а название не правится.
    // Отсутствие кнопки проверяется после того, как карточка ТОЧНО открылась: иначе «кнопки нет»
    // выполнялось бы и на пустой странице.
    await check('print-profiles-default-first-and-undeletable', async () => {
      await page.goto(`${BASE}/print-profiles`);
      await settled(page);
      const first = page.getByTestId('print-profiles-list').getByRole('button').first();
      if (!/по умолчанию/.test(await first.innerText())) throw new Error('первым в списке стоит не профиль по умолчанию');
      await first.click();
      await settled(page);
      const detail = page.getByTestId('print-profile-detail');
      if (!/Профиль по умолчанию: по нему строится вид/.test(await detail.innerText()))
        throw new Error('карточка профиля по умолчанию не открылась');
      if ((await page.getByRole('button', { name: 'Удалить' }).count()) > 0)
        throw new Error('у профиля по умолчанию есть «Удалить»');
      if (await detail.getByLabel('Название').isEditable())
        throw new Error('название профиля по умолчанию правится');
    });

    // Создание: одно название, параметры — из профиля по умолчанию; после создания открыта его карточка.
    const name = `${PREFIX} ${Date.now() % 100000}`;
    await check('print-profile-created-from-default', async () => {
      await page.getByRole('button', { name: 'Добавить профиль' }).click();
      await page.getByRole('dialog').getByLabel('Название').fill(name);
      await page.getByRole('dialog').getByRole('button', { name: 'Создать' }).click();
      await waitFor(async () => (await row(page, name).count()) === 1, 'новый профиль в списке');
      const detail = page.getByTestId('print-profile-detail');
      await waitFor(async () => /Применяется, когда его выбрали/.test(await detail.innerText()), 'открыта карточка нового профиля');
      const profiles = (await callApi(page, 'GET', URL)).body;
      const created = profiles.find(p => p.name === name);
      const base = profiles.find(p => p.isSystem);
      if (!created) throw new Error(`профиль не создан: ${JSON.stringify(profiles).slice(0, 300)}`);
      if (JSON.stringify(created.margins) !== JSON.stringify(base.margins) || created.paper !== base.paper)
        throw new Error('параметры нового профиля не взяты из профиля по умолчанию');
    });

    // Ошибка — под полями, «Сохранить» не выключается: A5 с полями по 40 мм оставляет 68 мм.
    await check('print-profile-margins-shortfall-under-fields', async () => {
      await row(page, name).click();
      await settled(page);
      const detail = page.getByTestId('print-profile-detail');
      await detail.getByRole('group', { name: 'Бумага' }).getByRole('button', { name: /A5/ }).click();
      await detail.getByLabel('Левое').fill('40');
      await detail.getByLabel('Правое').fill('40');
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => (await detail.getByRole('alert').count()) > 0, 'ошибка под полями');
      const text = await detail.getByRole('alert').innerText();
      if (!/остаётся 68 мм/.test(text)) throw new Error(`под полями не та ошибка: «${text}»`);
      const saved = (await callApi(page, 'GET', URL)).body.find(p => p.name === name);
      if (saved.paper === 'A5') throw new Error('негодный профиль ушёл на сервер');
      await page.getByRole('button', { name: 'Отмена' }).click();
    });

    // Правка доходит до сервера: альбомная ориентация и пустое поле — ошибкой, а не нулём.
    await check('print-profile-edit-saved', async () => {
      const detail = page.getByTestId('print-profile-detail');
      await detail.getByLabel('Верхнее').fill('');
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => /Целое число миллиметров/.test(await detail.innerText()), '«Целое число миллиметров» у пустого поля');
      await detail.getByLabel('Верхнее').fill('12');
      await detail.getByRole('group', { name: 'Ориентация' }).getByRole('button', { name: 'Альбомная' }).click();
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => {
        const saved = (await callApi(page, 'GET', URL)).body.find(p => p.name === name);
        return saved?.orientation === 'landscape' && saved.margins.top === 12;
      }, 'правка на сервере');
    });

    // Удаление своего — с подтверждением; после него открыт профиль по умолчанию.
    await check('print-profile-deleted', async () => {
      await page.getByRole('button', { name: 'Удалить' }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Удалить' }).click();
      await waitFor(async () => (await row(page, name).count()) === 0, 'профиль ушёл из списка');
      const detail = page.getByTestId('print-profile-detail');
      await waitFor(async () => /Профиль по умолчанию: по нему строится вид/.test(await detail.innerText()), 'открыт профиль по умолчанию');
    });
  } finally {
    await sweep(page);
  }
}
