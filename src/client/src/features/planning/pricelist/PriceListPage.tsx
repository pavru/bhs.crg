import { useState } from 'react';
import { useCan } from '@/shared/api/access';
import { usePriceList, type PriceListEntry, type PriceListWork } from '@/shared/api/planningPriceList';
import { companyToday } from '@/shared/format/companyDay';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { moneyText } from '../offers/offerFormat';
import { PriceEntryDialog, type PriceEntryAction } from './PriceEntryDialog';
import { PriceListOfferAction } from './PriceListOfferAction';
import { CATALOG_ACCESS, hasWorkUnit, pricedChoice, workLabel } from './workChoices';

const columns: DataGridColumn<PriceListWork>[] = [
  { key: 'work', label: 'Вид работы', rowHeader: true, wrap: true, cell: row => <>
    {workLabel(pricedChoice(row))}
    {row.archived && <span className="block text-body-small text-on-surface-variant">В архиве</span>}
  </> },
  { key: 'unit', label: 'Единица', cell: row => row.effective?.unit ?? row.unit ?? '—' },
  { key: 'price', label: 'Действующая цена', align: 'right', cell: row => moneyText(row.effective?.price) },
  { key: 'date', label: 'Действует с', cell: row => row.effective ? formatDate(row.effective.validFrom) : '—' },
];

export function PriceListPage() {
  const can = useCan();
  const canRead = can.permission('plan.offer.read');
  const canEdit = can.permission('plan.pricelist.edit');
  const canCatalog = can.permission('core.catalog.read');
  const [date, setDate] = useState<string>();
  const [incomplete, setIncomplete] = useState(false);
  const [workId, setWorkId] = useState<string | null>(null);
  const [action, setAction] = useState<PriceEntryAction | null>(null);
  const query = usePriceList(date, canRead && date !== '');
  const data = canRead && date !== '' && !incomplete && !query.isError ? query.data : undefined;
  const work = data?.items.find(row => row.workTypeId === workId);
  const shownDate = date ?? data?.date ?? companyToday();

  return <div className="h-full overflow-auto">
    <section className="mx-auto grid min-w-0 max-w-7xl grid-cols-1 gap-4 px-4 py-5 sm:px-6">
      <PageHeader title="Прайс-лист" actions={<PriceListOfferAction />} />
      <div className="flex flex-wrap items-end gap-3">
        <div className="max-w-56">
          <DateField label="Цены на дату" value={shownDate} onChange={value => {
            setDate(value);
            setWorkId(null);
          }} onIncomplete={message => setIncomplete(Boolean(message))} />
        </div>
        {canRead && canEdit && canCatalog && <Button variant="tonal" disabled={incomplete || date === ''}
          onClick={() => setAction({ kind: 'new' })}>Добавить вид в прайс-лист…</Button>}
      </div>
      {canRead && canEdit && !canCatalog && <Banner tone="info">{CATALOG_ACCESS}</Banner>}
      {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
        {apiError(query.error, 'Прайс-лист не прочитался.')}
      </Banner>}
      {canRead && date !== '' && !incomplete && query.isPending && <p role="status" className="text-body-medium">Читаем прайс-лист…</p>}
      {data && (data.items.length > 0 ? <DataGrid rows={data.items} columns={columns} rowKey={row => row.workTypeId}
        onRowOpen={row => setWorkId(row.workTypeId)} rowOpen={row => row.workTypeId === workId} pinned={1} />
        : <p className="text-body-medium">В прайс-листе пока нет записей.</p>)}
      {work && !action && <PriceHistory key={work.workTypeId} work={work} canEdit={canEdit}
        onClose={() => setWorkId(null)} onAction={setAction} />}
      {action && canRead && <PriceEntryDialog action={action} initialDate={shownDate || data?.date || companyToday()}
        onReload={() => { void query.refetch(); }} onClose={() => setAction(null)} />}
    </section>
  </div>;
}

function PriceHistory({ work, canEdit, onClose, onAction }: {
  work: PriceListWork; canEdit: boolean; onClose: () => void; onAction: (action: PriceEntryAction) => void;
}) {
  const choice = pricedChoice(work);
  const history: DataGridColumn<PriceListEntry>[] = [
    { key: 'date', label: 'С даты', rowHeader: true, cell: row => formatDate(row.validFrom) },
    { key: 'unit', label: 'Единица', cell: row => row.unit },
    { key: 'price', label: 'Цена', align: 'right', cell: row => moneyText(row.price) },
    { key: 'state', label: 'Состояние', cell: row => row.id === work.effective?.id ? <StatusText tone="success">Действует</StatusText> : '—' },
  ];
  if (canEdit) history.push({ key: 'actions', label: 'Действия', interactive: true, cell: row =>
    <div className="flex flex-wrap gap-1">
      <Button variant="text" onClick={() => onAction({ kind: 'edit', work: choice, entry: row })}
        aria-label={`Изменить цену с ${formatDate(row.validFrom)}`}>Изменить…</Button>
      <Button variant="text" onClick={() => onAction({ kind: 'remove', work: choice, entry: row })}
        aria-label={`Снять цену с ${formatDate(row.validFrom)}`}>Снять…</Button>
    </div>,
  });
  return <Modal open wide nothingToLose title="История цен" onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalExit>Закрыть</ModalExit>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-title-medium">{workLabel(choice)}</h3>
        {canEdit && !work.archived && hasWorkUnit(choice) && <Button variant="tonal"
          onClick={() => onAction({ kind: 'new', work: choice, history: work.history })}>Новая цена…</Button>}
      </div>
      <DataGrid rows={work.history} columns={history} rowKey={row => row.id} density="compact" />
    </div>
  </Modal>;
}
