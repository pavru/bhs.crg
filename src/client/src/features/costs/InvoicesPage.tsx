import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router';
import { FileText, Link2, Plus, ScanLine, Tags } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { useToast } from '@/shared/ui/Toast';
import { useNavigationGuard } from '@/shared/ui/NavigationGuard';
import { useCloseGone } from '@/shared/hooks/useCloseGone';
import { useOpenRecord } from './openRecord';
import { RereadFailedNotice } from './RereadFailedNotice';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import {
  INVOICES_KEY, forgetInvoice, scansRunning, useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
  type InvoiceListItem, type InvoiceQueue,
} from '@/shared/api/invoices';
import { useInvoiceQueues, useParsingCount } from '@/shared/api/invoiceQueues';
import { refreshInvoiceLists, sendScan, useInvoiceFromScan } from '@/shared/api/invoiceRecognition';
import { acceptOf, recognizedKinds, useFileKinds, wordsOf } from '@/shared/api/fileKinds';
import { useAuth } from '@/shared/hooks/useAuth';
import { ArticlesDialog } from './ArticlesDialog';
import { SupplierMatchesDialog } from './SupplierMatchesDialog';
import { InvoiceForm } from './InvoiceForm';
import { InvoiceLeftRow, InvoiceListRow } from './InvoiceListRow';
import { InvoiceQueueChips } from './InvoiceQueueChips';
import {
  afterExit, backRefused, emptyText, heldRow, keepTrace, lockedNote, queueRows,
  type HeldRow, type QueueExit, type QueueTrace,
} from './invoiceQueues';
import { InvoiceScanBatchBar, InvoiceScanDrop } from './InvoiceScanDrop';
import { InvoiceScanPanel, ScanTooNarrow } from './InvoiceScanPanel';
import { K, asInput, scanFitsBeside } from './invoiceFields';
import { useQueuesFollowScans } from './recognitionWatch';
import {
  forgetCreated, retryRejected, startBatch, uploadEmpty, uploadedOf, useScanBatch, type BatchPort,
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
  const [params, setParams] = useSearchParams();
  const selected = params.get(INVOICE_RECORD.param) || null;
  // Счёт, на который привёл переход очереди: его форма при открытии берёт фокус на шапку. Любой
  // другой выбор счёта это снимает — иначе счёт забирал бы фокус при каждом следующем открытии.
  const [led, setLed] = useState<string | null>(null);
  const setSelected = (id: string) => {
    setLed(null);
    setParams(prev => {
      const next = new URLSearchParams(prev);
      next.set(INVOICE_RECORD.param, id);
      return next;
    }, { replace: true });
  };
  const [query, setQuery] = useState('');
  // Отбор списка — ОДНО состояние (ревью PR #1304): чип очереди либо «только счета этой загрузки»
  // (issue #1294). Двух сразу не бывает по устройству, а не по дисциплине вызовов: чип считает
  // сервер, загрузку — вкладка, и их пересечение дало бы пустой список без объяснения.
  const [filter, setFilter] = useState<InvoiceQueue | 'upload' | null>(null);
  const queue = filter === 'upload' ? null : filter;
  const setQueue: (next: InvoiceQueue | null) => void = setFilter;
  const wide = useWideEnoughForScan();
  const { data: access = NO_ACCESS } = useAccess();
  const [articlesOpen, setArticlesOpen] = useState(false);
  const [matchesOpen, setMatchesOpen] = useState(false);

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
  const toast = useToast();

  const view = useInvoice(selected ?? undefined);
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
  // Принадлежность загрузке считается в одном месте — `uploadedOf`: и строки, и число в полосе.
  const ofUpload = new Set((uploadedOf(invoices.data, batch) ?? []).map(i => i.id));
  // Открытый счёт под отбором загрузки остаётся строкой, даже если заведён не ею: форма справа без
  // активной строки слева выглядела бы счётом, которого в списке нет. В число он не входит.
  const rows = queueRows(invoices.data ?? [], nextHeld, trace)
    .filter(r => matches(r.item, query) && (!onlyUpload || ofUpload.has(r.item.id) || r.item.id === selected));
  // Сколько счетов загрузки в списке. Под включённым отбором — сколько показано: поиск сужает внутри,
  // и полоса не должна называть пять над одной строкой. «Не знаем» — список не пришёл, стоит под чипом
  // (в отобранном сервером списке счетов загрузки может и не быть) либо догоняет: заведённых в нём
  // меньше, чем завела загрузка, и чтение ещё идёт. Просто «перечитывается» незнанием не считается —
  // пока файлы распознаются, список опрашивается каждые пять секунд, и число мигало бы.
  const madeCount = batch?.created.length ?? 0;
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
  const guard = useNavigationGuard();
  // Идёт запись выхода из очереди и выбор следующего счёта: форма на это время заперта (`inert`).
  // «Несохранённое» знает только о шапке и строках, а набрать можно и в оплате, и в разноске, — и
  // смена счёта стёрла бы это молча. Запертая форма закрывает весь класс разом, не перечисляя мест.
  const [leading, setLeading] = useState(false);

  /**
   * «Разобран» и «Отклонить»: запись идёт отсюда. Под отбором очереди форма запирается, а после
   * ответа выбирается следующий счёт — уже не задерживая того, кто звал: его обещание кончается
   * ответом на запись. Сама ничего не сказала (вне отбора, ушли со счёта, сбой) — зовёт `otherwise`.
   */
  async function exiting<T>(
    id: string, number: string | null, how: QueueExit, send: () => Promise<T>, otherwise: (result: T) => void,
  ): Promise<T> {
    if (now.current.queue !== 'parsing') {
      const result = await send();
      otherwise(result);
      return result;
    }
    setLeading(true);
    let result: T;
    try { result = await send(); } catch (e) { setLeading(false); throw e; }
    void leadOn(id, number, how).catch(() => false)
      .then(said => { if (!said) otherwise(result); })
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
   * @returns сказано ли о случившемся здесь: нет — говорит тот, кто вывел счёт из очереди.
   */
  async function leadOn(id: string, number: string | null, how: QueueExit): Promise<boolean> {
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
    const listed = read ? queueRows(fresh.data, place).filter(r => matches(r.item, at.query)) : null;
    const after = afterExit(listed, id, number, at.query, unsaved.current, how);
    if (!after.open) { toast.info(after.message); return true; }
    setSelected(after.open.id);
    // Фокус на шапку — если человек не печатает в поиске: забирать его посреди ввода нельзя.
    if (!(document.activeElement instanceof HTMLInputElement)) setLed(after.open.id);
    // «Вернуться» уводит со счёта, на который только что перевели, — и о его несохранённом
    // спрашивает так же, как сам переход.
    const back = () => {
      if (!alive.current) {
        // Со страницы уже ушли: возвращаемся адресом, через охрану ухода того раздела, где человек сейчас.
        const go = () => navigate(`${INVOICE_RECORD.screen}?${INVOICE_RECORD.param}=${id}`);
        if (!guard?.attempt(go)) go();
        return;
      }
      if (unsaved.current) { toast.info(backRefused(number, how)); return; }
      setSelected(id);
      setLed(id);
    };
    // Дольше обычного: в сообщении действие, а прочитать надо два номера.
    toast.success(after.message, { duration: 8000, action: { label: 'Вернуться', onClick: back } });
    return true;
  }

  async function addDraft() {
    try {
      // Пустой черновик — осознанно: счёт заводят, чтобы приложить скан и разобрать его, и требовать
      // реквизиты до того, как человек увидел бумагу, — это требовать угадать.
      const created = await create.mutateAsync({});
      setSelected(created.id);
    } catch (e) { toast.apiError(e, 'Черновик не заведён'); }
  }

  /**
   * Счёт из скана (issue #1077): черновик заводится всегда, когда файл годен, распознавание идёт в
   * фоне. Отказ распознавания — не отказ этого действия: о нём говорит полоса в открытом счёте.
   */
  async function addFromScan(file: File) {
    try {
      const created = await fromScan.mutateAsync(file);
      // Поиск снимается: у счёта без номера и поставщика ему не за что зацепиться — открылся бы счёт,
      // которого нет в списке. Отбор снимается тоже — кроме «Разобрать»: новый черновик под ним стоит
      // (issue #1292), и выбрасывать человека из рабочей очереди незачем.
      if (queue !== 'parsing') setQueue(null);
      setQuery('');
      setSelected(created.invoice.id);
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
  const closeGone = useCloseGone(selected, gone, id => setParams(prev => {
    if (prev.get(INVOICE_RECORD.param) !== id) return prev;
    const next = new URLSearchParams(prev);
    next.delete(INVOICE_RECORD.param);
    return next;
  }, { replace: true }), id => { forgetCreated(id); forgetInvoice(qc, id); });
  const loading = fromScan.isPending || (batch !== null && batch.phase !== 'done');
  // Что распознаётся, говорит реестр видов: из него и выбор файла, и подсказка, и отсев до отправки.
  const kinds = useFileKinds();
  const readable = recognizedKinds(kinds.data);
  const port: BatchPort = {
    owner: me ?? '',
    kinds: kinds.data,
    send: file => sendScan(qc, file).then(created => created.invoice),
    refresh: () => refreshInvoiceLists(qc),
  };
  function addScans(files: File[], folders: string[] = []) {
    if (files.length === 1 && folders.length === 0) void addFromScan(files[0]);
    else {
      // Новая загрузка — отбор прежней снимается: под ним новые черновики не появились бы.
      if (filter === 'upload') setFilter(null);
      startBatch(files, folders, port);
    }
  }

  // Тот же счёт, что в форме: панель скана и форма обязаны говорить об одном (ревью PR #1300).
  const shown = opened.kind === 'open' ? opened.data : null;
  const scan = shown ? shown.requisites[K.scan] : null;
  const hasScan = scan != null && typeof scan === 'object';

  return (
    <ListDetailShell
      title="Счета на оплату"
      subtitle="Черновик сохраняется без строк и сразу попадает в список"
      headerAction={
        <div className="flex items-center gap-2">
          {/* Справочник статей вне строек (F3) — у того, кто его ведёт, а не у всех: остальным он виден
              только в выборе цели разноски. */}
          {hasPermission(access, 'costs.articles.edit') && (
            <Button variant="outlined" icon={<Tags size={16} />} onClick={() => setArticlesOpen(true)}>
              Статьи вне строек
            </Button>
          )}
          {/* Заводит счета тот, кто их вводит: бухгалтеру кнопка была бы дверью в отказ (N1, #1102). */}
          {/* Главная — «Счёт из скана» (решение владельца 08.10.2026): счёт приезжает бумагой, и
              основной путь — от неё. */}
          {hasPermission(access, 'costs.invoice.edit') && (
            <>
              {/* Соответствия наименований (C3, #1079) — у того, кто вводит счета: он их и запоминает. */}
              <Button variant="outlined" icon={<Link2 size={16} />} onClick={() => setMatchesOpen(true)}>
                Соответствия
              </Button>
              <Button variant="outlined" icon={<Plus size={16} />} loading={create.isPending} onClick={addDraft}>
                Новый счёт
              </Button>
              <Button variant="filled" icon={<ScanLine size={16} />} loading={loading}
                title={`Один или несколько файлов${readable.length ? `: ${wordsOf(readable)}` : ''}. Файлы можно перетащить на список счетов.`}
                onClick={() => scanInput.current?.click()}>
                Счёт из файла
              </Button>
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
        </div>
      }
      nav={
        <InvoiceScanDrop enabled={hasPermission(access, 'costs.invoice.edit') && !loading} onFiles={addScans}>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, поставщик, назначение…" />
          <InvoiceQueueChips queue={queue} onChange={setQueue} queues={queues} parsing={parsing}
            failed={counts.isError}
            onRetry={() => void counts.refetch()} />
          <InvoiceScanBatchBar batch={batch} only={onlyUpload} found={found}
            // Включение снимает чип (состояние одно) и поиск: одним действием видны ровно те счета.
            onOnly={() => { setQuery(''); setFilter('upload'); }}
            onShowAll={() => setFilter(null)} onRetry={() => retryRejected(port)} onOpen={setSelected} />
          <div className="flex-1 overflow-y-auto">
            {doubt && (
              <p className="px-3 py-1.5 text-xs text-warning" role="status">
                {doubt[0].toUpperCase() + doubt.slice(1)}. Список может быть неполным.
              </p>
            )}
            {invoices.isPending && <p className="px-3 py-2 text-xs text-fg3">Загрузка…</p>}
            {invoices.isError && (
              <p className="px-3 py-2 text-xs text-danger">
                Список не пришёл. Это отказ чтения, а не пустой список: счета могут быть.
              </p>
            )}
            {/* Под отбором загрузки «пусто» говорится только по свежему списку: прежний, из кэша,
                её счетов ещё не знает, и «не осталось» было бы неправдой до ответа сервера. */}
            {onlyUpload && rows.length === 0 && catchingUp && !invoices.isPending && (
              <p className="px-3 py-2 text-xs text-fg3">Загрузка…</p>
            )}
            {!invoices.isPending && !invoices.isError && rows.length === 0 && !(onlyUpload && catchingUp) && (
              <div className="px-3 py-2 text-xs text-fg3 space-y-1">
                <p>{onlyUpload ? uploadEmpty(query) : emptyText(queue, query, queues)}</p>
                {(onlyUpload || (queue && !query.trim())) && (
                  <button type="button" className="underline hover:text-fg1" onClick={() => setFilter(null)}>
                    Показать все счета
                  </button>
                )}
              </div>
            )}
            {rows.map(({ item, left, exit }) => left ? (
              // Номер — у открытого счёта: строка списка осталась снимком до правки.
              <InvoiceLeftRow key={item.id} onClick={() => setSelected(item.id)}
                exit={exit} active={item.id === selected}
                number={shown?.id === item.id ? asInput(shown.requisites[K.number]) : item.number} />
            ) : (
              <InvoiceListRow key={item.id} item={item} active={item.id === selected} queue={queue}
                onClick={() => setSelected(item.id)} />
            ))}
            {/* Запертые — строкой, а не кнопкой: открыть их можно, исправить нельзя. Под пустым списком
                о них уже сказано словами пустого состояния. Числа не пришли — так и говорим: молчание
                читалось бы как «запертых нет». */}
            {queue === 'lost' && rows.length > 0 && (queues
              ? queues.locked > 0 && <p className="px-3 py-2 text-xs text-fg3">{lockedNote(queues.locked)}</p>
              : !counts.isPending && (
                <p className="px-3 py-2 text-xs text-warning">
                  Есть ли счета закрытого периода с удалёнными записями — не посчитано.
                </p>
              ))}
          </div>
        </InvoiceScanDrop>
      }
      detail={
        // ⚠️ Пять положений, а не одно — см. `openRecord`. «Ничего не выбрано», «счёт грузится», «счёта
        // нет», «счёт не пришёл» и «счёт открыт» — разные вещи: отказ чтения, показанный приглашением
        // выбрать счёт, выглядит как будто человек никуда не нажимал, и повторное нажатие по той же
        // строке ничего не меняет.
        opened.kind === 'none' ? (
          <div className="flex-1 grid place-items-center">
            <EmptyState icon={<FileText size={28} />} title="Выберите счёт"
              description="Или заведите новый — черновик сохранится пустым." />
          </div>
        ) : opened.kind === 'loading' ? (
          <div className="flex-1 grid place-items-center text-xs text-fg3">Счёт загружается…</div>
        ) : opened.kind === 'missing' ? (
          // Мёртвая ссылка. «Повторить» здесь нет: повтор ответит тем же.
          <div className="flex-1 grid place-items-center p-6">
            <p className="text-sm text-fg2" data-testid="invoice-missing">Счёта нет: его удалили или ссылка неверна.</p>
          </div>
        ) : opened.kind === 'unread' ? (
          // Только для счёта, не прочитанного НИ РАЗУ: неудачное перечитывание открытого форму не снимает.
          <div className="flex-1 grid place-items-center p-6">
            <div className="max-w-md text-center space-y-2">
              <p className="text-sm text-danger">Счёт не открылся: {apiError(opened.error, 'сервер отказал')}</p>
              <p className="text-xs text-fg3">
                Это отказ чтения, а не пустой счёт — сам счёт в списке остался.
              </p>
              <Button size="sm" variant="outlined" onClick={() => void view.refetch()}>Повторить</Button>
            </div>
          </div>
        ) : (
          <div className="flex-1 min-w-0 min-h-0 flex flex-col" inert={leading}>
            {opened.rereadFailed != null && (
              <RereadFailedNotice what="Счёт не перечитался" error={opened.rereadFailed}
                retrying={view.isFetching} onRetry={() => void view.refetch()} />
            )}
            <div className="flex-1 min-w-0 min-h-0 flex">
              <InvoiceForm
                key={opened.id}
                view={opened.data}
                organizations={organizations.data ?? []}
                organizationsError={organizations.isError ? organizations.error : undefined}
                onOpenInvoice={setSelected}
                onExit={(how, send, otherwise) => exiting(
                  opened.id, asInput(opened.data.requisites[K.number]) || null, how, send, otherwise)}
                focusHeader={led === opened.id}
                onUnsaved={noteUnsaved}
                gone={gone} onClose={() => closeGone(opened.id)}
                scanSlot={hasScan && !wide && !gone
                  ? <ScanTooNarrow invoiceId={opened.id} blobPath={fileProp(scan, 'blobPath')}
                      fileName={fileProp(scan, 'fileName')} width={window.innerWidth} />
                  : undefined}
              />
              {/* Ширина панели: форма — главное, скан — опора. Отсюда 40 % и потолок: на широком
                  экране бумага не должна отъедать место у полей, которые правят. */}
              {hasScan && wide && !gone && (
                <aside className="w-[40%] max-w-[620px] min-w-[340px] shrink-0 border-l border-stroke flex flex-col min-h-0">
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

/**
 * Хватает ли ширины окна на скан РЯДОМ с формой (решение владельца 28.09.2026: минимум 1280).
 *
 * Слушаем `resize`, а не спрашиваем один раз: окно меняют мышью, и панель, появляющаяся только после
 * перезагрузки, выглядит как поломка. Начальное значение берём сразу — иначе первый кадр рисует «узко»
 * на широком экране, и панель мигает.
 */
function useWideEnoughForScan(): boolean {
  const [wide, setWide] = useState(() => scanFitsBeside(window.innerWidth));

  useEffect(() => {
    const onResize = () => setWide(scanFitsBeside(window.innerWidth));
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  return wide;
}
