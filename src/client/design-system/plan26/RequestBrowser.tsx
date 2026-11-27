import { useState } from 'react';
import { formatDate, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { ListDetailShell } from '@/shared/ui/ListDetailShell';
import { ListItem } from '@/shared/ui/ListItem';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { siteName, sites, statusName, statuses, type Material, type Request, type Scenario, type Site, type Status, type Variant } from './sample';

const statusTone: Record<Status, string> = {
  draft: 'bg-surface-container-high text-on-surface-variant',
  submitted: 'bg-primary-container text-on-primary-container',
  closed: 'bg-secondary-container text-on-secondary-container',
};

export function RequestStatus({ status }: { status: Status }) {
  return <span className={`inline-block rounded-full px-2 py-0.5 text-label-medium ${statusTone[status]}`}>
    {statusName(status)}
  </span>;
}

const measure = (value: number | null) => value === null ? 'Неизвестно' : formatNumber(value);

function MaterialSummary({ request, scenario }: { request: Request; scenario: Scenario }) {
  const hidden = scenario === 'manager';
  const incomplete = scenario === 'no-lines' && request.invoices > 0;
  const columns: DataGridColumn<Material>[] = [
    { key: 'material', label: 'Материал', rowHeader: true, wrap: true, cell: row => <>
      {row.name}
      <span className="block text-body-small text-on-surface-variant">{row.unit}</span>
    </> },
    { key: 'ordered', label: 'Заказано', align: 'right', cell: row => measure(row.quantity) },
    { key: 'invoiced', label: 'Выставлено', align: 'right', wrap: true,
      cell: row => hidden ? 'Скрыто' : measure(incomplete ? null : row.invoiced) },
    { key: 'issued', label: 'Отпущено по всей стройке', align: 'right', wrap: true, cell: row => <>
      {measure(row.issued)}
      {row.mismatch && <span className="block text-body-small text-warning">Другая единица</span>}
    </> },
  ];
  return <div className="@container/summary grid min-w-0 gap-3">
    {hidden && <Banner tone="info">«Выставлено» скрыто. Нужно право чтения счетов.</Banner>}
    {incomplete && <Banner tone="info">У связанного счёта ещё нет строк. Итог «Выставлено» неизвестен.</Banner>}
    <div className="hidden min-w-0 @xl/summary:block">
      <DataGrid columns={columns} rows={request.rows} rowKey={row => row.id} density="compact" />
    </div>
    <div className="grid gap-2 @xl/summary:hidden">
      {request.rows.map(row => <Card key={row.id} title={row.name}>
        <dl className="grid gap-1 text-body-medium">
          {columns.slice(1).map((column, index) => <div key={column.key} className="flex justify-between gap-2">
            <dt className="text-on-surface-variant">{column.label}</dt>
            <dd className="text-right tabular-nums">{column.cell?.(row, index)}{row.unit && (index === 0 || (index === 1
              ? !hidden && !incomplete && row.invoiced !== null : row.issued !== null)) ? ` ${row.unit}` : ''}</dd>
          </div>)}
        </dl>
      </Card>)}
    </div>
  </div>;
}

type Action = 'edit' | 'return' | 'delete' | 'close' | 'submit';
const actionNames: Record<Action, string> = {
  edit: 'Изменить строки', return: 'Вернуть в черновик', delete: 'Удалить черновик',
  close: 'Закрыть заявку', submit: 'Подать заявку',
};

export function RequestDetail({ request, scenario, narrow, inDialog, onSave, onDelete }: {
  request: Request; scenario: Scenario; narrow: boolean; inDialog: boolean;
  onSave: (row: Request) => void; onDelete: (number: number) => void;
}) {
  const [action, setAction] = useState<Action>();
  const [reason, setReason] = useState('');
  const [rows, setRows] = useState(request.rows);
  const [conflict, setConflict] = useState<'stale' | 'linked'>();
  const [reloaded, setReloaded] = useState(false);
  const isDraft = request.status === 'draft';
  const isSubmitted = request.status === 'submitted';
  function open(next: Action) {
    setRows(request.rows);
    setReason('');
    setConflict(undefined);
    setAction(next);
  }
  function save() {
    if (!action) return;
    if (scenario === 'stale' && !reloaded) {
      setConflict('stale');
      return;
    }
    if (action === 'delete' && request.invoices > 0) {
      setConflict('linked');
      return;
    }
    if (action === 'delete') onDelete(request.number);
    if (action === 'edit') onSave({ ...request, rows });
    if (action === 'return') onSave({ ...request, status: 'draft' });
    if (action === 'submit') onSave({ ...request, status: 'submitted' });
    if (action === 'close') onSave({ ...request, status: 'closed' });
    setAction(undefined);
  }

  return <article className="grid w-full min-w-0 content-start gap-4 p-4 @2xl:p-5" aria-label={`Заявка № ${request.number}`}>
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center gap-2">
        {!inDialog && <h2 className="text-title-large">Заявка № {request.number}</h2>}
        <RequestStatus status={request.status} />
      </div>
      <p className="text-body-medium text-on-surface-variant">{siteName(request.site)}</p>
      <p className="text-body-small text-on-surface-variant">{request.author} · {formatDate(request.date)}</p>
    </div>
    <MaterialSummary request={request} scenario={scenario} />
    {isDraft && <div className="flex flex-wrap gap-2">
      <Button variant="filled" onClick={() => open('submit')}>Подать заявку</Button>
      <Button variant="outlined" onClick={() => open('edit')}>Изменить строки</Button>
      <Button variant="text" onClick={() => open('delete')}>Удалить черновик</Button>
    </div>}
    {isSubmitted && <div className="flex flex-wrap gap-2">
      <Button variant="outlined" onClick={() => open('close')}>Закрыть заявку</Button>
      {request.availableToAuthor && <Button variant="text" onClick={() => open('return')}>Вернуть в черновик</Button>}
    </div>}
    {action && <Modal open title={actionNames[action]} wide={action === 'edit'}
      fullScreen={narrow && action === 'edit'} question={action === 'delete' || action === 'close' || action === 'submit'}
      onOpenChange={open => !open && setAction(undefined)}
      footer={<ModalButtons><ModalExit>Отмена</ModalExit>
        <ModalPrimary onClick={save} disabled={!!conflict || (action === 'return' && !reason.trim())}>
          {action === 'edit' ? 'Сохранить строки' : actionNames[action]}
        </ModalPrimary></ModalButtons>}>
      {conflict === 'linked' ? <Banner tone="error">У черновика есть связанные счета. Сначала снимите ссылки в счетах.</Banner>
        : conflict === 'stale' ? <Banner tone="error" actions={[{ label: 'Перечитать', onClick: () => {
        setRows(request.rows); setConflict(undefined); setReloaded(true);
      } }]}>Заявку тем временем изменили. Перечитайте её и повторите действие.</Banner>
        : action === 'return' ? <TextField label="Причина возврата" value={reason} onChange={event => setReason(event.target.value)} />
        : action === 'edit' ? <div className="grid gap-3">{rows.map(row => <TextField key={row.id}
          label={`${row.name}, ${row.unit}`} type="number" min="0" value={row.quantity}
          onChange={event => setRows(previous => previous.map(item => item.id === row.id
            ? { ...item, quantity: Math.max(0, Number(event.target.value)) } : item))} />)}</div>
        : <p className="text-body-medium">{action === 'submit' ? 'После подачи правка строк будет закрыта.'
          : action === 'close' ? 'Связанные счета сохранятся. Новые счета к закрытой заявке не привязываются.'
          : `Удалить заявку № ${request.number}?`}</p>}
    </Modal>}
  </article>;
}

export function RequestBrowser({ variant, scenario, narrow, requests, selected, openedAfterCreate, onSelect, onSave, onDelete }: {
  variant: Variant; scenario: Scenario; narrow: boolean; requests: Request[]; selected: number;
  openedAfterCreate: boolean;
  onSelect: (id: number) => void; onSave: (row: Request) => void; onDelete: (id: number) => void;
}) {
  const created = openedAfterCreate ? requests.find(row => row.number === selected) : undefined;
  const [site, setSite] = useState<Site | 'all'>(variant === 'b' ? created?.site ?? 'park' : 'all');
  const [status, setStatus] = useState<Status>(created?.status ?? 'submitted');
  const [open, setOpen] = useState<number | undefined>(created?.number);
  const view = useListDetailView(variant === 'a' ? String(created?.number ?? '') : null);
  const visible = requests.filter(row => (site === 'all' || row.site === site) && (variant !== 'c' || row.status === status));
  const active = visible.find(row => row.number === selected) ?? visible[0];
  const picked = requests.find(row => row.number === open);
  const detail = (row: Request) => <RequestDetail key={row.number} request={row} scenario={scenario}
    narrow={narrow} inDialog={variant === 'b'} onSave={onSave} onDelete={id => { onDelete(id); setOpen(undefined); }} />;

  const columns: DataGridColumn<Request>[] = [
    { key: 'number', label: 'Заявка', rowHeader: true, wrap: true, cell: row => <>
      <strong className="font-medium">№ {row.number}</strong>
      <span className="block text-body-small text-on-surface-variant">{siteName(row.site)}</span>
    </> },
    ...(variant === 'a' ? [] : [{ key: 'date', label: 'Создана', cell: (row: Request) => formatDate(row.date) }]),
    { key: 'status', label: 'Статус', cell: row => <RequestStatus status={row.status} /> },
  ];
  function choose(row: Request) {
    onSelect(row.number);
    if (variant === 'a') view.showDetail();
    else setOpen(previous => variant === 'c' && previous === row.number ? undefined : row.number);
  }

  const selector = <Select label="Стройка" density="dense" value={site} onValueChange={value => {
    const next = sites.find(item => item.value === value)?.value ?? 'all';
    setSite(next); setOpen(undefined); view.showList();
  }}>
    <SelectItem value="all">Все стройки</SelectItem>
    {sites.map(item => <SelectItem value={item.value} key={item.value}>{item.label}</SelectItem>)}
  </Select>;

  const registry = <DataGrid columns={columns} rows={visible} rowKey={row => String(row.number)}
    rowOpen={row => variant === 'a' ? active?.number === row.number : open === row.number}
    onRowOpen={choose} density="compact" framed={variant !== 'a'} />;

  if (variant === 'a') return <Card flush className="min-w-0">
    <ListDetailShell title="Реестр заявок" view={view} bare nav={<>
      <div className="p-3">{selector}</div>{registry}
    </>} detail={active ? detail(active) : <p className="p-4">Заявок нет.</p>} />
  </Card>;

  if (variant === 'b') return <section className="grid min-w-0 gap-4" aria-label="Заявки по стройкам">
    <div className="grid gap-2 @xl/p6:grid-cols-2">
      {sites.map(item => <Card key={item.value} title={item.label} variant={site === item.value ? 'filled' : 'outlined'}
        actions={<Button variant={site === item.value ? 'tonal' : 'text'} onClick={() => { setSite(item.value); setOpen(undefined); }}>
          {site === item.value ? 'Выбранная стройка' : 'Показать заявки'}
        </Button>}>
        <dl className="grid grid-cols-3 gap-2">{statuses.map(value => <div key={value.value}>
          <dt className="text-body-small text-on-surface-variant">{value.label}</dt>
          <dd className="text-title-medium tabular-nums">{requests.filter(row => row.site === item.value && row.status === value.value).length}</dd>
        </div>)}</dl>
      </Card>)}
    </div>
    {registry}
    {picked && <Modal open title={`Заявка № ${picked.number}`} wide fullScreen={narrow} nothingToLose
      onOpenChange={shown => !shown && setOpen(undefined)}>{detail(picked)}</Modal>}
  </section>;

  return <section className="grid min-w-0 gap-3" aria-label="Очередь заявок">
    <div className="max-w-sm">{selector}</div>
    <Tabs id="p6-queue" aria-label="Статус заявки" value={status} items={statuses.map(item => ({
      value: item.value, label: ({ draft: 'Черновики', submitted: 'Поданные', closed: 'Закрытые' })[item.value],
    }))} onChange={next => { setStatus(next); setOpen(undefined); }} />
    <TabPanel id="p6-queue" value={status} className="grid min-w-0 gap-3">
      <div className="hidden @lg/p6:block">{registry}</div>
      <div className="grid @lg/p6:hidden">
        {visible.map(row => <ListItem key={row.number} headline={`Заявка № ${row.number}`}
          supporting={siteName(row.site)} selected={open === row.number} onClick={() => choose(row)} />)}
        {visible.length === 0 && <p className="p-4 text-body-medium">В этом статусе заявок нет.</p>}
      </div>
      {picked && <Card flush>{detail(picked)}</Card>}
    </TabPanel>
  </section>;
}
