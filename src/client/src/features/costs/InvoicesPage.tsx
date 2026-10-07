import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { FileText, Plus, Tags } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import {
  useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
  type InvoiceListItem, type InvoiceQueue,
} from '@/shared/api/invoices';
import { useInvoiceQueueCounts, useLostReferences } from '@/shared/api/invoiceQueues';
import { ArticlesDialog } from './ArticlesDialog';
import { InvoiceForm } from './InvoiceForm';
import { InvoiceListRow } from './InvoiceListRow';
import { InvoiceQueueChips } from './InvoiceQueueChips';
import { emptyText, heldRow, lockedNote, queueRows, type HeldRow } from './invoiceQueues';
import { InvoiceScanPanel, ScanTooNarrow } from './InvoiceScanPanel';
import { K, scanFitsBeside } from './invoiceFields';

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
  // Числа чипов «наведите порядок» и, под отбором удалённых, — счета закрытого периода: в отбор они
  // не входят, и список обязан сказать о них сам.
  const counts = useInvoiceQueueCounts();
  const lostReferences = useLostReferences(queue === 'lost');
  const locked = lostReferences.data?.locked.invoices ?? null;
  const doubt = queue === 'lost' || queue === 'archived'
    ? counts.data?.find(s => s.code === queue)?.unchecked ?? null
    : null;
  const organizations = useCostsOrganizations('choice');
  const create = useCreateInvoice();
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
          {hasPermission(access, 'costs.invoice.edit') && (
            <Button variant="filled" icon={<Plus size={16} />} loading={create.isPending} onClick={addDraft}>
              Новый счёт
            </Button>
          )}
          {articlesOpen && <ArticlesDialog onClose={() => setArticlesOpen(false)} />}
        </div>
      }
      nav={
        <>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, поставщик, назначение…" />
          <InvoiceQueueChips queue={queue} onChange={setQueue} counts={counts.data} failed={counts.isError}
            onRetry={() => void counts.refetch()} locked={locked} />
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
                <p>{emptyText(queue, query, doubt, locked ?? 0)}</p>
                {queue && !query.trim() && (
                  <button type="button" className="underline hover:text-fg1" onClick={() => setQueue(null)}>
                    Показать все счета
                  </button>
                )}
              </div>
            )}
            {rows.map(({ item, left }) => (
              <InvoiceListRow key={item.id} item={item} active={item.id === selected} queue={queue} left={left}
                onClick={() => setSelected(item.id)} />
            ))}
            {/* Запертые — строкой, а не кнопкой: открыть их можно, исправить нельзя. Под пустым списком
                о них уже сказано словами пустого состояния. */}
            {queue === 'lost' && !!locked && rows.length > 0 && (
              <p className="px-3 py-2 text-xs text-fg3">{lockedNote(locked)}</p>
            )}
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
  return [item.number, item.supplierName, item.purpose]
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
