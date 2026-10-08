import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { FileText, Plus, ScanLine, Tags } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import {
  scansRunning, useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
  type InvoiceListItem, type InvoiceQueue,
} from '@/shared/api/invoices';
import { useInvoiceQueues } from '@/shared/api/invoiceQueues';
import { useInvoiceFromScan } from '@/shared/api/invoiceRecognition';
import { ArticlesDialog } from './ArticlesDialog';
import { InvoiceForm } from './InvoiceForm';
import { InvoiceLeftRow, InvoiceListRow } from './InvoiceListRow';
import { InvoiceQueueChips } from './InvoiceQueueChips';
import { emptyText, heldRow, lockedNote, queueRows, type HeldRow } from './invoiceQueues';
import { InvoiceScanPanel, ScanTooNarrow } from './InvoiceScanPanel';
import { K, asInput, scanFitsBeside } from './invoiceFields';
import { useQueuesFollowScans } from './recognitionWatch';

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

  const invoices = useInvoices(queue);
  // Числа чипов «наведите порядок» — тому, кто счёт может исправить: остальным число было бы упрёком
  // без выхода. Вместе с ними приходят счета закрытого периода (в отбор не входят, и список обязан
  // сказать о них сам) и оговорка «проверено не всё».
  const counts = useInvoiceQueues(hasPermission(access, 'costs.invoice.edit'));
  const queues = counts.data;
  useQueuesFollowScans(scansRunning(invoices.data), () => { if (counts.isEnabled) void counts.refetch(); });
  const fixing = queue === 'lost' || queue === 'archived';
  const doubt = fixing ? queues?.doubt ?? null : null;
  const organizations = useCostsOrganizations('choice');
  const create = useCreateInvoice();
  const fromScan = useInvoiceFromScan();
  const scanInput = useRef<HTMLInputElement>(null);
  const toast = useToast();

  const view = useInvoice(selected ?? undefined);

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
      // Отбор и поиск снимаются: новый черновик под «Разобрать» не стоит, а у счёта без номера и
      // поставщика поиску не за что зацепиться — открылся бы счёт, которого нет в списке.
      setQueue(null);
      setQuery('');
      setSelected(created.invoice.id);
    } catch (e) { toast.apiError(e, 'Счёт из скана не заведён'); }
  }

  const scan = view.data ? view.data.requisites[K.scan] : null;
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
              <Button variant="outlined" icon={<Plus size={16} />} loading={create.isPending} onClick={addDraft}>
                Новый счёт
              </Button>
              <Button variant="filled" icon={<ScanLine size={16} />} loading={fromScan.isPending}
                onClick={() => scanInput.current?.click()}>
                Счёт из скана
              </Button>
              {/* Только то, что распознаётся: файл другого вида сервер отверг бы, и выбор его здесь
                  был бы дверью в отказ. */}
              <input ref={scanInput} type="file" className="hidden" accept="application/pdf,image/png,image/jpeg"
                onChange={e => {
                  const file = e.target.files?.[0];
                  e.target.value = '';
                  if (file) void addFromScan(file);
                }} />
            </>
          )}
          {articlesOpen && <ArticlesDialog onClose={() => setArticlesOpen(false)} />}
        </div>
      }
      nav={
        <>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, поставщик, назначение…" />
          <InvoiceQueueChips queue={queue} onChange={setQueue} queues={queues} failed={counts.isError}
            onRetry={() => void counts.refetch()} />
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
                number={view.data?.id === item.id ? asInput(view.data.requisites[K.number]) : item.number} />
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
        </>
      }
      detail={
        // ⚠️ Три состояния, а не одно. «Ничего не выбрано», «счёт грузится» и «счёт не пришёл» —
        // разные вещи: отказ чтения, показанный приглашением выбрать счёт, выглядит как будто человек
        // никуда не нажимал, и повторное нажатие по той же строке ничего не меняет.
        !selected ? (
          <div className="flex-1 grid place-items-center">
            <EmptyState icon={<FileText size={28} />} title="Выберите счёт"
              description="Или заведите новый — черновик сохранится пустым." />
          </div>
        ) : view.isPending ? (
          <div className="flex-1 grid place-items-center text-xs text-fg3">Счёт загружается…</div>
        ) : view.isError || !view.data ? (
          <div className="flex-1 grid place-items-center p-6">
            <div className="max-w-md text-center space-y-2">
              <p className="text-sm text-danger">Счёт не открылся: {apiError(view.error, 'сервер отказал')}</p>
              <p className="text-xs text-fg3">
                Это отказ чтения, а не пустой счёт — сам счёт в списке остался.
              </p>
              <Button size="sm" variant="outlined" onClick={() => void view.refetch()}>Повторить</Button>
            </div>
          </div>
        ) : (
          <div className="flex-1 min-h-0 flex">
            <InvoiceForm
              key={view.data.id}
              view={view.data}
              organizations={organizations.data ?? []}
              organizationsError={organizations.isError ? organizations.error : undefined}
              onOpenInvoice={setSelected}
              scanSlot={hasScan && !wide
                ? <ScanTooNarrow invoiceId={view.data.id} width={window.innerWidth} />
                : undefined}
            />
            {/* Ширина панели: форма — главное, скан — опора. Отсюда 40 % и потолок: на широком
                экране бумага не должна отъедать место у полей, которые правят. */}
            {hasScan && wide && (
              <aside className="w-[40%] max-w-[620px] min-w-[340px] shrink-0 border-l border-stroke flex flex-col min-h-0">
                <InvoiceScanPanel
                  invoiceId={view.data.id}
                  blobPath={fileProp(scan, 'blobPath')}
                  fileName={fileProp(scan, 'fileName')}
                  mimeType={fileProp(scan, 'mimeType')}
                />
              </aside>
            )}
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
