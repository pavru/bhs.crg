import { useState } from 'react';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { Banner } from '@/shared/ui/Banner';
import { CountText } from '@/shared/ui/CountText';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { Tooltip } from '@/shared/ui/Tooltip';
import { permissions, type ProgressSample, type Variant, type WorkRow } from './sample';

const number = new Intl.NumberFormat('ru-RU');

function valueText(value: number, unit: string) {
  return `${number.format(value)} ${unit}`;
}

function Missing({ text }: { text: string }) {
  return <Tooltip content={text}><span className="text-on-surface-variant" aria-label={text}>—</span></Tooltip>;
}

function volumeCell(key: 'plan' | 'fact', partial: boolean) {
  return (row: WorkRow) => {
    const value = row[key];
    if (value != null) return <span className="whitespace-nowrap tabular-nums">{valueText(value, row.unit)}</span>;
    return <Missing text={key === 'plan' ? 'Нет планового объёма' : partial ? 'Нет включённого объёма' : 'Нет учтённого объёма'} />;
  };
}

function columns(sample: ProgressSample, withDates: boolean, narrow: boolean): DataGridColumn<WorkRow>[] {
  const volumeWidth = narrow ? 'w-22' : 'w-36';
  return [
    {
      key: 'name', label: 'Вид работы', rowHeader: true, width: narrow ? 'w-44' : withDates ? 'w-2/5' : undefined, wrap: true,
      cell: row => <>
        {row.name}
        {withDates && narrow && <span className="block text-body-small text-on-surface-variant">
          {sample.schedule ? row.dates && <><span className="sr-only">Срок по графику </span>{row.dates}</>
            : <Tooltip content={`Скрыто правами · ${permissions.schedule}`}><span>Срок скрыт</span></Tooltip>}
        </span>}
      </>,
    },
    {
      key: 'plan', label: 'План по смете', align: 'right', width: volumeWidth,
      header: narrow ? <span className="whitespace-normal">План по смете</span> : undefined,
      unavailable: sample.plan ? null : 'no-right', unavailableHint: `Скрыто правами · ${permissions.plan}`,
      cell: volumeCell('plan', false),
    },
    ...(sample.workOff ? [] : [{
      key: 'fact', label: 'Факт по учёту', align: 'right' as const, width: volumeWidth,
      unavailable: sample.fact ? null : 'no-right' as const,
      unavailableHint: `Скрыто правами · ${permissions.fact}`,
      header: (sample.partial || narrow) ? <span className="inline-flex flex-col whitespace-normal leading-tight">
        <span>Факт по учёту</span>
        {sample.partial && <span className="text-label-small text-on-surface-variant">
          {narrow ? 'Включено' : 'Включённый объём'}
        </span>}
      </span> : undefined,
      cell: volumeCell('fact', sample.partial),
    }]),
    ...(withDates && !narrow ? [{
      key: 'dates', label: 'Срок по графику',
      unavailable: sample.schedule ? null : 'no-right' as const,
      unavailableHint: `Скрыто правами · ${permissions.schedule}`,
      cell: (row: WorkRow) => row.dates ?? <Missing text="Срок отсутствует" />,
    }] : []),
  ];
}

function Hidden({ permission }: { permission: string }) {
  return <span className="text-on-surface-variant">Скрыто правами · <code>{permission}</code></span>;
}

function WorkDetails({ row, sample }: { row: WorkRow; sample: ProgressSample }) {
  return <aside className="p5-inspector" aria-label="Подробности работы">
    <h3 className="text-title-medium" aria-live="polite">{row.name}</h3>
    <dl className="p5-detail-values text-body-medium">
      <div><dt>План по смете</dt><dd>{sample.plan
        ? row.plan == null ? 'Нет планового объёма' : valueText(row.plan, row.unit)
        : <Hidden permission={permissions.plan} />}</dd></div>
      <div><dt>Срок по графику</dt><dd>{sample.schedule ? row.dates ?? 'Срок отсутствует'
        : <Hidden permission={permissions.schedule} />}</dd></div>
      <div><dt>Факт по учёту</dt><dd>{sample.workOff ? 'Учёт работ не подключён' : sample.fact
        ? row.fact == null ? sample.partial ? 'Нет включённого объёма' : 'Нет учтённого объёма' : valueText(row.fact, row.unit)
        : <Hidden permission={permissions.fact} />}</dd></div>
    </dl>
    {sample.partial && <Banner tone="warning">
      Факт неполный. Причины и охват по всей стройке указаны над таблицей.
    </Banner>}
  </aside>;
}

const groups = [
  { value: 'all', label: 'Все работы', short: 'Все', includes: () => true },
  { value: 'both', label: 'План и факт', short: 'План и факт', includes: (row: WorkRow) => row.plan != null && row.fact != null },
  { value: 'plan', label: 'Только план', short: 'План', includes: (row: WorkRow) => row.plan != null && row.fact == null },
  { value: 'fact', label: 'Только факт', short: 'Факт', includes: (row: WorkRow) => row.plan == null && row.fact != null },
] as const;
type Group = (typeof groups)[number]['value'];

type GroupRow = WorkRow & { groupTitle?: string; count?: number };

function groupedRows(rows: WorkRow[]): GroupRow[] {
  const sections = [
    ...groups.slice(1),
    { value: 'no-volumes', label: 'Без объёмов', includes: (row: WorkRow) => row.plan == null && row.fact == null },
  ];
  return sections.flatMap(({ value, label, includes }) => {
    const matches = rows.filter(includes);
    return matches.length ? [{ id: value, name: '', unit: '', groupTitle: label, count: matches.length }, ...matches] : [];
  });
}

function GroupedTable({ sample, narrow }: { sample: ProgressSample; narrow: boolean }) {
  const [group, setGroup] = useState<Group>('all');
  const canCompare = sample.plan && sample.fact;
  const active = canCompare ? group : 'all';
  const rows = sample.rows.filter(groups.find(item => item.value === active)!.includes);
  const arranged = canCompare && active === 'all' ? groupedRows(rows) : rows;
  return <div className="min-w-0">
    {canCompare ? <>
      <Tabs id="p5-groups" aria-label="Наличие плана и факта" value={active} onChange={setGroup}
        items={groups.map(item => ({
          value: item.value,
          label: narrow ? item.short : item.label,
          badge: <CountText>{sample.rows.filter(item.includes).length}</CountText>,
        }))} />
      <TabPanel id="p5-groups" value={active} className="min-w-0 pt-3">
        {sample.partial && <p className="mb-2 text-body-small text-on-surface-variant">
          Группы сформированы по плану и включённому факту.
        </p>}
        <DataGrid<GroupRow> rows={arranged} columns={columns(sample, true, narrow)} rowKey={row => row.id} pinned={1} fixed
          state={active === 'all' ? 'no-data' : 'filtered-out'} section={row => row.groupTitle
            ? <span className="inline-flex items-center gap-2">{row.groupTitle}<CountText>{row.count}</CountText></span>
            : null} />
      </TabPanel>
    </> : <DataGrid rows={rows} columns={columns(sample, true, narrow)} rowKey={row => row.id} pinned={1} fixed />}
  </div>;
}

export function ProgressTables({ variant, sample }: { variant: Variant; sample: ProgressSample }) {
  const [selected, setSelected] = useState<string>();
  const narrow = useMedia(NARROW);
  const row = sample.rows.find(item => item.id === selected) ?? sample.rows[0];
  if (variant === 'c') return <GroupedTable sample={sample} narrow={narrow} />;
  if (variant === 'b') return <div className="p5-split">
    <DataGrid rows={sample.rows} columns={columns(sample, false, narrow)} rowKey={item => item.id} pinned={1} fixed
      onRowOpen={item => setSelected(item.id)} rowOpen={item => item.id === row?.id} />
    {row && <WorkDetails row={row} sample={sample} />}
  </div>;
  return <DataGrid rows={sample.rows} columns={columns(sample, true, narrow)} rowKey={item => item.id} pinned={1} fixed />;
}
