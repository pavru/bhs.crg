// Вопрос ухода из формы накладной (ревью PR #1817 и #1821, issue #1653) — живым браузером: уход без смены
// адреса («Новая накладная» при набранном) спрашивает, и вопрос стоит, пока идёт его запись. Прежде «Сохранить и
// перейти» закрывал вопрос ДО записи: форма снова принимала правки, а удачная запись уносила их без вопроса.
// Отказ записи уход отменяет: набранное на экране, новой накладной нет.
//
// Ломается: вернуть в `WaybillForm` свою связку с `setLeave(null)` до `await saveDraft()` — вопрос пропадёт на
// время записи; вернуть в `WaybillsPage` смену накладной мимо охраны ухода — «Новая накладная» уйдёт без
// вопроса и унесёт набранное.
//
// Черновики, заведённые здесь, здесь же и удаляются (ревью PR #1821): иначе каждый прогон засорял бы список.
//
// Отдельным файлом: `costs-smoke` у порога размера. Едет внутри него, с его помощниками.
//
// ⚠️ Перехват записи снимается ТОЛЬКО на затихшей странице (`settled`), а не сразу по её ответу. Удачная запись
// тут же уводит на новую накладную, и POST её заведения уходит в ту же миллисекунду, когда проверка снимала
// маршрут. Снятие последнего маршрута выключает перехват в браузере, и запрос, начатый в этот миг вслед за
// запросом, который маршрут держал, Playwright 1.62 не отпускает НИКОГДА: «новая накладная не открылась», а
// «Новая накладная» заперта висящим заведением — и следующая проверка ждёт её 30 с (CI 09.11 и 10.11.2026).
// Голым Playwright, без приложения: 17 зависших из 300 при снятии по ответу задержанного, 0 из 300 при снятии
// после конца цепочки и 0 из 400, если маршрут ничего не держал. Окно этим сужено, а не закрыто: фоновый опрос
// приложения может уйти в тот же миг и на затихшей странице. Снимает перехват общий `underRoute` обвязки — там
// же и почему маршрута, который не снимается, нет.

import { settled, underRoute } from './harness.mjs';

/** На сколько задержать запись накладной — вопрос успевают увидеть занятым. */
const HOLD_MS = 1_500;

export async function waybillLeaveChecks({ seat, check, api, visit }) {
  const { page } = seat;
  const question = page.getByRole('dialog', { name: 'Несохранённые изменения' });
  const saveAndGo = question.getByRole('button', { name: 'Сохранить и перейти' });
  const opened = () => new URL(page.url()).searchParams.get('waybill');
  const isWrite = id => r => r.method() === 'PUT' && new URL(r.url()).pathname === `/api/costs/waybills/${id}`;
  const leftFrom = was => page.waitForFunction(w => new URL(location.href).searchParams.get('waybill') !== w, was,
    { timeout: 10_000 });

  // Каждая заведённая здесь накладная — и своя, и та, что заводит уход «Новой накладной», — на учёте.
  const created = new Set();
  const onCreated = async r => {
    if (r.request().method() !== 'POST' || new URL(r.url()).pathname !== '/api/costs/waybills' || !r.ok()) return;
    const id = (await r.json().catch(() => ({}))).id;
    if (id) created.add(id);
  };

  /** «Новая накладная» — и дождаться её формы: о набранном прежде спросят, оно здесь не нужно. */
  async function newDraft() {
    const before = opened();
    await page.getByRole('button', { name: 'Новая накладная' }).click();
    // Набранное могла оставить предыдущая проверка (`supplier-keeps-typed-waybill-when-reread-fails`). Что раньше —
    // вопрос или уход, — решает гонка, а не пауза: на медленном раннере вопрос приходит позже любой паузы.
    const left = leftFrom(before).then(() => 'left', () => null);
    const asked = question.waitFor({ timeout: 10_000 }).then(() => 'asked', () => null);
    if (await Promise.race([left, asked]) === 'asked')
      await question.getByRole('button', { name: 'Не сохранять' }).click();
    if (await left !== 'left') throw new Error('«Новая накладная» не открылась');
    // Своя форма, а не пометка «Черновик» в строке списка: у только что заведённой номера нет.
    await page.getByRole('heading', { name: /^Накладная № без номера/ }).waitFor({ timeout: 10_000 });
  }

  /** Свой черновик: заведён, записан и снова правлен — уйти с ним молча значило бы потерять правку. */
  async function typedDraft(stamp) {
    await newDraft();
    await page.getByLabel('Номер', { exact: true }).fill(stamp);
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await page.getByRole('heading', { name: new RegExp(stamp) }).waitFor({ timeout: 10_000 });
    await page.getByLabel('Примечание').fill(`набрано до ухода ${stamp}`);
    return opened();
  }

  async function waitsForTheWrite() {
    seat.screen = 'уход из накладной: запись';
    const stamp = `РН-УХОД-${Date.now()}`;
    const id = await typedDraft(stamp);

    const hold = async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      await new Promise(resolve => setTimeout(resolve, HOLD_MS));
      // Снятие перехвата задержанную запись дожидается (`settled` видит её в полёте); отказ `continue` — лишь
      // у страницы, закрытой посреди задержки оборванным набором.
      await route.continue().catch(() => {});
    };
    await underRoute(page, `**/api/costs/waybills/${id}`, hold, async () => {
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
      // Уход — внутри перехвата: он и есть те запросы, которые снятие маршрута могло подвесить (см. шапку).
      await question.waitFor({ state: 'hidden', timeout: 10_000 });
      await leftFrom(id).catch(() => { throw new Error('после удачной записи новая накладная не открылась'); });
    });
    const saved = await api(page, 'GET', `/costs/waybills/${id}`);
    if (saved.note !== `набрано до ухода ${stamp}`) throw new Error(`правка не записана перед уходом: «${saved.note}»`);
  }

  async function staysWhenRefused() {
    seat.screen = 'уход из накладной: отказ записи';
    const stamp = `РН-ОТКАЗ-${Date.now()}`;
    const id = await typedDraft(stamp);

    let posted = 0;
    const onRequest = r => { if (r.method() === 'POST' && new URL(r.url()).pathname === '/api/costs/waybills') posted += 1; };
    const refuse = route => route.request().method() === 'PUT'
      ? route.fulfill({ status: 409, contentType: 'application/json', body: '{"error":"Накладную тем временем изменили (проба)."}' })
      : route.continue();
    page.on('request', onRequest);
    try {
      await underRoute(page, `**/api/costs/waybills/${id}`, refuse, async () => {
        await page.getByRole('button', { name: 'Новая накладная' }).click();
        await question.waitFor({ timeout: 10_000 });
        await saveAndGo.click();
        await question.waitFor({ state: 'hidden', timeout: 10_000 })
          .catch(() => { throw new Error('после отказа записи вопрос остался'); });
        // Уход, если отказ его не отменил, — запросом вслед за ответом на запись. Ждём, пока приложение ответит
        // на отказ целиком (и перечитает накладную), а не паузу: паузы на медленном раннере могло не хватить.
        // Снятие перехвата следом ждёт того же — на затихшей уже странице это два спокойных круга.
        await settled(page);
        if (opened() !== id) throw new Error('отказ записи не отменил уход: открыта другая накладная');
        if (posted) throw new Error('отказ записи не отменил уход: заведена новая накладная');
        if (await page.getByLabel('Примечание').inputValue() !== `набрано до ухода ${stamp}`)
          throw new Error('после отказа записи набранное пропало');
      });
    } finally {
      page.off('request', onRequest);
    }
  }

  page.on('response', onCreated);
  try {
    await check('supplier-is-asked-before-leaving-a-typed-waybill-and-the-question-waits-for-the-write', waitsForTheWrite);
    await check('supplier-stays-on-the-waybill-when-the-write-before-leaving-is-refused', staysWhenRefused);
  } finally {
    page.off('response', onCreated);
  }

  // Уборка — проверкой: молча не удалившийся черновик копился бы прогон за прогоном. Со страницы без открытой
  // накладной: набранное в форме уходом по адресу бросается, а открытая не узнаёт об удалении соседским.
  await check('waybill-leave-checks-remove-their-drafts', async () => {
    // Вопрос браузера об уходе с набранным (`beforeunload`) — «уйти»: набранное здесь брошено нарочно.
    const leave = d => void d.accept();
    page.on('dialog', leave);
    try { await visit(seat, 'накладные: уборка', '/waybills'); } finally { page.off('dialog', leave); }
    const failed = [];
    for (const id of created) {
      const { version } = await api(page, 'GET', `/costs/waybills/${id}`);
      const status = await page.evaluate(async ([wid, seen]) => {
        const token = localStorage.getItem('access_token') ?? sessionStorage.getItem('access_token');
        return (await fetch(`/api/costs/waybills/${wid}`, {
          method: 'DELETE', headers: { Authorization: `Bearer ${token}`, 'If-Match': seen },
        })).status;
      }, [id, version]);
      if (status !== 204) failed.push(`${id}: ${status}`);
    }
    if (!created.size) throw new Error('проверки не завели ни одной накладной — учёт заведённых не работает');
    if (failed.length) throw new Error(`не удалены черновики проверок: ${failed.join(', ')}`);
  });
}
