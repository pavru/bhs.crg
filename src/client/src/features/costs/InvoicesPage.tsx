import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { useSuppressSectionPanel } from '@/shared/ui/sectionPanelSuppress';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { useComposedSearchParams } from '@/shared/hooks/useComposedSearchParams';
import { useRecentTouch } from '@/shared/hooks/useRecentTouch';
import { COARSE, useMedia } from '@/shared/hooks/useMedia';
import { Icon } from '@/shared/ui/Icon';
import { Add, CalendarClock, Description, LinkIcon, Sell } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';
import { useNavigationGuard } from '@/shared/ui/NavigationGuard';
import { useCloseGone } from '@/shared/hooks/useCloseGone';
import { useOpenRecord } from './openRecord';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { PALETTE_ACTIONS } from '@/shared/ui/paletteActions';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { usePaletteIntent } from '@/shared/ui/usePaletteIntent';
import { apiError } from '@/shared/utils/apiError';
import {
  INVOICES_KEY, forgetInvoice, scansRunning, useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
  type InvoiceListItem, type InvoiceQueue, type InvoiceView,
} from '@/shared/api/invoices';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { useInvoiceQueues, useParsingCount } from '@/shared/api/invoiceQueues';
import { useInvoiceFromScan } from '@/shared/api/invoiceRecognition';
import { acceptOf, recognizedKinds, useFileKinds } from '@/shared/api/fileKinds';
import { useInvoiceFileIntake } from './useInvoiceFileIntake';
import { UNCHECKED_NOTE } from './scanIntake';
import { useAuth } from '@/shared/hooks/useAuth';
import { ArticlesDialog } from './ArticlesDialog';
import { SupplierMatchesDialog } from './SupplierMatchesDialog';
import { SupplierTermsDialog } from './SupplierTermsDialog';
import { InvoiceForm } from './InvoiceForm';
import { InvoiceLeftRow, InvoiceListRow } from './InvoiceListRow';
import { InvoiceQueueChips } from './InvoiceQueueChips';
import { RailSitesEmpty } from './InvoiceRailSites';
import { useRailSites } from './useRailSites';
import {
  QUEUE_LABEL, afterExit, backRefused, undoRefused, emptyText, heldRow, keepTrace, lockedNote, queueRows,
  type HeldRow, type QueueExit, type QueueTrace,
} from './invoiceQueues';
import { InvoiceScanBatchBar } from './InvoiceScanBatchBar';
import { dropInvited, fromFileTitle as fromFileWords, selectHint } from './invoiceDropWords';
import { useQueueFilter } from './useQueueFilter';
import { InvoiceScanPanel } from './InvoiceScanPanel';
import { InvoiceFileButton, type FileShow } from './InvoiceFileButton';
import { FromFileButton, InvoiceListHeader, InvoiceStepper } from './InvoicePageChrome';
import { pageActions, useWorkWidth } from './invoicePageModel';
import { FILE_WIDTH_CSS, fileWanted, placeOf, positionOf } from './invoicePlace';
import { K, PARSED, REJECTED, asInput } from './invoiceFields';
import { useQueuesFollowScans } from './recognitionWatch';
import { markCreatedHere } from './invoiceDelete';
import { pendingIds, usePendingDeletes } from './pendingDelete';
import { useInvoiceUndo } from './useInvoiceUndo';
import {
  acceptHeld, aliveIds, forgetCreated, retryRejected, uploadEmpty, uploadedOf, useScanBatch,
} from './scanBatch';

/**
 * Счета на оплату: реестр слева, форма ввода справа, скан рядом с формой (задача C1, issue #1076).
 *
 * <p>⚠️ Это НЕ реестр из ТЗ. Реестр — готовое представление таблицы счетов, со своей сеткой, отбором
 * и постраничностью (задача G4, issue #1097), и у него свой пункт в навигации. Здесь список ровно
 * затем, чтобы дойти до формы и увидеть, что черновик в него попал.</p>
 *
 * <p><b>Открытый счёт назван в адресе</b> (`?invoice=…`; G4, issue #1097): по нему сюда ведёт строка
 * реестра, и перезагрузка счёт не закрывает. Выбор в списке адрес ЗАМЕНЯЕТ, а не добавляет запись в
 * историю (конвенция list-detail, issue #787): «назад» возвращает туда, откуда пришли, — в реестр с
 * его отбором, — а не перебирает щелчки по списку. Счёт, которого нет, отвечает отказом чтения в
 * форме, а не приглашением выбрать счёт.</p>
 */
export function InvoicesPage() {
  // Записи адреса подряд складываются (`useComposedSearchParams`): снять отбор и открыть счёт — один обработчик.
  const [params, setParams] = useComposedSearchParams();
  const selected = params.get(INVOICE_RECORD.param) || null;
  // Узкое место (#1355): счёт, названный адресом, показывается карточкой — и при открытии, и при смене адреса.
  const ld = useListDetailView(selected);
  // Счёт, на который привёл переход очереди: его форма при открытии берёт фокус на шапку. Любой
  // другой выбор счёта это снимает — иначе счёт забирал бы фокус при каждом следующем открытии.
  const [led, setLed] = useState<string | null>(null);
  const setSelected = (id: string) => {
    ld.showDetail();
    setLed(null);
    setParams(next => next.set(INVOICE_RECORD.param, id), { replace: true });
  };
  const guard = useNavigationGuard();
  /**
   * Смена счёта по строке списка, ссылке двойника и полосе загрузки (D-10.7) — через ту же охрану ухода,
   * что уход по ссылке: форма с несохранённым спрашивает своим вопросом и сама решает, уходить ли. Без
   * этого щелчок по соседнему счёту перемонтировал форму, и набранное пропадало без вопроса.
   *
   * <p>Форма, что пишет сама (черновик, costs-invoice-screen-12-autosave, D-12.7), не спрашивает: смена сначала
   * дописывает её. Дошло — переход; отказ или сбой — человек остаётся на счёте, причину называет носитель
   * записи в сводке.</p>
   *
   * <p>Выбор — из `now`, а не из замыкания: «Вернуть» снэкбара зовёт это из рендера, где удалённый черновик
   * ещё был выбранным, и прежний выбор принял бы возврат за щелчок по открытому.</p>
   */
  const switchTo = (id: string) => {
    if (id === now.current.selected) { ld.showDetail(); return; }
    const go = () => setSelected(id);
    const flush = flushForm.current;
    if (flush) { void flush().then(ok => { if (ok && alive.current) go(); }); return; }
    if (!guard?.attempt(go)) go();
  };
  // Запись открытой формы, что пишет сама, — её зовёт смена счёта (`InvoiceForm.onFlush`).
  const flushForm = useRef<(() => Promise<boolean>) | null>(null);
  const noteFlush = useCallback((flush: (() => Promise<boolean>) | null) => { flushForm.current = flush; }, []);
  const [query, setQuery] = useState('');
  // Отбор списка — ОДНО состояние (ревью PR #1304): чип очереди либо «только счета этой загрузки»
  // (issue #1294). Двух сразу не бывает по устройству, а не по дисциплине вызовов: чип считает
  // сервер, загрузку — вкладка, и их пересечение дало бы пустой список без объяснения.
  // Очередь живёт и в адресе (shell-08, ТЗ COST-6.2) — см. `useQueueFilter`.
  const [filter, setFilter] = useQueueFilter(params, setParams);
  const queue = filter === 'upload' ? null : filter;
  const setQueue: (next: InvoiceQueue | null) => void = setFilter;
  const [measure, workWidth] = useWorkWidth();
  const { data: access = NO_ACCESS } = useAccess();
  const [articlesOpen, setArticlesOpen] = useState(false);
  const [matchesOpen, setMatchesOpen] = useState(false);
  const [termsOpen, setTermsOpen] = useState(false);

  const invoices = useInvoices(queue);
  // Числа чипов «наведите порядок» — тому, кто счёт может исправить: остальным число было бы упрёком
  // без выхода. Вместе с ними приходят счета закрытого периода (в отбор не входят, и список обязан
  // сказать о них сам) и оговорка «проверено не всё».
  const counts = useInvoiceQueues(hasPermission(access, 'costs.invoice.edit'));
  const queues = counts.data;
  const parsing = useParsingCount().data;
  useQueuesFollowScans(scansRunning(invoices.data), () => { if (counts.isEnabled) void counts.refetch(); });
  const fixing = queue === 'lost' || queue === 'archived';
  const doubt = fixing ? queues?.doubt ?? null : null;
  const organizations = useCostsOrganizations('choice');
  const create = useCreateInvoice();
  const fromScan = useInvoiceFromScan();
  const scanInput = useRef<HTMLInputElement>(null);
  const toast = useSnackbar();

  const view = useInvoice(selected ?? undefined);
  // «Недавние» Главной: счёт отмечается, когда прочитан, — не найденный в них не попадает.
  useRecentTouch('СчётНаОплату', view.data?.id);
  // Счёт удалили, пока он был открыт (issue #1282): чтение отвечает «нет такого», а прежний вид ещё в
  // руках. Форма остаётся с набранным, а не сменяется отказом чтения.
  //
  // ⚠️ Узнанное запоминается (ревью PR #1298): по одной последней ошибке судить нельзя. Следующее
  // перечитывание — возврат в окно — может кончиться обрывом связи, и «счёта нет» сменилось бы экраном
  // «счёт не открылся» вместе с набранным, ради переноса которого форма и осталась.
  //
  // Тем же правилом форма переживает и любое другое неудачное перечитывание (issue #1287): решает
  // `openRecord`, одно место на счёт и накладную.
  const opened = useOpenRecord(selected, view);
  const gone = opened.kind === 'open' && opened.gone;

  // Открытый счёт под отбором запоминается: после замены значения сервер его под отбором уже не
  // отдаёт, а строка обязана остаться, пока счёт открыт. Запись при отрисовке, а не в эффекте: так
  // список не рисуется кадр без строки.
  const [held, setHeld] = useState<HeldRow | null>(null);
  const nextHeld = heldRow(held, queue, selected, invoices.data);
  if (nextHeld !== held) setHeld(nextHeld);
  // След счёта, разобранного под очередью (issue #1293): строка — путь назад, когда тост уже погас.
  // Живёт до следующего разобранного, до смены отбора и до возврата счёта в список — снимается, а не
  // прячется: спрятанный всплыл бы при возврате к отбору, на устаревшем месте.
  const [parsedTrace, setParsedTrace] = useState<QueueTrace | null>(null);
  const trace = keepTrace(parsedTrace, queue, invoices.data) ? parsedTrace : null;
  if (parsedTrace && !trace) setParsedTrace(null);
  // Отбор «только заведённые этой загрузкой» (issue #1294). Живёт на странице и нигде не хранится:
  // включить его заново — один щелчок в полосе, а переживший перезагрузку отбор без полосы рядом
  // выглядел бы пропажей счетов. Полосу закрыли — снимается и он: управлять им больше нечем.
  const me = useAuth().user?.sub;
  const batch = useScanBatch(me);
  const onlyUpload = filter === 'upload' && batch !== null;
  if (filter === 'upload' && !batch) setFilter(null);
  // Черновик, удаление которого ждёт закрытия снэкбара, уже скрыт (D-13.4): «Вернуть» его вернёт. Скрыт он
  // везде — и в строках, и в полосе загрузки: её число и ссылки не должны вести к уходящему (ревью PR #1808).
  const deleting = usePendingDeletes();
  const shownData = deleting.size ? invoices.data?.filter(i => !deleting.has(i.id)) : invoices.data;
  // Принадлежность загрузке считается в одном месте — `uploadedOf`: и строки, и число в полосе.
  const ofUpload = new Set((uploadedOf(shownData, batch) ?? []).map(i => i.id));
  // Избранные стройки (#1221) урезают только общий список — без чипа и без отбора загрузки.
  const sites = useRailSites({ cuts: filter === null, list: invoices.data, selected, matches: i => matches(i, query) });
  // Открытый счёт под отбором загрузки остаётся строкой, даже если заведён не ею: форма справа без
  // активной строки слева выглядела бы счётом, которого в списке нет. В число он не входит.
  const rows = queueRows(invoices.data ?? [], nextHeld, trace)
    .filter(r => matches(r.item, query) && (!onlyUpload || ofUpload.has(r.item.id) || r.item.id === selected)
      && !sites.pending && !sites.hides(r.item) && !deleting.has(r.item.id));
  // Сколько счетов загрузки в списке. Под включённым отбором — сколько показано: поиск сужает внутри,
  // и полоса не должна называть пять над одной строкой. «Не знаем» — список не пришёл, стоит под чипом
  // (в отобранном сервером списке счетов загрузки может и не быть) либо догоняет: заведённых в нём
  // меньше, чем завела загрузка, и чтение ещё идёт. Просто «перечитывается» незнанием не считается —
  // пока файлы распознаются, список опрашивается каждые пять секунд, и число мигало бы.
  const madeCount = batch?.created.filter(id => !deleting.has(id)).length ?? 0;
  const catchingUp = invoices.isFetching && ofUpload.size < madeCount;
  const listKnown = queue === null && !catchingUp && !invoices.isError && invoices.data !== undefined;
  const found = !listKnown ? null
    : onlyUpload ? rows.filter(r => ofUpload.has(r.item.id)).length : ofUpload.size;
  // Включённый отбор начинает со свежего списка: во время загрузки тот перечитывается не на каждый
  // файл. Эффектом, а не в обработчике щелчка: к этому мигу наблюдатель уже стоит на общем списке, а
  // не на списке чипа, из-под которого отбор включили. Идущее чтение не обрывается. Дальше список
  // ведут перечитывания загрузки и опрос распознавания — на каждый файл его не читаем.
  const refetchList = invoices.refetch;
  useEffect(() => {
    if (onlyUpload) void refetchList({ cancelRefetch: false });
  }, [onlyUpload, refetchList]);

  // Экран, каким он стоит СЕЙЧАС, — для решений, принимаемых после ответа сервера (ревью PR #1303):
  // замыкание помнит миг нажатия, а за время запроса человек мог уйти на другой счёт, снять отбор
  // или начать править. Слойным эффектом — как колбэк охраны ухода: между коммитом и обычным
  // эффектом есть щель, в которую попадает ответ.
  const now = useRef({ queue, selected, query, held: nextHeld, trace });
  useLayoutEffect(() => { now.current = { queue, selected, query, held: nextHeld, trace }; });
  const unsaved = useRef(false);
  const noteUnsaved = useCallback((value: boolean) => { unsaved.current = value; }, []);
  // Сообщение с «Вернуться» живёт дольше, чем страница может простоять на экране.
  const alive = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const navigate = useNavigate();
  // Идёт запись выхода из очереди и выбор следующего счёта: форма на это время заперта (`inert`).
  // «Несохранённое» знает только о шапке и строках, а набрать можно и в оплате, и в разноске, — и
  // смена счёта стёрла бы это молча. Запертая форма закрывает весь класс разом, не перечисляя мест.
  const [leading, setLeading] = useState(false);

  /**
   * «Разобран» и «Отклонить»: запись идёт отсюда. Под отбором очереди форма запирается, а после
   * ответа выбирается следующий счёт — уже не задерживая того, кто звал: его обещание кончается
   * ответом на запись. Сама ничего не сказала (вне отбора, ушли со счёта, сбой) — зовёт `otherwise`.
   * `back` даёт версию для «Вернуть» (D-13.1): тогда снэкбар перехода несёт его вместо «Вернуться».
   */
  async function exiting<T>(
    id: string, number: string | null, how: QueueExit, send: () => Promise<T>,
    otherwise: (result: T, undo?: () => void) => void, back?: (result: T) => string | null,
  ): Promise<T> {
    const undoOf = (result: T) => {
      const version = back?.(result);
      if (!version) return undefined;
      const revert = undoReject(id, version);
      // Как прежний «Вернуться»: под формой с несохранённым соседним счётом — отказ ДО записи. Иначе счёт
      // вернулся бы в черновик, а охрана ухода, отпущенная «Отменой», оставила бы его неоткрытым молча.
      return () => {
        if (alive.current && unsaved.current && now.current.selected !== id) toast.info(undoRefused(number));
        else revert();
      };
    };
    if (now.current.queue !== 'parsing') {
      const result = await send();
      otherwise(result, undoOf(result));
      return result;
    }
    setLeading(true);
    let result: T;
    try { result = await send(); } catch (e) { setLeading(false); throw e; }
    const undo = undoOf(result);
    void leadOn(id, number, how, undo).catch(() => false)
      .then(said => { if (!said) otherwise(result, undo); })
      .finally(() => setLeading(false));
    return result;
  }

  /**
   * После «Разобран»: под отбором очереди — к следующему счёту, с тостом и путём назад.
   *
   * Следующего выбирают по ПЕРЕЧИТАННОМУ списку: тот, что стоял на экране, мог устареть — сосед
   * разобрал или удалил счёт, — и открылся бы не черновик, а то и «счёта нет». Место разобранного в
   * перечитанном списке помнит удержанная строка.
   *
   * @param undo «Вернуть» вышедшего счёта (D-13.1): снэкбар один, и он несёт его вместо «Вернуться».
   * @returns сказано ли о случившемся здесь: нет — говорит тот, кто вывел счёт из очереди.
   */
  async function leadOn(id: string, number: string | null, how: QueueExit, undo?: () => void): Promise<boolean> {
    // Не отменяя начатого: запись уже инвалидировала список, и это чтение присоединяется к тому,
    // что началось после неё, а не шлёт второе.
    const fresh = await invoices.refetch({ cancelRefetch: false });
    const at = now.current;
    // Ушли со счёта или сменили отбор, пока шёл запрос, — человек уже выбрал сам, вести некуда.
    if (at.selected !== id || at.queue !== 'parsing') return false;
    // Место счёта в списке помнит удержанная строка, а у счёта, открытого по строке-следу, — сам след.
    const place = at.held?.item.id === id ? at.held : at.trace?.item.id === id ? at.trace : null;
    const read = !fresh.isError && !!fresh.data;
    // Список не перечитан — в кэше прежний, где вышедший счёт стоит черновиком. Что он вышел,
    // известно точно: убираем его оттуда сами, иначе след тут же снялся бы, а строка осталась «готов».
    if (!read) qc.setQueryData<InvoiceListItem[]>([...INVOICES_KEY, 'list', 'parsing'], list => list?.filter(i => i.id !== id));
    // След ставится и тогда, когда перехода нет: вышедший отсюда счёт подписан одинаково, а
    // прежний след уступает ему место.
    setParsedTrace(place && { queue: place.queue, item: place.item, index: place.index, how });
    // Скрытый черновик, ждущий удаления, следующим не открывается: он вот-вот уйдёт (ревью PR #1808).
    const listed = read ? queueRows(fresh.data, place).filter(r => matches(r.item, at.query) && !pendingIds().has(r.item.id)) : null;
    const after = afterExit(listed, id, number, at.query, unsaved.current, how);
    const undoAction = undo && { duration: SNACKBAR_UNDO_MS, action: { label: 'Вернуть', onClick: undo } };
    if (!after.open) { if (undoAction) toast.success(after.message, undoAction); else toast.info(after.message); return true; }
    setSelected(after.open.id);
    // Фокус на шапку — если человек не печатает в поиске: забирать его посреди ввода нельзя.
    if (!(document.activeElement instanceof HTMLInputElement)) setLed(after.open.id);
    // «Вернуться» уводит со счёта, на который только что перевели, — и о его несохранённом
    // спрашивает так же, как сам переход.
    const back = () => {
      if (!alive.current) { reopen(id); return; }
      if (unsaved.current) { toast.info(backRefused(number, how)); return; }
      setSelected(id);
      setLed(id);
    };
    // Дольше обычного: в сообщении действие, а прочитать надо два номера.
    toast.success(after.message, undoAction ?? { duration: 8000, action: { label: 'Вернуться', onClick: back } });
    return true;
  }

  /** Открыть счёт после «Вернуть»: через охрану ухода; со страницы ушли — адресом, через охрану того раздела. */
  function reopen(id: string) {
    if (alive.current) { switchTo(id); return; }
    const go = () => navigate(`${INVOICE_RECORD.screen}?${INVOICE_RECORD.param}=${id}`);
    if (!guard?.attempt(go)) go();
  }
  const { undoReject, deferDelete } = useInvoiceUndo(id => reopen(id));

  /**
   * Новый черновик — без окна (D-13.4): скрыт сразу, удаление уходит по закрытию снэкбара. Открытый счёт
   * закрывается; под отбором «Разобрать» — к следующему, как после отклонения, и снэкбар называет его.
   */
  function deleteLater(view: InvoiceView) {
    const at = now.current;
    const listed = queueRows(invoices.data ?? [], at.held, at.trace)
      .filter(r => matches(r.item, at.query) && !pendingIds().has(r.item.id));
    const after = at.queue === 'parsing' ? afterExit(listed, view.id, null, at.query, false, 'deleted') : null;
    if (!deferDelete(view, after?.message ?? 'Черновик удалён.')) return;
    if (after?.open) { setSelected(after.open.id); setLed(after.open.id); } else closeInvoice();
  }

  /**
   * Сначала охрана ухода, потом запись (ревью PR #1786): спроси она после — отказ «Отмена» оставлял бы
   * заведённый черновик сиротой, а у файла ещё и снятые отбор и поиск.
   */
  const guarded = (act: () => void) => { if (!guard?.attempt(act)) act(); };

  function addDraft() { guarded(() => void createDraft()); }

  async function createDraft() {
    try {
      // Пустой черновик — осознанно: счёт заводят, чтобы приложить скан и разобрать его, и требовать
      // реквизиты до того, как человек увидел бумагу, — это требовать угадать.
      const created = await create.mutateAsync({});
      // Охрана уже спросила: второй раз, через `switchTo`, она спросила бы о форме, которую человек отпустил.
      setSelected(created.id);
    } catch (e) { toast.apiError(e, 'Черновик не заведён'); }
  }

  /**
   * Счёт из скана (issue #1077): черновик заводится всегда, когда файл годен, распознавание идёт в
   * фоне. Отказ распознавания — не отказ этого действия: о нём говорит полоса в открытом счёте.
   */
  function addFromScan(file: File, acceptRepeat: boolean, unchecked = false) {
    guarded(() => void createFromScan(file, acceptRepeat, unchecked));
  }

  /** `unchecked` — вопрос о повторе не ответил: сказать это можно, только когда черновик заведён (ревью PR #1823). */
  async function createFromScan(file: File, acceptRepeat: boolean, unchecked: boolean) {
    try {
      const created = await fromScan.mutateAsync({ file, acceptRepeat });
      // Заведён из файла этой вкладкой: удалить его, пока не правили, можно без окна (D-13.3).
      markCreatedHere(created.invoice.id);
      // Поиск снимается: у счёта без номера и поставщика ему не за что зацепиться — открылся бы счёт,
      // которого нет в списке. Отбор снимается тоже — кроме «Разобрать»: новый черновик под ним стоит
      // (issue #1292), и выбрасывать человека из рабочей очереди незачем.
      if (queue !== 'parsing') setQueue(null);
      setQuery('');
      setSelected(created.invoice.id);
      if (unchecked) toast.info(UNCHECKED_NOTE);
    } catch (e) { toast.apiError(e, 'Счёт из файла не заведён'); }
  }

  /**
   * Несколько файлов разом (issue #1093) — по черновику на файл, и экран при этом НЕ двигается
   * (решение владельца 09.10.2026): открытый счёт, отбор и поиск остаются, о заведённых говорит
   * полоса над списком. Решает число выбранных файлов, а не принятых: один — открывает свой черновик.
   */
  const qc = useQueryClient();
  /**
   * Закрыть счёт, которого больше нет, — удалённый здесь или соседом. Сначала адрес, потом кэш: счёт,
   * выброшенный из кэша при открытой форме, был бы тут же запрошен заново. Следующий счёт сам не
   * открывается — выбор за человеком.
   *
   * <p>Кэш чистится при ЛЮБОМ уходе с такого счёта (ревью PR #1298), а не только по кнопке: щелчок по
   * другой строке или «назад» иначе оставили бы удалённый счёт строкой списка.</p>
   */
  const closeGone = useCloseGone(selected, gone, id => setParams(next => {
    if (next.get(INVOICE_RECORD.param) === id) next.delete(INVOICE_RECORD.param);
  }, { replace: true }), id => { forgetCreated(id); forgetInvoice(qc, id); });
  // Что распознаётся, говорит реестр видов: из него и выбор файла, и подсказка, и отсев до отправки.
  const kinds = useFileKinds();
  const readable = recognizedKinds(kinds.data);
  // Тот же счёт, что в форме: панель скана и форма обязаны говорить об одном (ревью PR #1300).
  const shown = opened.kind === 'open' ? opened.data : null;
  const scan = shown ? shown.requisites[K.scan] : null;
  const hasScan = scan != null && typeof scan === 'object';

  // Место (D-10.3, ТЗ COST-6.3): пока открыт счёт, подпанели раздела нет (D-10.5), а список и файл встают
  // по ширине рабочей области. Файл поверх формы — только по нажатию «Файл»: открытый сам, он закрывал бы
  // форму при каждом открытии счёта на 1024.
  useSuppressSectionPanel(selected !== null);
  const [remembered, setRemembered] = useSyncedPreference('invoiceFile', 'crg-invoice-file', 'open');
  const [chosen, setChosen] = useState<{ id: string; open: boolean } | null>(null);
  const listed = invoices.data?.find(i => i.id === selected);
  const draft = !!shown && shown.requisites[K.state] !== PARSED && shown.requisites[K.state] !== REJECTED;
  const wanted = fileWanted({
    hasFile: hasScan && !gone,
    needsPaper: draft && (shown.unconfirmed.length > 0 || listed?.recognition?.state === 'failed'),
    remembered: remembered !== 'closed',
    chosen: chosen?.id === selected ? chosen.open : null,
  });
  const room = placeOf(workWidth, true).file === 'beside';
  const place = placeOf(workWidth, wanted && room);
  const listShown = opened.kind !== 'open' || place.list;
  // Бросок на всё окно, кнопка и палитра — одним путём (costs-invoice-screen-14, D1, D3): проверки до выгрузки,
  // окно повтора для одного файла, пакет — для нескольких.
  const intake = useInvoiceFileIntake({
    enabled: hasPermission(access, 'costs.invoice.edit'), owner: me ?? '', batch,
    opened: opened.kind === 'open' ? { number: asInput(opened.data.requisites[K.number]) || null, hasFile: hasScan } : null,
    listShown, sending: fromScan.isPending,
    onSingle: addFromScan,
    onOpen: switchTo,
    // Новая загрузка — отбор прежней снимается: под ним новые черновики не появились бы.
    onBatchStart: () => { if (filter === 'upload') setFilter(null); },
  });
  const { addScans, loading } = intake;
  // Те же две кнопки из командной палитры (issue #1433): «Новый счёт» и «Счёт из файла» из любого места —
  // и закрыты тогда же, когда закрыты кнопки.
  usePaletteIntent(PALETTE_ACTIONS.invoiceNew, () => void addDraft(), { busy: create.isPending });
  usePaletteIntent(PALETTE_ACTIONS.invoiceScan, () => scanInput.current?.click(), { busy: loading });

  const toggleFile = () => {
    if (!selected) return;
    const open = place.file !== 'beside';
    setChosen({ id: selected, open });
    setRemembered(open ? 'open' : 'closed');
  };
  // «Показать на скане» у пункта перечня о метках (11, D-11.4): закрытый файл — открыть рядом или поверх, открытый рядом —
  // перевести фокус в его панель.
  const fileShow = useRef<FileShow>(null);
  const filePane = useRef<HTMLElement>(null);
  // Фокус в панель — с кольцом: без него щелчок по метке при открытом файле ничего видимого не делал (ревью
  // PR #1794). Кольцо — только у такого показа: щелчок внутри панели её не обводит.
  const [paneShown, setPaneShown] = useState(false);
  const revealPane = () => { setPaneShown(true); filePane.current?.focus(); };
  const position = selected ? positionOf(rows.map(r => r.item.id), selected) : null;
  const filterName = filter === 'upload' ? 'Эта загрузка' : queue ? QUEUE_LABEL[queue] : 'Все';
  const closeInvoice = () => {
    const go = () => setParams(next => next.delete(INVOICE_RECORD.param), { replace: true });
    if (!guard?.attempt(go)) go();
  };
  const canEdit = hasPermission(access, 'costs.invoice.edit');
  // О броске до броска говорят пустые состояния (shell-07): цель — всё окно, отдельного места под неё нет.
  const invited = dropInvited({ canEdit, coarse: useMedia(COARSE), info: kinds.data });
  const actions = pageActions({
    articles: hasPermission(access, 'costs.articles.edit'), edit: canEdit, creating: create.isPending,
    onArticles: () => setArticlesOpen(true), onMatches: () => setMatchesOpen(true),
    onTerms: () => setTermsOpen(true), onNew: () => void addDraft(),
  });
  const batchBar = (
    <InvoiceScanBatchBar batch={batch} only={onlyUpload} found={found} alive={aliveIds(shownData, listKnown, batch)}
      // Включение снимает чип (состояние одно) и поиск: одним действием видны ровно те счета.
      onOnly={() => { setQuery(''); setFilter('upload'); }}
      onShowAll={() => setFilter(null)} onRetry={() => retryRejected(intake.port)} onOpen={switchTo}
      onAccept={print => acceptHeld(print, intake.port)} />
  );
  const fromFileTitle = fromFileWords(readable);
  // При открытом счёте вход — в сводке, всегда (D7 у 14): в шапке списка его нет, кнопка не прыгает по ширине.
  const fromFile = canEdit
    ? <FromFileButton loading={loading} title={fromFileTitle} onClick={() => scanInput.current?.click()} />
    : null;

  return (
    <div ref={measure} className="h-full">
    {intake.layer}
    <ListDetailShell view={ld} bare={selected !== null} navHidden={!listShown}
      title="Счета на оплату"
      subtitle="Черновик сохраняется без строк и сразу попадает в список"
      actions={
        <>
          {/* Справочник статей вне строек (F3) — у того, кто его ведёт, а не у всех: остальным он виден
              только в выборе цели разноски. */}
          {hasPermission(access, 'costs.articles.edit') && (
            <Button variant="outlined" icon={<Icon icon={Sell} size={18} />} onClick={() => setArticlesOpen(true)}>
              Статьи вне строек
            </Button>
          )}
          {/* Заводит счета тот, кто их вводит: бухгалтеру кнопка была бы дверью в отказ (N1, #1102). */}
          {/* Главная — «Счёт из скана» (решение владельца 08.10.2026): счёт приезжает бумагой, и
              основной путь — от неё. */}
          {hasPermission(access, 'costs.invoice.edit') && (
            <>
              {/* Соответствия наименований (C3, #1079) — у того, кто вводит счета: он их и запоминает. */}
              <Button variant="outlined" icon={<Icon icon={LinkIcon} size={18} />} onClick={() => setMatchesOpen(true)}>
                Соответствия
              </Button>
              {/* Условия поставщиков (C4, #1080) — рядом с соответствиями и у того же человека: отсрочку
                  в счёт подставляют они, и видит, что подставлять нечего, тот, кто вводит счета. */}
              <Button variant="outlined" icon={<Icon icon={CalendarClock} size={18} />} onClick={() => setTermsOpen(true)}>
                Условия поставщиков
              </Button>
              <Button variant="outlined" icon={<Icon icon={Add} size={18} />} loading={create.isPending} onClick={addDraft}>
                Новый счёт
              </Button>
              {/* На телефоне это действие — плавающая кнопка (#1434): кнопка сама там прячется. */}
              <PrimaryActionButton action={PALETTE_ACTIONS.invoiceScan} loading={loading}
                title={fromFileTitle}
                onClick={() => scanInput.current?.click()} />
              {/* Только то, что распознаётся: файл другого вида сервер отверг бы, и выбор его здесь
                  был бы дверью в отказ. */}
              <input ref={scanInput} type="file" multiple className="hidden" accept={acceptOf(readable) || undefined}
                onChange={e => {
                  const files = [...e.target.files ?? []];
                  e.target.value = '';
                  if (files.length > 0) addScans(files);
                }} />
            </>
          )}
          {articlesOpen && <ArticlesDialog onClose={() => setArticlesOpen(false)} />}
          {matchesOpen && <SupplierMatchesDialog onClose={() => setMatchesOpen(false)} />}
          {termsOpen && <SupplierTermsDialog onClose={() => setTermsOpen(false)} />}
        </>
      }
      nav={
        // Колонка больше не отдельная цель броска (D1 у 14): цель — всё окно, её держит захват окна.
        <div className="relative flex-1 min-h-0 flex flex-col">
          {/* Счёт открыт — шапки страницы нет (D-10.8): заголовок и её действия встают над списком. */}
          {/* Счёт ещё грузится, не найден или не прочитан — сводки нет, и «Счёт из файла» встаёт сюда (ревью PR #1823). */}
          {selected !== null && <InvoiceListHeader actions={actions} fromFile={opened.kind === 'open' ? null : fromFile} />}
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, поставщик, назначение…" />
          <InvoiceQueueChips queue={queue} onChange={setQueue} queues={queues} parsing={parsing}
            failed={counts.isError}
            onRetry={() => void counts.refetch()}
            // Под очередью и отбором загрузки рейл не урезается (#1221) — и отбора «Стройки» там нет.
            // Под поиском число — сколько скрытых нашёл бы он: иначе оно звало бы искать там, где нет.
            sites={filter === null ? <SitesFilter hidden={sites.foundHidden} /> : undefined} />
          {listShown && batchBar}
          <div className="flex-1 overflow-y-auto">
            {doubt && (
              <p className="px-3 py-1.5 text-body-small text-warning" role="status">
                {doubt[0].toUpperCase() + doubt.slice(1)}. Список может быть неполным.
              </p>
            )}
            {invoices.isPending && <p className="px-3 py-2 text-body-small text-on-surface-variant">Загрузка…</p>}
            {invoices.isError && (
              <p className="px-3 py-2 text-body-small text-error">
                Список не пришёл. Это отказ чтения, а не пустой список: счета могут быть.
              </p>
            )}
            {/* Под отбором загрузки «пусто» говорится только по свежему списку: прежний, из кэша,
                её счетов ещё не знает, и «не осталось» было бы неправдой до ответа сервера. */}
            {onlyUpload && rows.length === 0 && catchingUp && !invoices.isPending && (
              <p className="px-3 py-2 text-body-small text-on-surface-variant">Загрузка…</p>
            )}
            {/* Фильтр ещё не известен — строк нет: ни полных, ни урезанных (см. SiteFilterValue). */}
            {sites.pending && <p className="px-3 py-2 text-body-small text-on-surface-variant">Загрузка…</p>}
            {/* Счета есть, но скрыты фильтром — сказать это ПЕРЕД «Счетов пока нет», которое было бы неправдой. */}
            {!invoices.isPending && !invoices.isError && !sites.pending && rows.length === 0 && filter === null && (
              <RailSitesEmpty searching={!!query.trim()} hidden={sites.hidden} foundHidden={sites.foundHidden}
                onShowAll={sites.showAll} />
            )}
            {!invoices.isPending && !invoices.isError && !sites.pending && rows.length === 0 && !(onlyUpload && catchingUp)
              && !(filter === null && (query.trim() ? sites.foundHidden : sites.hidden) > 0) && (
              <div className="px-3 py-2 text-body-small text-on-surface-variant space-y-1">
                {/* «Нажмите «Счёт из файла»» — только без открытого счёта: при нём кнопки с этой подписью нет, вход — значком. */}
                <p>{onlyUpload ? uploadEmpty(query) : emptyText(queue, query, queues, invited && selected === null)}</p>
                {(onlyUpload || (queue && !query.trim())) && (
                  <button type="button" className="underline hover:text-on-surface" onClick={() => setFilter(null)}>
                    Показать все счета
                  </button>
                )}
              </div>
            )}
            {rows.map(({ item, left, exit }) => left ? (
              // Номер — у открытого счёта: строка списка осталась снимком до правки.
              <InvoiceLeftRow key={item.id} onClick={() => switchTo(item.id)}
                exit={exit} active={item.id === selected}
                number={shown?.id === item.id ? asInput(shown.requisites[K.number]) : item.number} />
            ) : (
              <InvoiceListRow key={item.id} item={item} active={item.id === selected} queue={queue}
                onClick={() => switchTo(item.id)} />
            ))}
            {/* Запертые — строкой, а не кнопкой: открыть их можно, исправить нельзя. Под пустым списком
                о них уже сказано словами пустого состояния. Числа не пришли — так и говорим: молчание
                читалось бы как «запертых нет». */}
            {queue === 'lost' && rows.length > 0 && (queues
              ? queues.locked > 0 && <p className="px-3 py-2 text-body-small text-on-surface-variant">{lockedNote(queues.locked)}</p>
              : !counts.isPending && (
                <p className="px-3 py-2 text-body-small text-warning">
                  Есть ли счета закрытого периода с удалёнными записями — не посчитано.
                </p>
              ))}
          </div>
        </div>
      }
      detail={
        // ⚠️ Пять положений, а не одно — см. `openRecord`. «Ничего не выбрано», «счёт грузится», «счёта
        // нет», «счёт не пришёл» и «счёт открыт» — разные вещи: отказ чтения, показанный приглашением
        // выбрать счёт, выглядит как будто человек никуда не нажимал, и повторное нажатие по той же
        // строке ничего не меняет.
        opened.kind === 'none' ? (
          <div className="flex-1 grid place-items-center">
            <EmptyState icon={<Icon icon={Description} size={40} />} title="Выберите счёт"
              description={selectHint(invited, readable)} />
          </div>
        ) : opened.kind === 'loading' ? (
          <div className="flex-1 grid place-items-center text-body-small text-on-surface-variant">Счёт загружается…</div>
        ) : opened.kind === 'missing' ? (
          // Мёртвая ссылка. «Повторить» здесь нет: повтор ответит тем же.
          <div className="flex-1 grid place-items-center p-6">
            <p className="text-body-medium text-on-surface-variant" data-testid="invoice-missing">Счёта нет: его удалили или ссылка неверна.</p>
          </div>
        ) : opened.kind === 'unread' ? (
          // Только для счёта, не прочитанного НИ РАЗУ: неудачное перечитывание открытого форму не снимает.
          <div className="flex-1 grid place-items-center p-6">
            <div className="max-w-md text-center space-y-2">
              <p className="text-body-medium text-error">Счёт не открылся: {apiError(opened.error, 'сервер отказал')}</p>
              <p className="text-body-small text-on-surface-variant">
                Это отказ чтения, а не пустой счёт — сам счёт в списке остался.
              </p>
              <Button variant="outlined" onClick={() => void view.refetch()}>Повторить</Button>
            </div>
          </div>
        ) : (
          <div className="flex-1 min-w-0 min-h-0 flex flex-col" inert={leading}>
            {/* Списка нет (файл рядом или места мало) — ход и итог пакета в центре активности (D5 у 14), а не
                полосой над формой: пакет экран не двигает и высоту у формы не отнимает. */}
            <div className="flex-1 min-w-0 min-h-0 flex">
              <InvoiceForm
                key={opened.id}
                view={opened.data}
                organizations={organizations.data ?? []}
                organizationsError={organizations.isError ? organizations.error : undefined}
                onRetryOrganizations={() => void organizations.refetch()}
                // «Скрыт отбором» и «не перечитался» — пункты индикатора состояния формы (11), а не полосы над ней.
                hiddenByFilter={sites.openHidden ? sites.showAll : undefined}
                rereadFailed={opened.rereadFailed != null
                  ? { error: apiError(opened.rereadFailed, 'сервер отказал'), retrying: view.isFetching, onRetry: () => void view.refetch() }
                  : undefined}
                onOpenInvoice={switchTo}
                compactLines={place.list || place.file === 'beside'}
                nav={!place.list && position
                  ? <InvoiceStepper position={position} filter={filterName} onGo={switchTo} onList={closeInvoice} />
                  : undefined}
                fromFile={fromFile}
                file={hasScan && !gone
                  ? <InvoiceFileButton invoiceId={opened.id} blobPath={fileProp(scan, 'blobPath')}
                      fileName={fileProp(scan, 'fileName')} onToggle={toggleFile} onReveal={revealPane} showRef={fileShow}
                      file={!room ? 'over' : place.file === 'beside' ? 'beside' : 'closed'} />
                  : undefined}
                onShowFile={hasScan && !gone ? () => fileShow.current?.show() : undefined}
                pageActions={place.list ? undefined : actions}
                onExit={(how, send, otherwise, back) => exiting(
                  opened.id, asInput(opened.data.requisites[K.number]) || null, how, send, otherwise, back)}
                onDeleteLater={deleteLater}
                focusHeader={led === opened.id}
                onUnsaved={noteUnsaved} onFlush={noteFlush}
                gone={gone} onClose={() => closeGone(opened.id)}
              />
              {/* Ширина панели — по месту (D-10.3): 38 % рабочей области, от 420 до 520. Форма — главное,
                  файл — опора, и рядом он стоит, только пока форме остаётся не меньше 720. Задаёт её CSS:
                  замер ширины между сменами места устаревает нарочно (`useWorkWidth`). */}
              {place.file === 'beside' && (
                <aside ref={filePane} tabIndex={-1} aria-label="Файл счёта" style={{ width: FILE_WIDTH_CSS }}
                  onBlur={() => setPaneShown(false)}
                  className={`shrink-0 outline-none ${paneShown ? 'inset-ring-2 inset-ring-primary' : ''} border-l border-outline-variant flex flex-col min-h-0`}>
                  <InvoiceScanPanel
                    invoiceId={opened.id}
                    blobPath={fileProp(scan, 'blobPath')}
                    fileName={fileProp(scan, 'fileName')}
                  />
                </aside>
              )}
            </div>
          </div>
        )
      }
    />
    </div>
  );
}

function matches(item: InvoiceListItem, query: string): boolean {
  const text = query.trim().toLowerCase();
  if (!text) return true;
  // И по имени файла скана: у счёта без номера оно стоит заголовком строки.
  return [item.number, item.supplierName, item.purpose, item.scanFileName]
    .some(value => (value ?? '').toLowerCase().includes(text));
}

function fileProp(node: unknown, key: string): string | null {
  const value = (node as Record<string, unknown> | null)?.[key];
  return typeof value === 'string' ? value : null;
}
