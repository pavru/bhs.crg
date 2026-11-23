import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import type { Persona, Scene, Variant } from './sample';

interface Price {
  id: string; name: string; code: string; unit: string;
  effective?: { id: string; date: string; unit: string; amount: string };
  history: { id: string; date: string; unit: string; amount: string }[];
}

const current = { id: 'current', date: '2026-11-01', unit: 'м', amount: '350' };
const prices: Price[] = [
  { id: 'tray', name: 'Монтаж кабельного лотка', code: 'ЭМ-014', unit: 'м', effective: current,
    history: [current, { id: 'summer', date: '2026-07-01', unit: 'м', amount: '320' }, { id: 'old', date: '2026-06-01', unit: 'шт.', amount: '190' }] },
  { id: 'control', name: 'Проверка цепей управления', code: 'ЭМ-027', unit: 'шт.', history: [] },
];

function PriceEdit({ work, entry, scene, onClose, onSave }: {
  work: Price; entry?: Price['effective']; scene: Scene; onClose: () => void;
  onSave: (work: Price, entry: NonNullable<Price['effective']>) => void;
}) {
  const [date, setDate] = useState(entry?.date ?? '2026-11-23');
  const [value, setValue] = useState(entry?.amount ?? '');
  const [error, setError] = useState(false);
  const [reviewed, setReviewed] = useState(false);
  const stale = scene === 'stale-offer' && Boolean(entry);
  function save() {
    if (scene === 'forbidden' || scene === 'validation' || stale && !reviewed) { setError(true); return; }
    onSave(work, { id: entry?.id ?? 'added', date, unit: entry?.unit ?? work.unit, amount: value });
  }
  return <Modal open title={entry ? 'Изменить цену' : 'Новая цена'} onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalPrimary onClick={save} disabled={!date || !value || stale && error}>Сохранить цену</ModalPrimary>}>
    <div className="grid gap-3">
      <p className="text-body-medium">{work.code} · {work.name} · {entry?.unit ?? work.unit}</p>
      <DateField label="Действует с" value={date} onChange={setDate} disabled={Boolean(entry)} />
      <NumberInput label="Цена за единицу" value={value} onChange={setValue} unit="₽" />
      {error && <Banner tone="error" actions={stale ? [{ label: 'Перечитать', onClick: () => { setError(false); setReviewed(true); } }] : undefined}>
        {stale ? 'Цену уже изменили. Перечитайте запись, затем сверьте введённое значение.'
          : scene === 'forbidden' ? 'Нет права изменить цену. Данные остались в форме.' : 'Сервер отклонил цену. Проверьте заполненные поля.'}
      </Banner>}
      {reviewed && <Banner tone="info">Цена перечитана. Введённое значение осталось в поле.</Banner>}
    </div>
  </Modal>;
}

export function PriceList({ variant, persona, scene, onCreate }: { variant: Variant; persona: Persona; scene: Scene; onCreate?: () => void }) {
  const canBrowseCatalog = persona === 'manager' || persona === 'cost' || persona === 'editor';
  const [rows, setRows] = useState(() => canBrowseCatalog ? prices : prices.filter(row => row.history.length > 0));
  const [selected, setSelected] = useState<string | null>(null);
  const [edit, setEdit] = useState<{ work: Price; entry?: Price['effective'] } | null>(null);
  const [date, setDate] = useState('2026-11-23');
  const canEdit = persona === 'manager' || persona === 'cost' || persona === 'price-editor';
  const work = rows.find(row => row.id === selected);
  const columns: DataGridColumn<Price>[] = [
    { key: 'name', label: 'Вид работы', rowHeader: true, wrap: true, cell: row => <>{row.name}<small className="block text-on-surface-variant">{row.code}</small></> },
    { key: 'unit', label: 'Единица', cell: row => row.unit },
    { key: 'amount', label: 'Действующая цена', align: 'right', cell: row => row.effective ? `${row.effective.amount} ₽` : '—' },
    { key: 'from', label: 'Действует с', cell: row => row.effective?.date.split('-').reverse().join('.') ?? '—' },
  ];
  const history = work && <section className="grid min-w-0 grid-cols-1 gap-3" aria-label="История цен">
    <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="text-title-medium">{work.name}</h3>
      {canEdit && <Button variant="tonal" onClick={() => setEdit({ work })}>Новая цена…</Button>}</div>
    {work.history.length === 0 ? <p className="text-body-medium">Для этой работы цены ещё не заводили.</p> : <DataGrid
      density="compact" rows={work.history} rowKey={row => row.id} columns={[
        { key: 'date', label: 'С даты', rowHeader: true, cell: row => row.date.split('-').reverse().join('.') },
        { key: 'unit', label: 'Единица', cell: row => row.unit },
        { key: 'amount', label: 'Цена', align: 'right', cell: row => `${row.amount} ₽` },
        { key: 'state', label: 'Состояние', interactive: canEdit, cell: row => <div className="flex flex-wrap items-center gap-1">
          <StatusText tone={row.id === work.effective?.id ? 'success' : 'neutral'}>{row.id === work.effective?.id ? 'Действует' : 'Из истории'}</StatusText>
          {canEdit && <Button variant="text" onClick={() => setEdit({ work, entry: row })}>Изменить…</Button>}
        </div> },
      ]} />}
  </section>;
  return <section className="grid min-w-0 grid-cols-1 gap-4">
    <PageHeader title="Прайс-лист" actions={onCreate && <Button variant="filled" onClick={onCreate}>КП по прайс-листу…</Button>} />
    <div className="max-w-56"><DateField label="Цены на дату" value={date} onChange={setDate} /></div>
    {canEdit && !canBrowseCatalog && <Banner tone="info">Чтобы добавить новый вид работы, нужно право «Читать справочники» (<code>core.catalog.read</code>).</Banner>}
    <DataGrid rows={rows} columns={columns} rowKey={row => row.id} onRowOpen={row => setSelected(row.id)} rowOpen={row => row.id === selected} pinned={1} />
    {variant === 'split' ? <SideSheet open={Boolean(work) && !edit} variant="modal" title="История цен" onClose={() => setSelected(null)}>{history}</SideSheet>
      : variant === 'sections' && work && !edit ? <Modal open wide nothingToLose title="История цен" onOpenChange={open => { if (!open) setSelected(null); }}>{history}</Modal> : history}
    {edit && <PriceEdit work={edit.work} entry={edit.entry} scene={scene} onClose={() => setEdit(null)} onSave={(before, entry) => {
      setRows(previous => previous.map(row => row.id !== before.id ? row : { ...row,
        effective: entry.unit === row.unit && entry.date <= date && (!row.effective || entry.date >= row.effective.date) ? entry : row.effective,
        history: [entry, ...row.history.filter(item => item.id !== entry.id)] }));
      setEdit(null);
    }} />}
  </section>;
}
