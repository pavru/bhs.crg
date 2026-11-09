import type { Distribution, DistributionLine, DistributionRule, DistributionTarget } from '@/shared/api/planningDistribution';
import { costHidden, type EstimateRow, type HiddenPart } from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { ChevronRight, KeyboardArrowDown } from '@/shared/ui/icons';
import { Select, SelectGroup, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Tooltip } from '@/shared/ui/Tooltip';
import { formatAmount, formatCount, formatQuantity } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { lineSummary, lsrKey, targetText, type DistGridLine, type SectionName } from './distributionView';

/** Что сетке можно делать; `null` у действия — здесь нельзя (нет права, телефон). */
export interface GridActions {
  onRule: ((rule: DistributionRule, target: DistributionTarget | null) => void) | null;
  onReset: ((rule: DistributionRule) => void) | null;
  onSplit: ((line: DistributionLine) => void) | null;
  selected: ReadonlySet<string>;
  onSelect: ((rowIds: string[], on: boolean) => void) | null;
  busy: boolean;
}

/** Значение пункта «Стройка в целом» — непустое: Radix пустую строку пунктом не принимает. */
const WHOLE = 'whole';

/**
 * Строки редакции в режиме распределения (макет П2, вариант 1): та же сетка разделов ЛСР, что у сопоставления,
 * с колонкой «Распределено» на месте «Вида работы в плане». Стоимость остаётся на месте (CORE-33): без права она
 * называет причину. Правило раздела — `Select` в заголовке раздела.
 */
export function DistributionGrid({ distribution, lines, rows, hidden, name, filtering, onToggle, actions }: {
  distribution: Distribution;
  lines: DistGridLine[];
  /** Строки редакции — ради шифра и стоимости: в ответе распределения их нет. */
  rows: ReadonlyMap<string, EstimateRow>;
  hidden: HiddenPart[] | undefined;
  name: SectionName;
  filtering: boolean;
  onToggle: (key: string) => void;
  actions: GridActions;
}) {
  const rules = new Map(distribution.rules.map(r => [lsrKey(r.lsrSectionCode), r]));
  const deleted = new Set(distribution.targets.sections.filter(s => s.deleted).map(s => s.sectionId));
  const shownRows = lines.flatMap(l => (l.kind === 'row' ? [l.line.rowId] : []));
  const allOn = shownRows.length > 0 && shownRows.every(id => actions.selected.has(id));
  const someOn = shownRows.some(id => actions.selected.has(id));
  const closed = costHidden(hidden);
  const line = (l: DistGridLine) => (l.kind === 'row' ? l.line : null)!;

  const columns: DataGridColumn<DistGridLine>[] = [
    ...(actions.onSelect ? [{
      key: 'pick', label: 'Выбор', width: 'w-12', interactive: true,
      header: <Checkbox checked={allOn} indeterminate={!allOn && someOn} aria-label="Выбрать все показанные строки"
        onChange={on => actions.onSelect!(shownRows, on)} />,
      cell: (l: DistGridLine) => <Checkbox checked={actions.selected.has(line(l).rowId)} aria-label={`Выбрать строку ${line(l).number}`}
        onChange={on => actions.onSelect!([line(l).rowId], on)} />,
    }] : []),
    { key: 'number', label: '№', width: 'w-12', cell: l => <span className="tabular-nums text-on-surface-variant">{line(l).number}</span> },
    {
      key: 'rateCode', label: 'Шифр', width: 'w-36',
      cell: l => { const code = rows.get(line(l).rowId)?.rateCode; return <Tooltip content={code ?? undefined}><span className="block truncate font-mono text-body-small">{code}</span></Tooltip>; },
    },
    { key: 'name', label: 'Наименование', wrap: true, rowHeader: true, cell: l => <span className="text-body-medium">{line(l).name}</span> },
    {
      key: 'volume', label: 'Объём', align: 'right', width: 'w-20',
      cell: l => { const v = line(l).volume; return v === null ? null : <span className="tabular-nums">{formatQuantity(v.row)}</span>; },
    },
    { key: 'unit', label: 'Ед.', width: 'w-16', cell: l => line(l).units.row },
    {
      key: 'distributed', label: 'Распределено', width: 'w-48', wrap: true, interactive: true,
      cell: l => <DistributedCell line={line(l)} rule={rules.get(lsrKey(line(l).lsrSectionCode))} name={name} deleted={deleted}
        onSplit={actions.onSplit} busy={actions.busy} />,
    },
    {
      key: 'cost', label: 'Стоимость', align: 'right', width: 'w-32',
      unavailable: closed ? 'no-right' : null,
      unavailableHint: closed ? `Стоимость видна с правом «${closed.name}» — его выдаёт администратор.` : undefined,
      cell: l => { const t = rows.get(line(l).rowId)?.cost?.total; return t == null ? null : <span className="tabular-nums">{formatAmount(t)}</span>; },
    },
  ];

  return (
    <DataGrid columns={columns} rows={lines} fixed rowKey={l => l.key} state={filtering ? 'filtered-out' : undefined}
      tone={l => (l.kind === 'row' && l.line.targets.some(t => deleted.has(t)) ? 'warning' : null)}
      section={l => (l.kind === 'section'
        ? <RuleHeading rule={l.rule} open={l.open} shown={l.shown} filtering={filtering} distribution={distribution} name={name}
            onToggle={() => onToggle(lsrKey(l.rule.lsrSectionCode))} actions={actions} />
        : null)} />
  );
}

/**
 * Клетка «Распределено»: куда идёт строка, части — в подсказке полностью. «Разделить…» — у строки с объёмом;
 * без объёма действие скрыто с причиной, а правилом раздела строка распределяется всё равно (§77 F.1).
 */
function DistributedCell({ line, rule, name, deleted, onSplit, busy }: {
  line: DistributionLine;
  rule: DistributionRule | undefined;
  name: SectionName;
  deleted: ReadonlySet<string>;
  onSplit: GridActions['onSplit'];
  busy: boolean;
}) {
  const summary = lineSummary(line, rule, name, deleted);
  const text = <StatusText tone={summary.tone} title={summary.full}>{summary.text}</StatusText>;
  return (
    <div className="flex flex-col items-start gap-0.5">
      {text}
      {onSplit && (line.splittable
        ? <Button variant="text" disabled={busy} onClick={() => onSplit(line)}>{line.source === 'parts' ? 'Изменить…' : 'Разделить…'}</Button>
        : <span className="text-body-small text-on-surface-variant">без объёма — только правилом</span>)}
    </div>
  );
}

function RuleHeading({ rule, open, shown, filtering, distribution, name, onToggle, actions }: {
  rule: DistributionRule;
  open: boolean;
  shown: number;
  filtering: boolean;
  distribution: Distribution;
  name: SectionName;
  onToggle: () => void;
  actions: GridActions;
}) {
  const title = rule.lsrSectionCode === null
    ? 'Вне разделов'
    : `Раздел ${rule.lsrSectionCode}${rule.lsrSectionName ? `. ${rule.lsrSectionName}` : ''}`;
  const value = rule.target === null ? '' : rule.target.kind === 'whole' ? WHOLE : rule.target.sectionId;
  const sections = distribution.targets.sections.filter(s => !s.deleted || s.sectionId === value);
  return (
    <div className="flex flex-wrap items-center gap-2">
      {filtering ? (
        <span className="text-body-medium font-medium text-on-surface">{title}</span>
      ) : (
        <button type="button" aria-expanded={open} onClick={onToggle}
          className="inline-flex items-center gap-1 rounded-xs text-left text-body-medium font-medium text-on-surface hover:text-primary focus-visible:outline-2">
          <Icon icon={open ? KeyboardArrowDown : ChevronRight} size={18} aria-hidden className="shrink-0" />
          {title}
        </button>
      )}
      <span className="text-body-small text-on-surface-variant">
        {filtering
          ? `${formatCount(shown)} из ${formatCount(rule.rows)} ${ruPlural(rule.rows, 'строки', 'строк', 'строк')}`
          : `${formatCount(rule.rows)} ${ruPlural(rule.rows, 'строка', 'строки', 'строк')}`}
      </span>
      {rule.undistributed > 0 && <StatusText tone="neutral">не распределено: {formatCount(rule.undistributed)}</StatusText>}
      {rule.exceptions > 0 && <StatusText tone="neutral">исключений: {formatCount(rule.exceptions)}</StatusText>}
      <div className="ml-auto flex items-center gap-2">
        {actions.onReset && rule.exceptions > 0 && (
          <Button variant="text" disabled={actions.busy} onClick={() => actions.onReset!(rule)}>Сбросить исключения</Button>
        )}
        {actions.onRule ? (
          <Select density="dense" className="w-56" value={value} disabled={actions.busy} placeholder="Не распределено"
            aria-label={`Правило: ${title}`} emptyItem="Не распределено"
            onValueChange={v => actions.onRule!(rule, v === '' ? null : v === WHOLE ? { kind: 'whole' } : { kind: 'section', sectionId: v })}>
            <SelectGroup label="Разделы стройки">
              {sections.map(s => <SelectItem key={s.sectionId} value={s.sectionId}>{s.name ?? 'удалённый раздел'}</SelectItem>)}
            </SelectGroup>
            <SelectGroup label="Без раздела">
              <SelectItem value={WHOLE}>Стройка в целом — не распределять</SelectItem>
            </SelectGroup>
          </Select>
        ) : (
          <StatusText tone="neutral">{targetText(rule.target, name)}</StatusText>
        )}
      </div>
    </div>
  );
}
