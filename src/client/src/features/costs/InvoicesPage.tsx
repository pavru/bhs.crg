import { useEffect, useState } from 'react';
import { FileText, ListChecks, Plus, Sparkles, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import {
  useCostsOrganizations, useCreateInvoice, useInvoice, useInvoices,
  type InvoiceListItem,
} from '@/shared/api/invoices';
import { InvoiceForm } from './InvoiceForm';
import { InvoiceScanPanel, ScanTooNarrow } from './InvoiceScanPanel';
import { K, formatDate, formatMoney, scanFitsBeside } from './invoiceFields';

/**
 * Счета на оплату: реестр слева, форма ввода справа, скан рядом с формой (задача C1, issue #1076).
 *
 * <p>⚠️ Это НЕ реестр из ТЗ. Полноценный реестр со своей сеткой, отбором, сохранёнными
 * представлениями и постраничностью приезжает задачей G4 — здесь список ровно затем, чтобы дойти до
 * формы и увидеть, что черновик в него попал. Назвать его реестром значило бы объявить сделанным то,
 * что не начато.</p>
 */
export function InvoicesPage() {
  const [selected, setSelected] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [needsParsing, setNeedsParsing] = useState(false);
  const wide = useWideEnoughForScan();

  const invoices = useInvoices(needsParsing);
  const organizations = useCostsOrganizations();
  const create = useCreateInvoice();
  const toast = useToast();

  const view = useInvoice(selected ?? undefined);
  const items = (invoices.data ?? []).filter(i => matches(i, query));

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
        <Button variant="filled" icon={<Plus size={16} />} loading={create.isPending} onClick={addDraft}>
          Новый счёт
        </Button>
      }
      nav={
        <>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, поставщик, назначение…" />
          {/* Отбор «Разобрать» (ТЗ COST-6.2) — рабочая очередь снабженца: счета, у которых строки ждут
              позиции номенклатуры. Отбирает СЕРВЕР: считать «ждут позиции» по загруженному списку
              можно, а вот утверждать по нему, что других таких счетов нет, — нельзя. */}
          <label className="flex items-center gap-2 px-3 py-1.5 text-xs text-fg3 cursor-pointer">
            <input type="checkbox" checked={needsParsing}
              onChange={e => setNeedsParsing(e.target.checked)} />
            <ListChecks size={13} />
            Только «Разобрать»
          </label>
          <div className="flex-1 overflow-y-auto">
            {invoices.isPending && <p className="px-3 py-2 text-xs text-fg3">Загрузка…</p>}
            {invoices.isError && (
              <p className="px-3 py-2 text-xs text-danger">
                Список не пришёл. Это отказ чтения, а не пустой список: счета могут быть.
              </p>
            )}
            {!invoices.isPending && !invoices.isError && items.length === 0 && (
              <p className="px-3 py-2 text-xs text-fg3">
                {query ? 'Ничего не найдено.'
                  : needsParsing
                    ? 'Разбирать нечего: строк, ждущих позиции номенклатуры, нет ни у одного счёта. '
                      + 'Счета без строк вовсе в этот отбор не входят — это другая работа.'
                    : 'Счетов пока нет.'}
              </p>
            )}
            {items.map(item => (
              <ListRow key={item.id} item={item} active={item.id === selected}
                onClick={() => setSelected(item.id)} />
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

function ListRow({ item, active, onClick }: {
  item: InvoiceListItem; active: boolean; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick}
      className={`w-full text-left px-3 py-2 border-b border-stroke/60 transition-colors ` +
        `${active ? 'bg-brand-subtle' : 'hover:bg-surface2'}`}>
      <div className="flex items-center gap-2">
        <span className="text-sm text-fg1 font-medium truncate">{item.number ?? 'без номера'}</span>
        {item.issuedOn && <span className="text-xs text-fg3 shrink-0">{formatDate(item.issuedOn)}</span>}
        <div className="flex-1" />
        {item.total != null && <span className="text-xs text-fg2 shrink-0">{formatMoney(item.total)}</span>}
      </div>
      <div className="flex items-center gap-2 mt-0.5">
        <SupplierName item={item} />
        {item.unconfirmedCount > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0"
            title="Распознано, не подтверждено">
            <Sparkles size={11} />{item.unconfirmedCount}
          </span>
        )}
        {/* Счётчик «ждут позиции» — затем, чтобы не открывать счёт ради ответа «а с этим что делать».
            Строки БЕЗ ожидающих не показываем вовсе: число «0» у каждой строки читается как шум. */}
        {item.linesWithoutNomenclature > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0"
            title={`Строк ждёт позиции номенклатуры: ${item.linesWithoutNomenclature} из ${item.linesCount}`}>
            <ListChecks size={11} />{item.linesWithoutNomenclature}
          </span>
        )}
      </div>
    </button>
  );
}

/**
 * Название поставщика — или прямое указание на потерю.
 *
 * ⚠️ Ссылка без названия и «поставщик не выбран» — РАЗНЫЕ вещи, и одним прочерком их путать нельзя:
 * первое означает, что запись справочника удалили, и счёт остался со ссылкой в пустоту.
 */
function SupplierName({ item }: { item: InvoiceListItem }) {
  if (item.supplierName) {
    return <span className="text-xs text-fg3 truncate">{item.supplierName}</span>;
  }
  if (item.supplierId) {
    return (
      <span className="inline-flex items-center gap-1 text-xs text-danger truncate"
        title="Ссылка на организацию есть, а записи нет — её удалили">
        <TriangleAlert size={11} /> организация не найдена
      </span>
    );
  }
  return <span className="text-xs text-fg4 truncate">поставщик не выбран</span>;
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
