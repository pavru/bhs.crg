import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import {
  purchaseRefusal, REQUEST_EDIT, REQUEST_READ, usePurchaseConstructions, usePurchaseList, usePurchaseRequest,
  type PurchaseListRow,
} from '@/shared/api/purchaseRequests';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { Add } from '@/shared/ui/icons';
import { ListDetailShell } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { Select, SelectItem } from '@/shared/ui/Select';
import { PurchaseDetail, PurchaseStatusText } from './PurchaseDetail';
import { PurchaseWizard } from './PurchaseWizard';

export function PurchaseRequestsPage() {
  const can = useCan();
  const readable = can.module('costs') && can.permission(REQUEST_READ);
  const editable = readable && can.permission(REQUEST_EDIT);
  const planInstalled = can.access.modules.some(module => module.code === 'plan');
  const [params, setParams] = useSearchParams();
  const focused = params.get('request') ?? undefined;
  const site = params.get('construction') ?? '';
  const layout = useListDetailView(focused);
  const [wizard, setWizard] = useState(false);
  const constructions = usePurchaseConstructions(readable);
  const list = usePurchaseList(site || undefined, readable);
  const items = list.data?.pages.flatMap(page => page.items) ?? [];
  const selected = focused ?? items[0]?.id;
  const detail = usePurchaseRequest(selected, readable);

  const sites = constructions.data ?? [];
  const name = (id: string) => sites.find(row => row.id === id)?.name ?? 'Стройка не найдена';

  function pick(id: string, constructionId?: string) {
    setParams(previous => {
      const next = new URLSearchParams(previous);
      next.set('request', id);
      if (site && constructionId && constructionId !== site) next.set('construction', constructionId);
      return next;
    });
    layout.showDetail();
  }
  function filter(id: string) {
    setParams(previous => {
      const next = new URLSearchParams(previous);
      if (id) next.set('construction', id); else next.delete('construction');
      next.delete('request');
      return next;
    });
    layout.showList();
  }
  const columns: DataGridColumn<PurchaseListRow>[] = [
    { key: 'number', label: 'Заявка', rowHeader: true, wrap: true, cell: row => <>
      <strong className="font-medium">№ {row.number}</strong>
      <span className="block text-body-small text-on-surface-variant">{name(row.constructionId)}</span>
    </> },
    { key: 'status', label: 'Статус', cell: row => <PurchaseStatusText status={row.status} /> },
  ];
  return <>
    <ListDetailShell title="Заявки на закупку" view={layout}
      actions={editable && <Button variant="filled" icon={<Icon icon={Add} size={18} />}
        disabled={!planInstalled || !sites.length} onClick={() => setWizard(true)}>Новая из сметы</Button>}
      nav={<>
        {editable && !planInstalled && <Banner tone="info">Для создания заявки нужен модуль «Планирование».</Banner>}
        {constructions.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void constructions.refetch() }]}>
          {purchaseRefusal(constructions.error).message}
        </Banner>}
        <div className="p-3">
          <Select label="Стройка" density="dense" value={site || 'all'} onValueChange={id => filter(id === 'all' ? '' : id)}>
            <SelectItem value="all">Все стройки</SelectItem>
            {sites.map(row => <SelectItem key={row.id} value={row.id}>{row.name}</SelectItem>)}
          </Select>
        </div>
        <div className="min-h-0 overflow-y-auto">
          {list.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void list.refetch() }]}>
            {purchaseRefusal(list.error).message}
          </Banner> : list.isPending ? <p className="p-4" role="status">Читаем заявки…</p>
            : <DataGrid rows={items} columns={columns} rowKey={row => row.id} onRowOpen={row => pick(row.id)}
              rowOpen={row => row.id === selected} framed={false} />}
          {list.hasNextPage && <div className="p-2">
            <Button variant="text" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Показать ещё</Button>
          </div>}
        </div>
      </>}
      detail={!selected ? <p className="p-4 text-body-medium text-on-surface-variant">Выберите заявку.</p>
        : detail.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void detail.refetch() }]}>
          {purchaseRefusal(detail.error).message}
        </Banner> : detail.data ? <PurchaseDetail key={detail.data.id} request={detail.data} site={name(detail.data.constructionId)}
          editable={editable} invoices={can.permission('costs.invoice.read')}
          issued={can.permission('costs.waybill.read') || can.permission('costs.materials.read')}
          onDeleted={() => filter(site)} onReload={() => void detail.refetch()} />
        : <p className="p-4" role="status">Читаем заявку…</p>}
    />
    {editable && wizard && <PurchaseWizard constructions={sites} initialSite={site || sites[0]?.id || ''}
      onClose={() => setWizard(false)} onCreated={request => {
        setWizard(false);
        pick(request.id, request.constructionId);
      }} />}
  </>;
}
