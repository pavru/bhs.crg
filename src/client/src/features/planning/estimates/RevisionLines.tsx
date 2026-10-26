import { useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, Plus } from 'lucide-react';
import {
  costHidden, useRevisionRows, useRevisionSections, useUnitNames, useWorkTypeNames,
  type EstimateRevision, type EstimateRow, type HiddenPart, type RevisionSection,
} from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { SearchInput } from '@/shared/ui/SearchInput';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Tooltip } from '@/shared/ui/Tooltip';
import { apiError } from '@/shared/utils/apiError';
import { formatAmount, formatCount, formatQuantity } from '@/shared/format/format';
import { gridLines, queueCount, rowNumber, sectionKey, type GridLine, type RowQueue } from './estimateRows';
import { MatchDialog, type MatchTarget } from './MatchDialog';
import { ResourceMatchDialog } from './ResourceMatchDialog';
import { useEstimateEdit } from './useEstimateEdit';

/**
 * Строки редакции сметы (change `plan-09-estimate-ui`, макет П1: основа — вариант 1, «что ждёт» — из 3).
 *
 * - Разделы ЛСР — заголовками групп, по умолчанию свёрнуты: при 300 строках таблица иначе на пятнадцать экранов.
 * - Над таблицей — чипы «что ждёт» со счётчиками сервера; чип ставит отбор и раскрывает разделы, где ждут.
 * - Колонки «Пометки» нет: пометка — в клетке вида работы, ширина нужна наименованию (824 px карточки).
 * - Без `plan.estimate.cost` колонка стоимости стоит в состоянии «нет права» сетки (CORE-33) с именем права
 *   из каталога — ни нуля, ни пустой суммы (D4 зонтика).
 * - Сопоставление (PR 3) — окнами: «Сопоставить раздел» в заголовке раздела, вид работы строки — из её клетки,
 *   материалы — очередью. Только с `plan.estimate.edit` на широком экране и не у зафиксированной редакции:
 *   строки той не правятся (ТЗ PLAN-5).
 */
export function RevisionLines({ revision, initialQueue = 'all' }: {
  revision: EstimateRevision;
  /** Отбор при открытии: редакция, созданная с несопоставленным, открывается на нём (ответ П1). */
  initialQueue?: RowQueue;
}) {
  const rows = useRevisionRows(revision.id);
  const sections = useRevisionSections(revision.id);
  const workTypes = useWorkTypeNames();
  const [queue, setQueue] = useState<RowQueue>(initialQueue);
  const [query, setQuery] = useState('');
  const [opened, setOpened] = useState<ReadonlySet<string>>(new Set());
  const [matching, setMatching] = useState<MatchTarget | null>(null);
  const [materials, setMaterials] = useState(false);
  const editable = useEstimateEdit() && revision.frozen === null;

  const items = useMemo(() => rows.data?.items ?? [], [rows.data]);
  const lines = useMemo(() => gridLines(sections.data ?? [], items, queue, query, opened),
    [sections.data, items, queue, query, opened]);
  // Чипы по материалам — числом СТРОК, как и остальные: счётчик сервера `materialsUnmatched` считает материалы,
  // а отбор показывает строки, и «3» на чипе при одной строке под ним искали бы глазами.
  const remembered = useMemo(() => queueCount(items, 'remembered'), [items]);
  const noNomenclature = useMemo(() => queueCount(items, 'no-nomenclature'), [items]);
  const units = useUnitNames(useMemo(() => items.flatMap(r => (r.unitId ? [r.unitId] : [])), [items]));

  if (rows.isError || sections.isError)
    return <p role="alert" className="text-sm text-danger">{apiError(rows.error ?? sections.error, 'Строки редакции не прочитались')}</p>;
  if (!rows.data || !sections.data) return <p className="text-sm text-fg4">Читаем строки редакции…</p>;

  const toggle = (key: string) => setOpened(prev => {
    const next = new Set(prev);
    if (next.has(key)) next.delete(key); else next.add(key);
    return next;
  });
  const pick = (next: RowQueue) => setQueue(prev => (prev === next ? 'all' : next));
  const filtering = queue !== 'all' || query.trim() !== '';

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <SearchInput value={query} onChange={setQuery} placeholder="Шифр или наименование" ariaLabel="Поиск строки по шифру или наименованию"
          className="w-64 max-w-full" />
        <Chip selected={queue === 'all'} count={formatCount(revision.rows)} onClick={() => setQueue('all')}>Все</Chip>
        {/* Чипы «что ждёт» — по счётчикам сервера; пустой чип не показывается: ждать нечего. */}
        {revision.unmatched > 0 && (
          <Chip tone="warning" selected={queue === 'unmatched'} count={formatCount(revision.unmatched)}
            onClick={() => pick('unmatched')}>Не сопоставлены</Chip>
        )}
        {noNomenclature > 0 && (
          <Chip tone="warning" selected={queue === 'no-nomenclature'} count={formatCount(noNomenclature)}
            onClick={() => pick('no-nomenclature')}>Без номенклатуры</Chip>
        )}
        {revision.unitChanged > 0 && (
          <Chip tone="warning" selected={queue === 'unit-changed'} count={formatCount(revision.unitChanged)}
            onClick={() => pick('unit-changed')}>Ед. изменилась</Chip>
        )}
        {/* «Запомнено» — чтобы найти неверно запомненное: очередь «что ждёт» его прячет (П1, вариант 3). */}
        {remembered > 0 && (
          <Chip selected={queue === 'remembered'} count={formatCount(remembered)} onClick={() => pick('remembered')}>Запомнено</Chip>
        )}
        {editable && noNomenclature > 0 && (
          <Button size="sm" variant="text" className="ml-auto" onClick={() => setMaterials(true)}>Сопоставить материалы…</Button>
        )}
      </div>

      <LinesGrid lines={lines} hidden={rows.data.hidden} names={{ workTypes: workTypes.data, units }} filtering={filtering}
        onToggle={toggle} onMatch={editable ? setMatching : null} />

      {matching && (
        <MatchDialog revision={revision} rows={items} sections={sections.data} target={matching} onClose={() => setMatching(null)} />
      )}
      {materials && <ResourceMatchDialog revision={revision} rows={items} onClose={() => setMaterials(false)} />}
    </div>
  );
}

/** Колонки строк редакции. Стоимость — всегда на месте: без права она называет причину, а не исчезает. */
function lineColumns(hidden: HiddenPart[] | undefined, names: Names, onMatch: OnMatch): DataGridColumn<GridLine>[] {
  const closed = costHidden(hidden);
  const row = (line: GridLine) => (line.kind === 'row' ? line.row : null);
  return [
    { key: 'number', label: '№', width: 'w-12', cell: l => <span className="tabular-nums text-fg3">{rowNumber(row(l)!)}</span> },
    {
      key: 'rateCode', label: 'Шифр', width: 'w-36',
      // Шифр — в одну строку с обрезкой (макет П1): ширина нужна наименованию.
      cell: l => <Tooltip content={row(l)!.rateCode ?? undefined}><span className="block truncate font-mono text-xs">{row(l)!.rateCode}</span></Tooltip>,
    },
    { key: 'name', label: 'Наименование', wrap: true, rowHeader: true, cell: l => <span className="text-sm">{row(l)!.name}</span> },
    {
      key: 'sourceVolume', label: 'Объём', align: 'right', width: 'w-20',
      cell: l => { const v = row(l)!.sourceVolume; return v === null ? null : <span className="tabular-nums">{formatQuantity(v)}</span>; },
    },
    { key: 'sourceUnit', label: 'Ед.', width: 'w-16', cell: l => row(l)!.sourceUnit },
    { key: 'workType', label: 'Вид работы в плане', width: 'w-40', wrap: true, cell: l => <WorkTypeCell row={row(l)!} names={names} onMatch={onMatch} /> },
    {
      key: 'cost', label: 'Стоимость', align: 'right', width: 'w-32',
      unavailable: closed ? 'no-right' : null,
      unavailableHint: closed ? `Стоимость видна с правом «${closed.name}» — его выдаёт администратор.` : undefined,
      cell: l => { const t = row(l)!.cost?.total; return t == null ? null : <span className="tabular-nums">{formatAmount(t)}</span>; },
    },
  ];
}

/** Открыть окно сопоставления; `null` — сопоставлять здесь нельзя (нет права, телефон, редакция зафиксирована). */
type OnMatch = ((target: MatchTarget) => void) | null;

/** Названия справочников ядра для показа: видов работ и единиц плана. */
interface Names {
  workTypes: ReadonlyMap<string, string> | undefined;
  units: ReadonlyMap<string, string>;
}

/**
 * Клетка вида работы — и место пометок строки (макет П1): «не сопоставлена», «ед. изменилась», «запомнено»,
 * у позиции-материала — «материал» (вида работы не требует, решение (14) П1).
 */
function WorkTypeCell({ row, names, onMatch }: { row: EstimateRow; names: Names; onMatch: OnMatch }) {
  const marks = row.marks.filter(m => m.code !== 'remembered');
  // «Запомнено» — и у строки, и у её материала: иначе строка из очереди «Запомнено» стояла бы без пометки.
  const remembered = row.marks.find(m => m.code === 'remembered')
    ?? row.materials.flatMap(m => m.marks).find(m => m.code === 'remembered');
  const rememberedBadge = remembered && <StatusBadge tone="quiet" title={remembered.text}>запомнено</StatusBadge>;
  if (row.kind === 'material') return (
    <div className="flex flex-col items-start gap-0.5">
      <StatusBadge tone="quiet" title="Позиция-материал ЛСР: вида работы не требует, в план не входит">материал</StatusBadge>
      {rememberedBadge}
    </div>
  );
  return (
    <div className="flex flex-col items-start gap-0.5">
      {row.workTypeId && (onMatch ? (
        <button type="button" onClick={() => onMatch({ kind: 'row', rowId: row.id })}
          className="rounded-sm text-left text-sm text-fg1 hover:text-brand hover:underline focus-visible:outline-2">
          {names.workTypes?.get(row.workTypeId) ?? '…'}
        </button>
      ) : (
        <span className="text-sm text-fg1">{names.workTypes?.get(row.workTypeId) ?? '…'}</span>
      ))}
      {!row.workTypeId && onMatch && (
        <button type="button" onClick={() => onMatch({ kind: 'row', rowId: row.id })}
          className="inline-flex items-center gap-1 rounded-sm text-sm text-brand hover:underline focus-visible:outline-2">
          <Plus size={14} aria-hidden />выбрать вид работы
        </button>
      )}
      {row.workTypeId && row.volume !== null && (
        <span className="text-xs text-fg3 tabular-nums">
          {formatQuantity(row.volume)}{row.unitId && names.units.get(row.unitId) ? ` ${names.units.get(row.unitId)}` : ''}
          {row.factor !== null && row.factor !== 1 ? ` · × ${formatQuantity(row.factor)}` : ''}
        </span>
      )}
      {marks.map(m => (
        <StatusBadge key={m.code} tone={m.code === 'unmatched' || m.code === 'unit-changed' ? 'warning' : 'neutral'} title={m.text}>
          {MARK_LABEL[m.code] ?? m.text}
        </StatusBadge>
      ))}
      {rememberedBadge}
    </div>
  );
}

/** Короткие подписи пометок в клетке; полный текст сервера — в подсказке. Неизвестный код — текстом сервера. */
const MARK_LABEL: Record<string, string> = {
  unmatched: 'не сопоставлена',
  'unit-changed': 'ед. изменилась',
};

function LinesGrid({ lines, hidden, names, filtering, onToggle, onMatch }: {
  lines: GridLine[];
  hidden: HiddenPart[] | undefined;
  names: Names;
  filtering: boolean;
  onToggle: (key: string) => void;
  onMatch: OnMatch;
}) {
  const columns = lineColumns(hidden, names, onMatch);
  return (
    <DataGrid columns={columns} rows={lines} fixed rowKey={l => l.key}
      state={filtering ? 'filtered-out' : undefined}
      tone={l => (l.kind === 'row' && l.row.marks.some(m => m.code === 'unmatched' || m.code === 'unit-changed') ? 'warning' : null)}
      section={l => (l.kind === 'section'
        ? <SectionHeading section={l.section} open={l.open} shown={l.shown} filtering={filtering} onToggle={() => onToggle(sectionKey(l.section.code))}
            onMatch={onMatch && l.section.unmatched > 0 ? () => onMatch({ kind: 'section', code: l.section.code }) : null} />
        : null)} />
  );
}

function SectionHeading({ section, open, shown, filtering, onToggle, onMatch }: {
  section: RevisionSection; open: boolean; shown: number; filtering: boolean; onToggle: () => void;
  /** «Сопоставить раздел» — когда в нём ждут и сопоставлять здесь можно. */
  onMatch: (() => void) | null;
}) {
  const title = section.code === null
    ? 'Вне разделов'
    : `Раздел ${section.code}${section.name ? `. ${section.name}` : ''}`;
  const Icon = open ? ChevronDown : ChevronRight;
  return (
    <div className="flex flex-wrap items-center gap-2">
      {/* С отбором разделы раскрыты сами — сворачивать нечего: под отбором видны только подходящие строки. */}
      {filtering ? (
        <span className="text-sm font-medium text-fg1">{title}</span>
      ) : (
        <button type="button" aria-expanded={open} onClick={onToggle}
          className="inline-flex items-center gap-1 rounded-sm text-left text-sm font-medium text-fg1 hover:text-brand focus-visible:outline-2">
          <Icon size={14} aria-hidden className="shrink-0" />
          {title}
        </button>
      )}
      <span className="text-xs text-fg3">
        {filtering ? `${formatCount(shown)} из ${formatCount(section.rows)} строк` : `${formatCount(section.rows)} строк`}
      </span>
      {section.unmatched > 0
        ? <StatusBadge tone="warning">не сопоставлено: {formatCount(section.unmatched)}</StatusBadge>
        : <StatusBadge tone="success">всё сопоставлено</StatusBadge>}
      {section.materialsUnmatched > 0 && (
        <StatusBadge tone="neutral">без номенклатуры: {formatCount(section.materialsUnmatched)}</StatusBadge>
      )}
      {onMatch && (
        <Button size="sm" variant="text" className="ml-auto" onClick={onMatch}>
          {section.code === null ? 'Сопоставить строки' : 'Сопоставить раздел'}
        </Button>
      )}
    </div>
  );
}
