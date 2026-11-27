import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Chip } from '@/shared/ui/Chip';
import { CountText } from '@/shared/ui/CountText';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusText } from '@/shared/ui/StatusText';
import { useTheme } from '@/shared/ui/themeContext';
import { ArrowBack, Warning } from '@/shared/ui/icons';

type Field = 'location' | 'materials';
type Line = { name: string; place: string; material: string; missing: Field[] };
type Report = { id: string; employee: string; site: string; details: Line[]; customerFields?: boolean };
type Filter = 'required' | 'customer';

const labels = { location: 'Место работ', materials: 'Материалы' };
const start: Report[] = [
  { id: '1', employee: 'Андрей Лебедев', site: 'Главный корпус', customerFields: true, details: [
    { name: 'Прокладка кабеля', place: 'Щитовая А‑12', material: 'ВВГнг 3×2,5', missing: ['location'] },
    { name: 'Разметка трассы', place: 'Этаж 2', material: '—', missing: [] },
    { name: 'Монтаж лотка', place: 'Коридор Б', material: 'Лоток 100×50', missing: ['location', 'materials'] },
  ] },
  { id: '2', employee: 'Михаил Орлов', site: 'Главный корпус', details: [
    { name: 'Монтаж светильников', place: 'Холл', material: 'Светильник ДПО', missing: ['materials'] },
  ] },
  { id: '3', employee: 'Ирина Белова', site: 'Подстанция ТП‑2', details: [
    { name: 'Прокладка кабеля', place: 'Камера 3', material: 'ВВГнг 3×2,5', missing: ['location'] },
  ] },
  { id: '4', employee: 'Сергей Волков', site: 'Подстанция ТП‑2', details: [
    { name: 'Монтаж щита', place: 'Этаж 1', material: 'Щит ЩРН‑24', missing: [] },
  ] },
];

function missingOf(report: Report) {
  return report.details.flatMap((line, index) => line.missing.map(field => ({ line: index + 1, field })));
}

function Marks({ report }: { report: Report }) {
  const missing = missingOf(report);
  const location = missing.some(item => item.field === 'location');
  const materials = missing.some(item => item.field === 'materials');
  const text = location
    ? materials ? 'не указаны обязательные место работ и материалы' : 'не указано обязательное место работ'
    : 'не указаны обязательные материалы';
  return <div className="flex flex-wrap gap-2">
    {missing.length > 0 && <StatusText tone="warning" icon={Warning}>{text}</StatusText>}
    {report.customerFields && <StatusText tone="warning" icon={Warning}>не заполнены поля: 1</StatusText>}
    {missing.length === 0 && !report.customerFields && <span aria-label="Без пометок">—</span>}
  </div>;
}

function MissingList({ report }: { report: Report }) {
  return <ul className="space-y-1 text-body-medium">
    {missingOf(report).map(item => <li key={`${item.line}-${item.field}`}>Строка {item.line} · {labels[item.field]}</li>)}
  </ul>;
}

function Detail({ report, variant, onFix, onBack }: {
  report: Report; variant: 'a' | 'b' | 'c'; onFix: (field: Field) => void; onBack: () => void;
}) {
  const missing = missingOf(report);
  return <div className="min-w-0 space-y-3">
    <PageHeader title={report.employee} subtitle={`${report.site} · 16.11 · день`}
      actions={<Button variant="text" icon={<Icon icon={ArrowBack} />} onClick={onBack}>К очереди</Button>} />
    <Marks report={report} />
    {variant === 'a' && missing.length > 0 && <Banner tone="warning">
      <div className="space-y-2"><strong>Нужно заполнить для ИД</strong><MissingList report={report} /></div>
    </Banner>}
    {variant === 'b' && missing.length > 0 && <Card title="Недостающие данные" icon={Warning} iconTone="warning">
      <MissingList report={report} />
    </Card>}
    <Card title="Строки отчёта">
      <ol className="divide-y divide-outline-variant">
        {report.details.map((line, index) => <li key={line.name} className="space-y-1 py-2 text-body-medium">
          <strong>{index + 1}. {line.name}</strong>
          <div className="grid gap-1 sm:grid-cols-2">
            <span>Место · {line.missing.includes('location') ? '—' : line.place}</span>
            <span>Материалы · {line.missing.includes('materials') ? '—' : line.material}</span>
          </div>
          {variant === 'c' && line.missing.map(field => <StatusText key={field} tone="warning" icon={Warning}>
            {labels[field]} · обязательно
          </StatusText>)}
        </li>)}
      </ol>
    </Card>
    <div className="flex flex-wrap gap-2" aria-label="Действия макета">
      {missing.some(item => item.field === 'location') &&
        <Button variant="tonal" onClick={() => onFix('location')}>Заполнить места в примере</Button>}
      {missing.some(item => item.field === 'materials') &&
        <Button variant="tonal" onClick={() => onFix('materials')}>Добавить материалы в примере</Button>}
    </div>
  </div>;
}

const columns: DataGridColumn<Report>[] = [
  { key: 'employee', label: 'Сотрудник', rowHeader: true, wrap: true, cell: report => report.employee },
  { key: 'site', label: 'Стройка', wrap: true, cell: report => report.site },
  { key: 'marks', label: 'Пометки', wrap: true, cell: report => <Marks report={report} /> },
];

export function ReviewMockup({ variant }: { variant: 'a' | 'b' | 'c' }) {
  const { resolvedTheme, setTheme } = useTheme();
  const [reports, setReports] = useState(start);
  const [filter, setFilter] = useState<Filter>();
  const [opened, setOpened] = useState<string>();
  const [expanded, setExpanded] = useState<string>();
  const [narrow, setNarrow] = useState(false);
  const report = reports.find(item => item.id === opened);
  const requiredCount = reports.filter(item => missingOf(item).length > 0).length;
  const customerCount = reports.filter(item => item.customerFields).length;
  const visible = reports.filter(item => filter === 'required' ? missingOf(item).length > 0 : filter !== 'customer' || item.customerFields);
  const queue = <section className="min-w-0 space-y-3" aria-label="Очередь приёмки">
    <PageHeader title="Приёмка" subtitle="Отчёты ваших строек (2)" badge={<CountText>Ждут решения · {visible.length}</CountText>} />
    <div className="flex flex-wrap gap-2" role="group" aria-label="Отбор по пометкам">
      {requiredCount > 0 && <Chip icon={Warning} count={requiredCount} selected={filter === 'required'}
        onClick={() => setFilter(filter === 'required' ? undefined : 'required')}>не заполнены место или материалы</Chip>}
      <Chip icon={Warning} count={customerCount} selected={filter === 'customer'}
        onClick={() => setFilter(filter === 'customer' ? undefined : 'customer')}>не заполнены поля</Chip>
      {filter && <Button variant="text" onClick={() => setFilter(undefined)}>Сбросить отбор</Button>}
    </div>
    {visible.length === 0 && <Banner tone="info">Отбор скрыл все отчёты</Banner>}
    {variant === 'c' || narrow ? <div className="space-y-2">
      {visible.map(item => <Card key={item.id} title={item.employee} actions={<>
        {variant === 'c' && missingOf(item).length > 0 && <Button variant="text" aria-expanded={expanded === item.id}
          onClick={() => setExpanded(expanded === item.id ? undefined : item.id)}>Пропуски по строкам</Button>}
        <Button variant="tonal" onClick={() => setOpened(item.id)}>Открыть отчёт</Button>
      </>}>
        <p className="text-body-small text-on-surface-variant">{item.site}</p><Marks report={item} />
        {variant === 'c' && expanded === item.id && <MissingList report={item} />}
      </Card>)}
    </div> : <DataGrid columns={columns} rows={visible} rowKey={item => item.id} onRowOpen={item => setOpened(item.id)} />}
  </section>;

  function fix(field: Field) {
    setReports(previous => previous.map(item => item.id === opened ? {
      ...item, details: item.details.map(line => ({ ...line, missing: line.missing.filter(value => value !== field) })),
    } : item));
  }

  return <main className={`mx-auto space-y-4 p-4 ${narrow ? 'max-w-[420px]' : 'max-w-[1280px]'}`}>
    <div className="flex flex-wrap gap-2" aria-label="Настройки просмотра макета">
      <Chip selected={narrow} onClick={() => setNarrow(!narrow)}>Узкий экран</Chip>
      <Chip selected={resolvedTheme === 'dark'} onClick={() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark')}>Тёмная тема</Chip>
      <Button variant="text" onClick={() => { setReports(start); setFilter(undefined); setOpened(undefined); setExpanded(undefined); }}>
        Сбросить пример
      </Button>
    </div>
    <div className={variant === 'b' && report && !narrow ? 'grid items-start gap-4 lg:grid-cols-2' : undefined}>
      {(!report || variant === 'b' && !narrow) && queue}
      {report && <Detail report={report} variant={variant} onFix={fix} onBack={() => setOpened(undefined)} />}
    </div>
  </main>;
}
