// Вопрос ухода из формы накладной (ревью PR #1817, issue #1653) — живым браузером: уход без смены адреса
// («Новая накладная» при набранном) спрашивает, и вопрос стоит, пока идёт его запись. Прежде «Сохранить и
// перейти» закрывал вопрос ДО записи: форма снова принимала правки, а удачная запись уносила их без вопроса.
// Отказ записи уход отменяет: набранное на экране, новой накладной нет.
//
// Ломается: вернуть в `WaybillForm` свою связку с `setLeave(null)` до `await saveDraft()` — вопрос пропадёт на
// время записи; убрать `onAsk` у `LeaveGuard` — «Новая накладная» уйдёт без вопроса и унесёт набранное.
//
// Отдельным файлом: `costs-smoke` у порога размера. Едет внутри него, с его помощниками.

/** На сколько задержать запись накладной — вопрос успевают увидеть занятым. */
const HOLD_MS = 1_500;

export async function waybillLeaveChecks({ seat, check, api }) {
  const { page } = seat;
  const question = page.getByRole('dialog', { name: 'Несохранённые изменения' });
  const saveAndGo = question.getByRole('button', { name: 'Сохранить и перейти' });
  const opened = () => new URL(page.url()).searchParams.get('waybill');
  const isWrite = id => r => r.method() === 'PUT' && new URL(r.url()).pathname === `/api/costs/waybills/${id}`;

  /** Свой черновик: заведён, записан и снова правлен — уйти с ним молча значило бы потерять правку. */
  async function typedDraft(stamp) {
    const before = opened();
    await page.getByRole('button', { name: 'Новая накладная' }).click();
    // Предыдущая проверка могла оставить набранное (`supplier-keeps-typed-waybill-when-reread-fails`) — тогда
    // спрашивают о нём; оно этой проверке не нужно.
    await question.waitFor({ timeout: 1_000 }).then(
      () => question.getByRole('button', { name: 'Не сохранять' }).click(), () => {});
    await page.waitForFunction(was => new URL(location.href).searchParams.get('waybill') !== was, before, { timeout: 10_000 });
    await page.getByText('Черновик', { exact: true }).first().waitFor({ timeout: 10_000 });
    await page.getByLabel('Номер', { exact: true }).fill(stamp);
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await page.getByRole('heading', { name: new RegExp(stamp) }).waitFor({ timeout: 10_000 });
    await page.getByLabel('Примечание').fill(`набрано до ухода ${stamp}`);
    return opened();
  }

  await check('supplier-is-asked-before-leaving-a-typed-waybill-and-the-question-waits-for-the-write', async () => {
    seat.screen = 'уход из накладной: запись';
    const stamp = `РН-УХОД-${Date.now()}`;
    const id = await typedDraft(stamp);

    const hold = async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      await new Promise(resolve => setTimeout(resolve, HOLD_MS));
      // Проверка могла упасть и снять перехват, пока запись держали: запрос тогда уже отпущен.
      await route.continue().catch(() => {});
    };
    await page.route(`**/api/costs/waybills/${id}`, hold);
    try {
      await page.getByRole('button', { name: 'Новая накладная' }).click();
      await question.waitFor({ timeout: 10_000 })
        .catch(() => { throw new Error('«Новая накладная» при набранном ушла без вопроса'); });
      const sent = page.waitForRequest(isWrite(id), { timeout: 10_000 });
      await saveAndGo.click();
      await sent;
      // Отказ ожидания — значением, а не брошенным обещанием: проверка ниже может упасть раньше, чем его ждут.
      const answered = page.waitForResponse(r => isWrite(id)(r.request()), { timeout: 15_000 }).catch(e => e);
      // Запись идёт: вопрос открыт и занят — правок под ним нет, а уход ещё не случился.
      if (!(await question.isVisible())) throw new Error('вопрос закрылся до конца записи');
      if (await saveAndGo.isEnabled()) throw new Error('«Сохранить и перейти» доступна, пока идёт запись');
      if (opened() !== id) throw new Error('форма ушла, не дождавшись записи');
      const write = await answered;
      if (write instanceof Error) throw write;
      if (!write.ok()) throw new Error(`запись накладной: ${write.status()}`);
    } finally {
      await page.unroute(`**/api/costs/waybills/${id}`, hold);
    }
    await question.waitFor({ state: 'hidden', timeout: 10_000 });
    await page.waitForFunction(was => new URL(location.href).searchParams.get('waybill') !== was, id, { timeout: 10_000 })
      .catch(() => { throw new Error('после удачной записи новая накладная не открылась'); });
    const saved = await api(page, 'GET', `/costs/waybills/${id}`);
    if (saved.note !== `набрано до ухода ${stamp}`) throw new Error(`правка не записана перед уходом: «${saved.note}»`);
  });

  await check('supplier-stays-on-the-waybill-when-the-write-before-leaving-is-refused', async () => {
    seat.screen = 'уход из накладной: отказ записи';
    const stamp = `РН-ОТКАЗ-${Date.now()}`;
    const id = await typedDraft(stamp);

    let created = 0;
    const onRequest = r => { if (r.method() === 'POST' && new URL(r.url()).pathname === '/api/costs/waybills') created += 1; };
    const refuse = route => route.request().method() === 'PUT'
      ? route.fulfill({ status: 409, contentType: 'application/json', body: '{"error":"Накладную тем временем изменили (проба)."}' })
      : route.continue();
    page.on('request', onRequest);
    await page.route(`**/api/costs/waybills/${id}`, refuse);
    try {
      await page.getByRole('button', { name: 'Новая накладная' }).click();
      await question.waitFor({ timeout: 10_000 });
      await saveAndGo.click();
      await question.waitFor({ state: 'hidden', timeout: 10_000 })
        .catch(() => { throw new Error('после отказа записи вопрос остался'); });
      await page.waitForTimeout(500);
      if (opened() !== id) throw new Error('отказ записи не отменил уход: открыта другая накладная');
      if (created) throw new Error('отказ записи не отменил уход: заведена новая накладная');
      if (await page.getByLabel('Примечание').inputValue() !== `набрано до ухода ${stamp}`)
        throw new Error('после отказа записи набранное пропало');
    } finally {
      await page.unroute(`**/api/costs/waybills/${id}`, refuse);
      page.off('request', onRequest);
    }
    // Набранное не нужно следующим проверкам: уйти без записи — тем же вопросом.
    await page.getByRole('button', { name: 'Новая накладная' }).click();
    await question.getByRole('button', { name: 'Не сохранять' }).click();
    await question.waitFor({ state: 'hidden', timeout: 10_000 });
  });
}
