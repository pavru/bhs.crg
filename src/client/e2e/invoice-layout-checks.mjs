// Раскладка вида для чтения у счёта (issue #1456) — проверка живым браузером на НАСТОЯЩЕМ конвертере.
//
// Отдельным файлом, а не строками в `invoices-smoke`: тот у предела размера. Едет внутри него, под
// его входом и с его помощниками (`h`): данные готовятся так же, как у соседних проверок файла счёта.
//
// ⚠️ Профили — настройка ЭКЗЕМПЛЯРА. Проверка заводит свой профиль под узнаваемым именем и убирает его
// за собой — и до (остаток упавшего прогона), и после, в finally. Профиль по умолчанию не трогает.

const PREFIX = 'Проверка раскладки счёта';
const URL = '/settings/print-profiles';

async function sweep(h) {
  for (const p of (await h.api('GET', URL)).filter(p => !p.isSystem && p.name.startsWith(PREFIX)))
    await h.api('DELETE', `${URL}/${p.id}`);
}

/**
 * Книга без своей раскладки (`converter-wide.xlsx` — та, ради которой профили заведены): вид разложен
 * по профилю по умолчанию; при втором профиле у строки вида есть чип «Раскладка», и выбор в нём
 * перестраивает по выбранному профилю — а счёт не трогает: версия прежняя.
 */
export async function invoiceLayoutChecks(page, check, h) {
  await sweep(h);
  try {
    await check('счёт в Excel без своей раскладки: чип «Раскладка» перестраивает по выбранному профилю', async () => {
      const base = (await h.api('GET', URL)).find(p => p.isSystem);
      const name = `${PREFIX} ${h.stamp}`;
      await h.api('POST', URL, { ...base, name, orientation: base.orientation === 'portrait' ? 'landscape' : 'portrait' });

      const number = `СЧ-L${h.stamp}`;
      const created = await h.api('POST', '/costs/invoices', { requisites: h.requisites(number, 'Раскладка по профилю') });
      await h.attachScan(created.id, h.officeFile('converter-wide.xlsx'), 'Широкий.xlsx', h.UNKNOWN_TYPE);
      const version = (await h.api('GET', `/costs/invoices/${created.id}`)).version;

      await h.open(number);
      const caveat = page.getByTestId('image-caveat');
      await h.named('строки «Вид для чтения» в панели нет', () => caveat.waitFor({ timeout: 60_000 }));
      const chip = caveat.getByTestId('layout-chip');
      await h.named('чипа «Раскладка» у книги без своей раскладки нет', () => chip.waitFor({ timeout: 15_000 }));
      if (!/Раскладка: по умолчанию/.test(await chip.innerText()))
        throw new Error(`чип называет не профиль по умолчанию: «${await chip.innerText()}»`);

      await chip.click();
      const popover = page.getByTestId('layout-popover');
      const current = popover.getByRole('radio', { name: /по умолчанию/ });
      if ((await current.getAttribute('aria-checked')) !== 'true' || !/сейчас/.test(await current.innerText()))
        throw new Error('в выборе не отмечен профиль показанного вида');
      await popover.getByRole('radio', { name: new RegExp(name) }).click();
      await popover.getByRole('button', { name: 'Перестроить по выбранному' }).click();

      await h.named('тост не назвал выбранный профиль',
        () => page.getByText(`Вид для чтения перестроен по профилю «${name}».`).first().waitFor({ timeout: 60_000 }));
      await h.named('чип не назвал выбранный профиль',
        () => caveat.getByTestId('layout-chip').filter({ hasText: name }).waitFor({ timeout: 10_000 }));
      const view = await h.api('GET', `/costs/invoices/${created.id}/scan/image`);
      if (view.profile?.name !== name) throw new Error(`сервер помнит не выбранный профиль: ${JSON.stringify(view.profile)}`);
      if ((await h.api('GET', `/costs/invoices/${created.id}`)).version !== version)
        throw new Error('перестроение по профилю двинуло версию счёта');
    });
  } finally {
    await sweep(h);
  }
}
