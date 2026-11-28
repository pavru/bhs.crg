import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { PageHeader } from '@/shared/ui/PageHeader';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { formatMoney } from '@/shared/format/format';

type Row = { id: string; name: string; estimate: number | null; allocated: number; paid: number; section: string; warning?: string };
const sample: Row[] = [
  { id: 'cable', name: 'Кабель ВВГнг-LS 3×2,5', estimate: 100000, allocated: 120000, paid: 90000, section: 'power' },
  { id: 'pipe', name: 'Труба гофрированная 20 мм', estimate: 36000, allocated: 24000, paid: 24000, section: 'power' },
  { id: 'washer', name: 'Шайба М8 кузовная DIN9021', estimate: 388.38, allocated: 388.38, paid: 0, section: 'network' },
  { id: 'tray', name: 'Лоток перфорированный 100×50', estimate: null, allocated: 18500, paid: 18500, section: 'network' },
  { id: 'box', name: 'Коробка распределительная', estimate: 5400, allocated: 0, paid: 0, section: 'power' },
  { id: 'water', name: 'Вода техническая', estimate: null, allocated: 120, paid: 120, section: 'network' },
  { id: 'none', name: 'Без позиции номенклатуры', estimate: null, allocated: 4500, paid: 0, section: 'power' },
];
const variants = [
  { value: 'a', label: 'А · Общая таблица' },
  { value: 'b', label: 'Б · Список и подробности' },
  { value: 'c', label: 'В · Группы отклонений' },
] as const;
const scenarios = [
  { value: 'normal', label: 'Данные доступны' },
  { value: 'hidden', label: 'Без права на смету' },
  { value: 'off', label: 'Plan не подключён' },
  { value: 'tax', label: 'НДС факта неизвестен' },
  { value: 'basis', label: 'Основа сметы неизвестна' },
  { value: 'empty', label: 'Данных нет' },
] as const;

function remember(key: string, value: string) {
  const url = new URL(location.href);
  url.searchParams.set(key, value);
  history.replaceState(null, '', url);
}

export function Review({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState(params.get('page') ?? 'a');
  const [scenario, setScenario] = useState(params.get('scenario') ?? 'normal');
  const [section, setSection] = useState('all');
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const [selected, setSelected] = useState('cable');
  const [narrow, setNarrow] = useState(false);
  const [tab, setTab] = useState('comparison');
  const [exported, setExported] = useState(false);
  const blocked = scenario === 'hidden' || scenario === 'off';
  const unavailable = scenario === 'hidden' ? 'Скрыто правами · plan.estimate.cost' : 'Не подключено';
  const rows = (scenario === 'empty' ? [] : sample).map(row => ({
    ...row,
    warning: row.id === 'cable' && scenario === 'tax' ? 'НДС неизвестен — учтено полной суммой'
      : row.id === 'cable' && scenario === 'basis' ? 'Основа сметной суммы без НДС не подтверждена' : undefined,
  })).filter(row => (section === 'all' || row.section === section)
    && row.name.toLocaleLowerCase('ru-RU').includes(query.toLocaleLowerCase('ru-RU'))
    && (filter !== 'excess' || !blocked && !row.warning && row.estimate !== null && row.allocated > row.estimate));
  const opened = rows.find(row => row.id === selected) ?? rows[0];
  const estimate = (row: Row) => blocked ? unavailable : row.estimate === null ? 'Нет в смете' : formatMoney(row.estimate);
  const difference = (row: Row) => blocked ? unavailable : row.warning ? 'Не определено' : row.estimate === null
    ? 'Нет в смете' : formatMoney(row.allocated - row.estimate);
  const columns: DataGridColumn<Row>[] = [
    { key: 'name', label: 'Материал', rowHeader: true, wrap: true, cell: row => row.name },
    { key: 'estimate', label: 'Смета без НДС', align: 'right', wrap: true, cell: estimate },
    { key: 'allocated', label: 'Разнесено без НДС', align: 'right', cell: row => formatMoney(row.allocated) },
    { key: 'paid', label: 'Оплачено без НДС', align: 'right', cell: row => formatMoney(row.paid) },
    { key: 'difference', label: 'Отклонение', align: 'right', wrap: true, cell: difference },
  ];
  function groupOf(row: Row) {
    if (blocked || row.estimate === null || row.warning) return 'Сравнение недоступно';
    return row.allocated > row.estimate ? 'Превышение сметы' : row.allocated < row.estimate ? 'В пределах сметы' : 'Совпадает со сметой';
  }
  const groups = [...new Set(rows.map(groupOf))];
  return <>
    <header className="border-b border-outline-variant bg-surface">
      <div className="mx-auto grid max-w-7xl gap-3 px-4 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <a className="focus-ring text-title-medium text-primary" href="./?section=mockups&project=plan-27-estimate-vs-costs">BHS.CRG UI · План против факта · П6</a>
          <div className="flex items-center gap-3">
            <Button variant="text" onClick={() => setNarrow(value => !value)}>{narrow ? 'Широкий экран' : 'Узкий экран'}</Button>
            <ThemeToggle label="Тема макета" />
          </div>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Select label="Вариант макета" value={variant} onValueChange={value => { setVariant(value); remember('page', value); }}>
            {variants.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
          </Select>
          <Select label="Сценарий" value={scenario} onValueChange={value => { setScenario(value); setExported(false); remember('scenario', value); }}>
            {scenarios.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
          </Select>
        </div>
      </div>
    </header>
    <main className={`mx-auto grid min-w-0 gap-4 px-4 py-5 ${narrow ? 'max-w-[390px]' : 'max-w-7xl'}`}>
      <PageHeader title="Затраты по стройке" />
      <Tabs id="plan27-preview" items={[{ value: 'costs', label: 'Затраты' }, { value: 'comparison', label: 'План против факта' }]}
        value={tab} onChange={setTab} />
      <TabPanel id="plan27-preview" value={tab}>
        {tab === 'costs' ? <Banner tone="info">Для просмотра сравнения выберите вкладку «План против факта».</Banner> : <div className="grid min-w-0 gap-4">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div className="grid flex-1 gap-3 sm:grid-cols-2">
              <Select label="Стройка" value="komarova" onValueChange={() => {}}>
                <SelectItem value="komarova">Комарова, 36</SelectItem>
              </Select>
              <Select label="Раздел" value={section} onValueChange={setSection}>
                <SelectItem value="all">Все разделы</SelectItem>
                <SelectItem value="power">Электроснабжение</SelectItem>
                <SelectItem value="network">Слаботочные сети</SelectItem>
              </Select>
            </div>
            <Button variant="outlined" onClick={() => setExported(true)}>Выгрузить XLSX</Button>
          </div>
          <p className="text-body-small text-on-surface-variant">За всё время · с исправлениями · сравнение по разнесённому без НДС</p>
          {blocked && <Banner tone="info">{scenario === 'hidden'
            ? 'Смета скрыта правами. Нужно право plan.estimate.cost. Разнесено и оплачено доступны.'
            : 'Модуль «Планирование» не подключён. Разнесено и оплачено доступны.'}</Banner>}
          {rows.some(row => row.warning) && <Banner tone="warning">{rows.find(row => row.warning)!.warning}. Точное отклонение не определено.</Banner>}
          {exported && <Banner tone="success" actions={[{ label: 'Закрыть', onClick: () => setExported(false) }]}>
            Предпросмотр выгрузки · {rows.length} строк. {blocked ? 'Разнесено и оплачено.' : 'Смета, разнесено, оплачено и отклонение.'}
            {' '}Неопределённые значения сохраняются пустыми с пояснениями.
          </Banner>}
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label="Найти материал" value={query} onChange={event => setQuery(event.target.value)} />
            <SegmentedButton aria-label="Показать строки" items={[{ value: 'all', label: 'Все материалы' }, { value: 'excess', label: 'Превышение сметы' }]}
              value={filter} onChange={setFilter} />
          </div>
          {variant === 'b' ? <div className={`grid min-w-0 gap-4 ${narrow ? '' : 'lg:grid-cols-2'}`}>
            <DataGrid columns={[columns[0], columns[2]]} rows={rows} rowKey={row => row.id}
              onRowOpen={row => setSelected(row.id)} rowOpen={row => opened?.id === row.id} />
            {opened && <section className="grid content-start gap-4 rounded-lg border border-outline-variant bg-surface p-4" aria-label="Подробности материала">
              <h2 className="text-title-medium">{opened.name}</h2>
              <DataGrid density="compact" columns={[{ key: 'label', label: 'Показатель' }, { key: 'value', label: 'Сумма', align: 'right' }]}
                rows={[{ label: 'Смета без НДС', value: estimate(opened) }, { label: 'Разнесено без НДС', value: formatMoney(opened.allocated) },
                  { label: 'Оплачено без НДС', value: formatMoney(opened.paid) }, { label: 'Отклонение', value: difference(opened) }]} />
              {opened.warning && <p className="text-body-medium text-on-surface-variant">{opened.warning}</p>}
            </section>}
          </div> : variant === 'c' && groups.length > 0 ? groups.map(group => <section key={group} className="grid min-w-0 gap-2">
            <h2 className="text-title-medium">{group}</h2>
            <DataGrid columns={columns} rows={rows.filter(row => groupOf(row) === group)} rowKey={row => row.id} />
          </section>) : <DataGrid columns={columns} rows={rows} rowKey={row => row.id} pinned={1}
            footer={column => column.key === 'name' ? 'Итого по показанному' : column.key === 'allocated'
              ? formatMoney(rows.reduce((sum, row) => sum + row.allocated, 0)) : column.key === 'paid'
                ? formatMoney(rows.reduce((sum, row) => sum + row.paid, 0)) : null} />}
        </div>}
      </TabPanel>
    </main>
  </>;
}
