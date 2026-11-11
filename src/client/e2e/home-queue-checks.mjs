// Главная → «Разобрать» (shell-08, ТЗ UI-34) — живым браузером: карточка очереди с числом ведёт в список
// счетов под этим отбором, и число карточки то же, что у чипа экрана (у них общий счётчик сервера).
//
// Отдельным файлом: `invoices-smoke` у предела размера. Едет внутри него, после проверки 1, где заведён
// черновик: он и стоит в очереди «Разобрать», так что карточка на любом стенде есть.

export async function homeQueueChecks(page, check, { BASE }) {
  await check('Главная: карточка «Разобрать» открывает список счетов под отбором «Разобрать»', async () => {
    await page.goto(`${BASE}/home`, { waitUntil: 'networkidle' });
    const card = page.getByRole('link', { name: 'Разобрать', exact: true });
    await card.waitFor({ timeout: 10_000 });
    const number = (await page.getByTestId('home-queue-costs-parsing').locator('.text-headline-small').innerText()).trim();
    await card.click();
    await page.waitForURL(u => u.pathname === '/invoices' && u.searchParams.get('queue') === 'parsing', { timeout: 10_000 });
    const chip = page.getByRole('button', { name: /^Разобрать/ });
    await chip.waitFor({ timeout: 10_000 });
    if (await chip.getAttribute('aria-pressed') !== 'true') throw new Error('чип «Разобрать» не нажат после перехода с Главной');
    const label = (await chip.getAttribute('aria-label')) ?? '';
    if (!label.includes(`счетов: ${number}`)) throw new Error(`на Главной «${number}», у чипа — «${label}»`);
    // Обновление страницы отбор не снимает: он в адресе.
    await page.reload({ waitUntil: 'networkidle' });
    if (await page.getByRole('button', { name: /^Разобрать/ }).getAttribute('aria-pressed') !== 'true')
      throw new Error('после обновления страницы отбор «Разобрать» снят');
    await page.getByRole('button', { name: /^Разобрать/ }).click();
  });
}
