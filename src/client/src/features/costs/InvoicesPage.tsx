import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { FileText, Link2, Plus, ScanLine, Tags } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { useToast } from '@/shared/ui/Toast';
import { useCloseGone } from '@/shared/hooks/useCloseGone';
import { useOpenRecord } from './openRecord';
import { RereadFailedNotice } from './RereadFailedNotice';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import {
  forgetInvoice, scansRunning, useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
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
import { emptyText, heldRow, lockedNote, queueRows, type HeldRow } from './invoiceQueues';
import { InvoiceScanBatchBar, InvoiceScanDrop } from './InvoiceScanDrop';
import { InvoiceScanPanel, ScanTooNarrow } from './InvoiceScanPanel';
import { K, asInput, scanFitsBeside } from './invoiceFields';
import { useQueuesFollowScans } from './recognitionWatch';
import { retryRejected, startBatch, useScanBatch, type BatchPort } from './scanBatch';

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
  const setSelected = (id: string) => setParams(prev => {
    const next = new URLSearchParams(prev);
    next.set(INVOICE_RECORD.param, id);
    return next;
  }, { replace: true });
  const [query, setQuery] = useState('');
  const [queue, setQueue] = useState<InvoiceQueue | null>(null);
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
  const rows = queueRows(invoices.data ?? [], nextHeld).filter(r => matches(r.item, query));

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
  }, { replace: true }), id => forgetInvoice(qc, id));
  const me = useAuth().user?.sub;
  const batch = useScanBatch(me);
  const loading = fromScan.isPending || (batch !== null && batch.phase !== 'done');
  // Что распознаётся, говорит реестр видов: из него и выбор файла, и подсказка, и отсев до отправки.
  const kinds = useFileKinds();
  const readable = recognizedKinds(kinds.data);
  const port: BatchPort = {
    owner: me ?? '',
    kinds: kinds.data,
    send: file => sendScan(qc, file).then(created => created.invoice.id),
    refresh: () => refreshInvoiceLists(qc),
  };
  function addScans(files: File[], folders: string[] = []) {
    if (files.length === 1 && folders.length === 0) void addFromScan(files[0]);
    else startBatch(files, folders, port);
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
          <InvoiceScanBatchBar batch={batch} narrowed={queue !== null || query.trim() !== ''}
            onShowAll={() => { setQueue(null); setQuery(''); }} onRetry={() => retryRejected(port)} />
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
            {!invoices.isPending && !invoices.isError && rows.length === 0 && (
              <div className="px-3 py-2 text-xs text-fg3 space-y-1">
                <p>{emptyText(queue, query, queues)}</p>
                {queue && !query.trim() && (
                  <button type="button" className="underline hover:text-fg1" onClick={() => setQueue(null)}>
                    Показать все счета
                  </button>
                )}
              </div>
            )}
            {rows.map(({ item, left }) => left ? (
              // Номер — у открытого счёта: строка списка осталась снимком до правки.
              <InvoiceLeftRow key={item.id} onClick={() => setSelected(item.id)}
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
          <div className="flex-1 min-w-0 min-h-0 flex flex-col">
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
