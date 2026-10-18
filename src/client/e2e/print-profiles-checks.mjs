// Профили печати (issue #1455) — проверки живым браузером страницы «Система → Профили печати».
//
// Отдельным файлом, а не своим набором, по той же причине, что избранное: входы прогонов CI
// упёрлись в предел частоты (`suites.test.mjs`). Едут внутри `settings-smoke` под его входом
// администратора.
//
// ⚠️ Профили — настройка ЭКЗЕМПЛЯРА: по профилю по умолчанию строится вид каждой книги на стенде.
// Его правит ровно одна проверка — сброс к заводским, — и только когда на старте он заводской: тогда
// исход известен и возвращается в finally. Правленый кем-то профиль проверки не трогают (пропуск):
// сброс стёр бы чужую настройку. Свои профили заводят под узнаваемым именем и убирают за собой — и до
// проверок (остаток упавшего прогона), и после, в finally, адресом, а не через экран.
//
// Каждая проверка сама открывает страницу и свой профиль: упавшая не оставляет следующей чужой экран.

import { BASE, settled, callApi, until } from './harness.mjs';

const PREFIX = 'Проверка профиля печати';
const URL = '/settings/print-profiles';

async function profiles(page) {
  const list = await callApi(page, 'GET', URL);
  if (list.status !== 200) throw new Error(`список профилей печати не прочитан: ${list.status}`);
  return list.body;
}

async function sweep(page) {
  for (const p of (await profiles(page)).filter(p => !p.isSystem && p.name.startsWith(PREFIX))) {
    await callApi(page, 'DELETE', `${URL}/${p.id}`);
  }
}

/**
 * Ждать, пока условие станет истинным. ⚠️ `until` повторяет пробу, только пока она БРОСАЕТ: проба,
 * вернувшая `false`, заканчивает ожидание сразу — так проверка была бы зелёной, ничего не дождавшись.
 */
const waitFor = (cond, what) => until(async () => { if (!(await cond())) throw new Error(`не дождались: ${what}`); });

const row = (page, name) => page.getByTestId('print-profiles-list').getByRole('button').filter({ hasText: name });
const choice = (detail, group, name) => detail.getByRole('radiogroup', { name: group }).getByRole('radio', { name });

/** Свой профиль с параметрами профиля по умолчанию — адресом, если его ещё нет (упала проверка создания). */
async function ensureOwn(page, name) {
  const list = await profiles(page);
  if (list.some(p => p.name === name)) return;
  const { paper, orientation, fit, margins } = list.find(p => p.isSystem);
  const made = await callApi(page, 'POST', URL, { name, paper, orientation, fit, margins });
  if (made.status !== 200 && made.status !== 201) throw new Error(`профиль для проверки не заведён: ${made.status}`);
}

/** Открыть страницу заново и карточку профиля по строке; вернуть карточку, когда она точно его. */
async function openProfile(page, name, marker) {
  await page.goto(`${BASE}/print-profiles`);
  await settled(page);
  await row(page, name).first().click();
  await settled(page);
  const detail = page.getByTestId('print-profile-detail');
  await waitFor(async () => marker.test(await detail.innerText()), `открыта карточка «${name}»`);
  return detail;
}

const SYSTEM_MARK = /Профиль по умолчанию: по нему строится вид/;
const OWN_MARK = /Применяется, когда его выбрали/;

export async function printProfilesChecks(page, check, skip) {
  await page.goto(`${BASE}/print-profiles`);
  await settled(page);
  await sweep(page);
  const systemAtStart = (await profiles(page)).find(p => p.isSystem);
  try {
    // Профиль по умолчанию — первым; удалить его нечем, а название не правится. Отсутствие кнопки
    // проверяется после того, как карточка ТОЧНО открылась: иначе «кнопки нет» было бы и на пустой странице.
    await check('print-profiles-default-first-and-undeletable', async () => {
      await page.goto(`${BASE}/print-profiles`);
      await settled(page);
      const first = page.getByTestId('print-profiles-list').getByRole('button').first();
      if (!/по умолчанию/.test(await first.innerText())) throw new Error('первым в списке стоит не профиль по умолчанию');
      const detail = await openProfile(page, 'по умолчанию', SYSTEM_MARK);
      if ((await page.getByRole('button', { name: 'Удалить' }).count()) > 0)
        throw new Error('у профиля по умолчанию есть «Удалить»');
      if (await detail.getByLabel('Название').isEditable())
        throw new Error('название профиля по умолчанию правится');
    });

    // Создание: одно название, параметры — из профиля по умолчанию; после создания открыта его карточка.
    const name = `${PREFIX} ${Date.now() % 100000}`;
    await check('print-profile-created-from-default', async () => {
      await page.goto(`${BASE}/print-profiles`);
      await settled(page);
      await page.getByRole('button', { name: 'Добавить профиль' }).click();
      await page.getByRole('dialog').getByLabel('Название').fill(name);
      await page.getByRole('dialog').getByRole('button', { name: 'Создать' }).click();
      await waitFor(async () => (await row(page, name).count()) === 1, 'новый профиль в списке');
      const detail = page.getByTestId('print-profile-detail');
      await waitFor(async () => OWN_MARK.test(await detail.innerText()), 'открыта карточка нового профиля');
      const list = await profiles(page);
      const created = list.find(p => p.name === name);
      const base = list.find(p => p.isSystem);
      if (!created) throw new Error(`профиль не создан: ${JSON.stringify(list).slice(0, 300)}`);
      if (JSON.stringify(created.margins) !== JSON.stringify(base.margins) || created.paper !== base.paper)
        throw new Error('параметры нового профиля не взяты из профиля по умолчанию');
    });

    // Ошибка пары — под наброском, «Сохранить» не выключается: A5 с полями по 40 мм оставляет 68 мм.
    await check('print-profile-margins-shortfall-under-fields', async () => {
      await ensureOwn(page, name);
      const detail = await openProfile(page, name, OWN_MARK);
      await choice(detail, 'Бумага', /A5/).click();
      await detail.getByLabel('Левое').fill('40');
      await detail.getByLabel('Правое').fill('40');
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => (await detail.getByRole('alert').count()) > 0, 'ошибка под полями');
      const text = await detail.getByRole('alert').innerText();
      if (!/по ширине остаётся 68 мм/.test(text)) throw new Error(`под полями не та ошибка: «${text}»`);
      const saved = (await profiles(page)).find(p => p.name === name);
      if (saved.paper === 'A5') throw new Error('негодный профиль ушёл на сервер');
      await page.getByRole('button', { name: 'Отмена' }).click();
    });

    // Правка доходит до сервера: альбомная ориентация; пустое поле — ошибкой, а не нулём.
    await check('print-profile-edit-saved', async () => {
      await ensureOwn(page, name);
      const detail = await openProfile(page, name, OWN_MARK);
      await detail.getByLabel('Верхнее').fill('');
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => /Целое число миллиметров/.test(await detail.innerText()), '«Целое число миллиметров» у пустого поля');
      await detail.getByLabel('Верхнее').fill('12');
      await choice(detail, 'Ориентация', 'Альбомная').click();
      await page.getByRole('button', { name: 'Сохранить' }).click();
      await waitFor(async () => {
        const saved = (await profiles(page)).find(p => p.name === name);
        return saved?.orientation === 'landscape' && saved.margins.top === 12;
      }, 'правка на сервере');
    });

    // Несохранённая правка не теряется молча: переход к другому профилю спрашивает, «Не сохранять» уводит
    // без записи.
    await check('print-profile-leave-asks', async () => {
      await ensureOwn(page, name);
      const detail = await openProfile(page, name, OWN_MARK);
      const before = (await profiles(page)).find(p => p.name === name);
      await detail.getByLabel('Нижнее').fill(String(before.margins.bottom === 7 ? 8 : 7));
      await row(page, 'по умолчанию').first().click();
      const dialog = page.getByRole('dialog', { name: 'Несохранённые изменения' });
      await waitFor(async () => (await dialog.count()) === 1, 'вопрос о несохранённом');
      if (!OWN_MARK.test(await detail.innerText())) throw new Error('карточка сменилась, не дождавшись ответа');
      await dialog.getByRole('button', { name: 'Не сохранять' }).click();
      await waitFor(async () => SYSTEM_MARK.test(await detail.innerText()), 'переход после «Не сохранять»');
      const after = (await profiles(page)).find(p => p.name === name);
      if (after.margins.bottom !== before.margins.bottom) throw new Error('«Не сохранять» записало правку');
    });

    // Удаление своего — с подтверждением; после него открыт профиль по умолчанию.
    await check('print-profile-deleted', async () => {
      await ensureOwn(page, name);
      const detail = await openProfile(page, name, OWN_MARK);
      await page.getByRole('button', { name: 'Удалить' }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Удалить' }).click();
      await waitFor(async () => (await row(page, name).count()) === 0, 'профиль ушёл из списка');
      await waitFor(async () => SYSTEM_MARK.test(await detail.innerText()), 'открыт профиль по умолчанию');
    });

    // Сброс к заводским: правленый профиль по умолчанию — пометка «изменён» и кнопка; после сброса обе ушли.
    // Правится адресом (экран правки проверен выше), сбрасывается экраном.
    if (!systemAtStart?.isFactory) {
      skip('print-profile-reset-to-factory', 'профиль по умолчанию на стенде правлен — сброс стёр бы чужую настройку');
    } else {
      await check('print-profile-reset-to-factory', async () => {
        const { id, name: sysName, paper, orientation, fit, margins } = systemAtStart;
        const changed = await callApi(page, 'PUT', `${URL}/${id}`,
          { name: sysName, paper, orientation, fit, margins: { ...margins, top: margins.top === 7 ? 8 : 7 } });
        if (changed.status !== 200) throw new Error(`профиль по умолчанию не поправлен адресом: ${changed.status}`);
        const detail = await openProfile(page, 'по умолчанию', SYSTEM_MARK);
        if (!/изменён/.test(await page.getByRole('main').innerText())) throw new Error('у правленого нет пометки «изменён»');
        await page.getByRole('button', { name: 'Сбросить к заводским' }).click();
        await page.getByRole('dialog').getByRole('button', { name: 'Сбросить' }).click();
        await waitFor(async () => (await profiles(page)).find(p => p.isSystem)?.isFactory === true, 'сброс на сервере');
        await waitFor(async () => (await page.getByRole('button', { name: 'Сбросить к заводским' }).count()) === 0,
          'кнопка сброса ушла');
        if (!SYSTEM_MARK.test(await detail.innerText())) throw new Error('после сброса открыт не профиль по умолчанию');
      });
    }
  } finally {
    await sweep(page);
    // Заводской на старте — заводской и после, даже если проверка сброса упала посередине.
    if (systemAtStart?.isFactory && !(await profiles(page)).find(p => p.isSystem)?.isFactory)
      await callApi(page, 'POST', `${URL}/${systemAtStart.id}/reset`);
  }
}
