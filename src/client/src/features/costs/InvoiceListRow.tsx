import { useEffect, useRef } from 'react';
import { Archive, Check, ListChecks, Sparkles, TriangleAlert } from 'lucide-react';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import type { InvoiceListItem, InvoiceQueue } from '@/shared/api/invoices';
import { formatDate, formatMoney } from '@/shared/format/format';
import { lostTitle, placesCount, placesText } from './invoiceQueues';
import { LOST, MISSING } from './lostReferences';

/**
 * Строка списка счетов.
 *
 * @param queue отбор, под которым стоит список: под «удалёнными» и «архивом» строка называет места.
 */
export function InvoiceListRow({ item, active, queue, onClick }: {
  item: InvoiceListItem; active: boolean; queue: InvoiceQueue | null; onClick: () => void;
}) {
  // Открытый счёт — на виду: сюда приходят и по ссылке из реестра, а там счёт мог стоять сотым.
  // `nearest` — строка, которая и так видна, с места не сдвигается.
  const row = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (active) row.current?.scrollIntoView({ block: 'nearest' }); }, [active]);

  const lost = placesCount(item.references?.lost);
  const archived = placesCount(item.references?.archived);
  // Третья строчка — что чинить: под отбором человек пришёл именно за этим, и открывать каждый счёт
  // ради ответа «а здесь что» незачем.
  const where = queue === 'lost' ? placesText(item.references?.lost)
    : queue === 'archived' ? placesText(item.references?.archived)
    : null;

  return (
    <button ref={row} type="button" onClick={onClick}
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
        <div className="flex-1" />
        {/* Значки-счётчики. Нулей не рисуем: число «0» у каждой строки читается как шум. */}
        {lost > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-danger shrink-0"
            title={lostTitle(item)} aria-label={`Удалённых записей: ${lost}`}>
            <TriangleAlert size={11} aria-hidden />{lost}
          </span>
        )}
        {archived > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-fg3 shrink-0"
            title={`Записи в архиве — ${placesText(item.references?.archived)}. Счёт верен: запись цела, её лишь убрали из выбора.`}
            aria-label={`Записей в архиве: ${archived}`}>
            <Archive size={11} aria-hidden />{archived}
          </span>
        )}
        {item.unconfirmedCount > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0"
            title="Распознано, не подтверждено">
            <Sparkles size={11} />{item.unconfirmedCount}
          </span>
        )}
        {/* Счётчик «ждут позиции» — затем, чтобы не открывать счёт ради ответа «а с этим что делать». */}
        {item.linesWithoutNomenclature > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0"
            title={`Строк ждёт позиции номенклатуры: ${item.linesWithoutNomenclature} из ${item.linesCount}`}>
            <ListChecks size={11} />{item.linesWithoutNomenclature}
          </span>
        )}
      </div>
      {where && (
        <div className={`mt-0.5 text-xs truncate ${queue === 'lost' ? 'text-danger' : 'text-fg3'}`}>{where}</div>
      )}
    </button>
  );
}

/**
 * Строка открытого счёта, который под отбор больше не попадает: держится, пока с него не ушли, —
 * исчезнувший без действия человека счёт читался бы как сбой.
 *
 * Рисует только номер — и берёт его у ОТКРЫТОГО счёта, а не из списка: строка списка осталась
 * снимком до правки, и поставщик, сумма и пометки в ней устарели тем же сохранением. Почему счёт
 * ушёл из-под отбора, список не знает (исправили, отклонили, оплатили в закрытый период) — поэтому
 * слова нейтральные, без «исправлено» (ревью PR #1241).
 */
export function InvoiceLeftRow({ number, onClick }: { number: string | null; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      className="w-full text-left px-3 py-2 border-b border-stroke/60 bg-brand-subtle"
      title="Счёт под этот отбор больше не попадает. Строка остаётся, пока он открыт">
      <div className="text-sm text-fg1 font-medium truncate">{number || 'без номера'}</div>
      <div className="mt-0.5 inline-flex items-center gap-1 text-xs text-fg3">
        <Check size={11} aria-hidden /> больше не в отборе
      </div>
    </button>
  );
}

/**
 * Название поставщика — или прямое указание на потерю.
 *
 * ⚠️ Ссылка без названия и «поставщик не выбран» — РАЗНЫЕ вещи, и одним прочерком их путать нельзя:
 * первое означает, что запись справочника удалили, и счёт остался со ссылкой в пустоту.
 *
 * «Удалена» говорит СЕРВЕР (`references.supplierLost`), а не сравнение со списком: пока тот
 * грузится, «нет в списке» выглядело бы потерей. Сервер потери не подтвердил — остаются осторожные
 * слова: названия может не быть и по другой причине (запись перевели в другой вид, вида «Организация»
 * нет, запись удалили между опросом и чтением), и утверждать любую из них список не вправе.
 */
function SupplierName({ item }: { item: InvoiceListItem }) {
  if (item.supplierName) {
    return (
      <span className="inline-flex items-center gap-1 min-w-0 text-xs text-fg3">
        <span className="truncate">{item.supplierName}</span>
        {item.supplierArchived && <ArchivedMark words={false} />}
      </span>
    );
  }
  if (!item.supplierId) return <span className="text-xs text-fg4 truncate">поставщик не выбран</span>;

  const lost = item.references?.supplierLost === true;
  return (
    <span className="inline-flex items-center gap-1 text-xs text-danger truncate"
      title={lost
        ? 'Запись справочника удалена. Откройте счёт и выберите другую организацию'
        : 'Ссылка на организацию есть, а в справочнике организаций её нет: запись удалили либо перевели в другой вид. Что именно — скажет открытый счёт'}>
      <TriangleAlert size={11} /> {lost ? LOST.organization : MISSING.organization}
    </span>
  );
}
