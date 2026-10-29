import { useEffect, useRef } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Alarm, Archive, Block, Check, Checklist, DocumentScanner, HistoryIcon, Schedule, StarShine, Warning } from '@/shared/ui/icons';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import type { InvoiceListItem, InvoiceQueue } from '@/shared/api/invoices';
import { formatDate, formatMoney } from '@/shared/format/format';
import { EXIT_WORD, lostTitle, placesCount, placesText, rowReadiness, type QueueExit } from './invoiceQueues';
import { REJECTED } from './invoiceFields';
import { LOST, MISSING } from './lostReferences';
import { rowScan } from './recognition';
import { rowDue } from './invoiceDue';
import { Tooltip } from '@/shared/ui/Tooltip';

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
  const rejected = item.state === REJECTED;
  // Третья строчка — что чинить: под отбором человек пришёл именно за этим, и открывать каждый счёт
  // ради ответа «а здесь что» незачем.
  const where = queue === 'lost' ? placesText(item.references?.lost)
    : queue === 'archived' ? placesText(item.references?.archived)
    : null;
  // Что со сканом — под любым отбором: основной носитель состояния распознавания — строка списка
  // (issue #1077). Человек завёл счёт из скана и ушёл; узнать об отказе он обязан, не открывая счёт.
  const scan = rowScan(item);
  // Чего не хватает до «Разобран» — под любым отбором (issue #1292): видно, за какой счёт браться.
  const readiness = rowReadiness(item);
  // Срок оплаты — под любым отбором (C4, issue #1080): просроченный счёт помечен без чьего-либо действия.
  const due = rowDue(item);

  return (
    <button ref={row} type="button" onClick={onClick} data-testid="invoice-row"
      className={`w-full text-left px-3 py-2 border-b border-outline-variant/60 ` +
        `state-layer ${active ? 'bg-primary-container' : ''}`}>
      <div className="flex items-center gap-2">
        {/* Номера нет, скан есть — имя файла: три черновика из скана подряд иначе неразличимы. */}
        {item.number || !item.scanFileName
          ? <span className="text-sm text-on-surface font-medium truncate">{item.number ?? 'без номера'}</span>
          : (
            <Tooltip content="Номера ещё нет — это имя файла счёта">
              <span className="inline-flex items-center gap-1 min-w-0 text-sm text-on-surface-variant">
                <Icon icon={DocumentScanner} size={18} className="shrink-0 text-on-surface-variant" aria-hidden />
                <span className="truncate">{item.scanFileName}</span>
              </span>
            </Tooltip>
          )}
        {item.issuedOn && <span className="text-xs text-on-surface-variant shrink-0">{formatDate(item.issuedOn)}</span>}
        <div className="flex-1" />
        {item.total != null && <span className="text-xs text-on-surface-variant shrink-0">{formatMoney(item.total)}</span>}
      </div>
      <div className="flex items-center gap-2 mt-0.5">
        <SupplierName item={item} />
        <div className="flex-1" />
        {/* Значки-счётчики. Нулей не рисуем: число «0» у каждой строки читается как шум. */}
        {lost > 0 && (
          <Tooltip content={lostTitle(item)}>
            <span className="inline-flex items-center gap-0.5 text-xs text-error shrink-0" aria-label={`Удалённых записей: ${lost}`}>
              <Icon icon={Warning} size={18} aria-hidden />{lost}
            </span>
          </Tooltip>
        )}
        {archived > 0 && (
          <Tooltip content={`Записи в архиве — ${placesText(item.references?.archived)}. Счёт верен: запись цела, её лишь убрали из выбора.`}>
            <span className="inline-flex items-center gap-0.5 text-xs text-on-surface-variant shrink-0"
              aria-label={`Записей в архиве: ${archived}`}>
              <Icon icon={Archive} size={18} aria-hidden />{archived}
            </span>
          </Tooltip>
        )}
        {item.unconfirmedCount > 0 && (
          <Tooltip content="Распознано, не подтверждено">
            <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0">
              <Icon icon={StarShine} size={18} />{item.unconfirmedCount}
            </span>
          </Tooltip>
        )}
        {due && (
          <Tooltip content={due.title}>
            <span className={`inline-flex items-center gap-0.5 text-xs shrink-0 ${DUE_TONE[due.tone]}`} data-testid="row-due">
              {due.tone === 'danger' ? <Icon icon={Alarm} size={18} aria-hidden /> : <Icon icon={Schedule} size={18} aria-hidden />}{due.text}
            </span>
          </Tooltip>
        )}
        {/* Отклонённый назван словом: в реестре он остаётся, и без пометки выглядел бы счётом в работе. */}
        {rejected && (
          <Tooltip content="По счёту не платим">
            <span className="inline-flex items-center gap-0.5 text-xs text-on-surface-variant shrink-0">
              <Icon icon={Block} size={18} aria-hidden />отклонён
            </span>
          </Tooltip>
        )}
        {/* Исправленный — тихим словом, как «отклонён» (разбор Дизайнера, п. 6): это не задача, а след. Без
            пометки счёт закрытого месяца выглядел бы таким, каким его закрыли. */}
        {item.corrected && (
          <Tooltip content="Менялся после закрытия периода — история в карточке счёта, секция «Исправления»">
            <span className="inline-flex items-center gap-0.5 text-xs text-on-surface-variant shrink-0">
              <Icon icon={HistoryIcon} size={18} aria-hidden />исправлен
            </span>
          </Tooltip>
        )}
        {/* Счётчик «ждут позиции» — затем, чтобы не открывать счёт ради ответа «а с этим что делать».
            У отклонённого его нет: разбирать такой счёт незачем. К очереди «Разобрать» счётчик не
            относится — она с issue #1292 отбирает по состоянию, а не по строкам без позиции. */}
        {!rejected && item.linesWithoutNomenclature > 0 && (
          <Tooltip content={`Строк ждёт позиции номенклатуры: ${item.linesWithoutNomenclature} из ${item.linesCount}`}>
            <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0">
              <Icon icon={Checklist} size={18} />{item.linesWithoutNomenclature}
            </span>
          </Tooltip>
        )}
      </div>
      {where && (
        <div className={`mt-0.5 text-xs truncate ${queue === 'lost' ? 'text-error' : 'text-on-surface-variant'}`}>{where}</div>
      )}
      {readiness && (
        <Tooltip content={readiness.title}>
          <div className={`mt-0.5 text-xs truncate ${readiness.ready ? 'text-success' : 'text-on-surface-variant'}`} data-testid="row-readiness">
            {readiness.text}
          </div>
        </Tooltip>
      )}
      {scan && (
        <div className={`mt-0.5 flex items-center gap-1 text-xs ${SCAN_TONE[scan.tone]}`}>
          {scan.running && <CircularProgress size={18} className="shrink-0" />}
          <span className="truncate">{scan.text}</span>
        </div>
      )}
    </button>
  );
}

const SCAN_TONE = { quiet: 'text-on-surface-variant', danger: 'text-error', warning: 'text-warning' } as const;
const DUE_TONE = SCAN_TONE;

/**
 * Строка открытого счёта, который под отбор больше не попадает: держится, пока с него не ушли, —
 * исчезнувший без действия человека счёт читался бы как сбой.
 *
 * Рисует только номер — и берёт его у ОТКРЫТОГО счёта, а не из списка: строка списка осталась
 * снимком до правки, и поставщик, сумма и пометки в ней устарели тем же сохранением. Почему счёт
 * ушёл из-под отбора, список не знает (исправили, отклонили, оплатили в закрытый период) — поэтому
 * слова нейтральные, без «исправлено» (ревью PR #1241).
 */
export function InvoiceLeftRow({ number, onClick, exit, active = true }: {
  number: string | null;
  onClick: () => void;
  /** След счёта, выведенного из очереди отсюда (issue #1293): причина ухода известна, и она названа. */
  exit?: QueueExit;
  /** След держится и после ухода со счёта — тогда строка не выделена. */
  active?: boolean;
}) {
  return (
    <Tooltip content={exit
        ? `Счёт ${EXIT_WORD[exit]} и из очереди ушёл. Строка остаётся, чтобы к нему можно было вернуться`
        : 'Счёт под этот отбор больше не попадает. Строка остаётся, пока он открыт'}>
      <button type="button" onClick={onClick} data-testid={exit ? 'row-exit' : undefined}
        className={`state-layer w-full text-left px-3 py-2 border-b border-outline-variant/60 ${active ? 'bg-primary-container' : ''}`}>
        <div className="text-sm text-on-surface font-medium truncate">{number || 'без номера'}</div>
        <div className="mt-0.5 inline-flex items-center gap-1 text-xs text-on-surface-variant">
          <Icon icon={Check} size={18} aria-hidden /> {exit ? EXIT_WORD[exit] : 'больше не в отборе'}
        </div>
      </button>
    </Tooltip>
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
      <span className="inline-flex items-center gap-1 min-w-0 text-xs text-on-surface-variant">
        <span className="truncate">{item.supplierName}</span>
        {item.supplierArchived && <ArchivedMark words={false} />}
      </span>
    );
  }
  if (!item.supplierId) return <span className="text-xs text-on-surface-variant truncate">поставщик не выбран</span>;

  const lost = item.references?.supplierLost === true;
  return (
    <Tooltip content={lost
        ? 'Запись справочника удалена. Откройте счёт и выберите другую организацию'
        : 'Ссылка на организацию есть, а в справочнике организаций её нет: запись удалили либо перевели в другой вид. Что именно — скажет открытый счёт'}>
      <span className="inline-flex items-center gap-1 text-xs text-error truncate">
        <Icon icon={Warning} size={18} /> {lost ? LOST.organization : MISSING.organization}
      </span>
    </Tooltip>
  );
}
