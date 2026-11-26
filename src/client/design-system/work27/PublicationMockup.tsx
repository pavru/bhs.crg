import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { Modal } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Switch } from '@/shared/ui/Switch';
import { Tabs } from '@/shared/ui/Tabs';
import { Group } from '@/shared/ui/icons';

const scenarios = {
  sections: { label: 'Разные позиции', reason: 'В строках группы разные позиции перечня.', accepted: 3, count: 3 },
  waiting: { label: 'Частичная приёмка', reason: 'Один из поданных отчётов ждёт приёмки.', accepted: 2, count: 3 },
  published: { label: 'Итог включён в факт', reason: '', accepted: 3, count: 3 },
  unsubmitted: { label: 'Есть неподанный отчёт', reason: '', accepted: 2, count: 2 },
  stale: { label: 'Поздняя подача', reason: 'После утверждения появился ещё один отчёт. Утвердите итог повторно.', accepted: 2, count: 3 },
  pending: { label: 'Итог ещё не утверждён', reason: 'Группа ждёт утверждения итога.', accepted: 3, count: 3 },
  empty: { label: 'Нет поданных строк', reason: 'В группе нет текущих поданных строк.', accepted: 0, count: 0 },
} as const;

type Scenario = keyof typeof scenarios;
type Row = { id: string; name: string; role: string; volume: number };
const rows: Row[] = [
  { id: '1', name: 'Сергей Соколов', role: 'Бригадир', volume: 120 },
  { id: '2', name: 'Елена Волкова', role: 'Участник', volume: 120 },
  { id: '3', name: 'Игорь Орлов', role: 'Участник', volume: 100 },
];
const columns: DataGridColumn<Row>[] = [
  { key: 'name', label: 'Сотрудник', rowHeader: true, wrap: true },
  { key: 'role', label: 'Роль' },
  { key: 'variant', label: 'Вариант работы', wrap: true, cell: () => 'В кабельном лотке' },
  { key: 'volume', label: 'Объём', align: 'right' },
  { key: 'unit', label: 'Ед.', cell: () => 'м' },
];

const variants = { a: 'А · Статус в шапке группы', b: 'Б · Сводка над строками', c: 'В · Раскрываемые подробности' };

export function PublicationMockup({ variant }: { variant: keyof typeof variants }) {
  const [scenario, setScenario] = useState<Scenario>('sections');
  const [canReadReports, setCanReadReports] = useState(true);
  const [expanded, setExpanded] = useState(false);
  const navigate = useNavigate();
  const { hash } = useLocation();
  const selected = rows.find(row => hash === `#report-${row.id}`);
  const [construction, setConstruction] = useState('river');
  const [from, setFrom] = useState('2026-11-26');
  const [to, setTo] = useState('2026-11-26');
  const sample = scenarios[scenario];
  const published = scenario === 'unsubmitted' || scenario === 'published';
  const awaitingApproval = scenario === 'pending' || scenario === 'stale';
  const groupVisible = construction !== 'north' && (!from || from <= '2026-11-26') && (!to || to >= '2026-11-26');
  const sections = scenario === 'sections' ? ['Корпус А · 2-й этаж', 'Корпус Б · 1-й этаж'] : ['Корпус А · 2-й этаж'];
  const status = <StatusText tone={published ? 'success' : 'warning'}>
    {published ? 'Включён в факт' : 'Не включён в факт'}
  </StatusText>;
  const accepted = <p className="text-body-medium">Принято отчётов · {sample.accepted} из {sample.count}</p>;
  const details = <>
    {sample.reason && <p className="text-body-medium">{sample.reason}</p>}
    {sample.count > 0 && <ul className="space-y-1 text-body-medium" aria-label="Позиции перечня">
      {sections.map(section => <li key={section}>
        <span>Прокладка кабеля · м</span>
        <span className="block text-on-surface-variant sm:inline"> · {section}</span>
      </li>)}
    </ul>}
  </>;
  const total = scenario !== 'pending' && <p className="text-body-medium">
    {scenario === 'stale' || scenario === 'empty' ? 'Прежний итог' : 'Итог'} · 120 м · Анна Крылова · 26.11.2026 09:15
  </p>;
  const warning = scenario === 'unsubmitted' && <p className="text-body-medium">Не подали отчёт · 1</p>;

  return <main className="mx-auto min-w-0 max-w-5xl space-y-5 p-4 sm:p-6">
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-64 max-w-full"><Select label="Сценарий макета" value={scenario}
        onValueChange={value => { setScenario(value as Scenario); setExpanded(false); }}>
        {Object.entries(scenarios).map(([id, item]) => <SelectItem key={id} value={id}>{item.label}</SelectItem>)}
      </Select></div>
      <div className="flex items-center gap-2 pb-1">
        <Switch checked={canReadReports} onChange={setCanReadReports} label="Доступны карточки отчётов" />
        <span className="text-body-medium">Доступны карточки отчётов</span>
      </div>
    </div>
    <h1 className="text-headline-small">Приёмка отчётов</h1>
    <div className="flex flex-wrap items-baseline justify-between gap-2">
      <Tabs id="work27-review" aria-label="Приёмка" value="crew" onChange={() => {}}
        items={[{ value: 'crew', label: 'Сверка бригад', icon: Group }]} />
      <span className="text-label-medium text-on-surface-variant">{variants[variant]}</span>
    </div>
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-56 max-w-full"><Select label="Стройка" density="dense" value={construction}
        emptyItem="Все стройки в охвате" onValueChange={setConstruction}>
        <SelectItem value="river">Речной квартал</SelectItem>
        <SelectItem value="north">Северный корпус</SelectItem>
      </Select></div>
      <DateRange label="Дата смены" from={from} to={to} ordered className="w-100 max-w-full"
        onChange={(start, end) => { setFrom(start); setTo(end); }} />
      {(construction || from || to) && <Button variant="text"
        onClick={() => { setConstruction(''); setFrom(''); setTo(''); }}>Сбросить отбор</Button>}
    </div>
    {groupVisible ? <section aria-labelledby="mockup-group" className="min-w-0 space-y-3 border-t border-outline-variant pt-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <h2 id="mockup-group" className="text-title-medium">Прокладка кабеля</h2>
          <p className="text-body-medium text-on-surface-variant">Речной квартал · 26.11.2026 · день</p>
          <div className="flex flex-wrap gap-x-4 gap-y-1">
            <StatusText tone={awaitingApproval || scenario === 'empty' ? 'warning' : 'success'}>
              {scenario === 'pending' ? 'Ожидает утверждения' : scenario === 'stale' || scenario === 'empty' ? 'Требует решения' : 'Утверждено'}
            </StatusText>
            {sample.count === 3 && <StatusText tone="warning" icon={Group}>Есть расхождение в объёмах</StatusText>}
          </div>
          {variant !== 'b' && total}
          {variant === 'a' && <div className="space-y-1 pt-1">{status}{details}{accepted}{warning}</div>}
        </div>
        {awaitingApproval && <Button variant="tonal"
          onClick={() => setScenario(scenario === 'stale' ? 'waiting' : 'published')}>Утвердить итог</Button>}
      </div>
      {variant === 'b' && <Card variant="filled" className="grid gap-3 p-3 sm:grid-cols-2">
        <div className="space-y-1"><h3 className="text-title-small">Итог сверки</h3>{total}{accepted}{warning}</div>
        <div className="space-y-1"><h3 className="text-title-small">Факт по перечню работ</h3>{status}{details}</div>
      </Card>}
      {variant === 'c' && <div className="space-y-2 border-y border-outline-variant py-2">
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
          {status}{accepted}
          <Button variant="text" aria-controls="publication-details" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
            {expanded ? 'Свернуть подробности' : 'Подробности публикации'}
          </Button>
        </div>
        {sample.reason && !expanded && <p className="text-body-medium">{sample.reason}</p>}
        {expanded && <div id="publication-details" className="space-y-1">{details}</div>}
        {warning}
      </div>}
      {sample.count > 0 && <DataGrid columns={columns} rows={rows.slice(0, sample.count)} rowKey={row => row.id}
        onRowOpen={canReadReports ? row => navigate(`#report-${row.id}`) : undefined}
        rowLink={canReadReports ? row => ({ to: `#report-${row.id}`, label: `Открыть отчёт · ${row.name}` }) : undefined} />}
    </section> : <p className="text-body-medium">Нет групп сверки. Измените стройку или период.</p>}
    {canReadReports && selected && <Modal open onOpenChange={open => { if (!open) navigate({ hash: '' }); }}
      title="Отчёт монтажника" nothingToLose>
      <p className="text-body-medium">{selected.name} · Прокладка кабеля · {selected.volume} м</p>
    </Modal>}
  </main>;
}
