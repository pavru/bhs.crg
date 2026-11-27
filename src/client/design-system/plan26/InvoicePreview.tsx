import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { ListItem } from '@/shared/ui/ListItem';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { RequestStatus } from './RequestBrowser';
import { siteName, type Request, type Scenario, type Variant } from './sample';

export function InvoicePreview({ variant, scenario, narrow, requests, linked, onLink }: {
  variant: Variant; scenario: Scenario; narrow: boolean; requests: Request[]; linked?: number;
  onLink: (id: number | undefined) => void;
}) {
  const [open, setOpen] = useState(false);
  const [selected, setSelected] = useState(String(linked ?? 'none'));
  const isSplit = scenario === 'split';
  const displayed = requests.find(item => item.number === linked);
  const candidates = requests.filter(item => item.site === 'park' && item.status === 'submitted');
  const columns: DataGridColumn<Request>[] = [
    { key: 'request', label: 'Заявка', rowHeader: true, cell: row => `№ ${row.number}` },
    { key: 'author', label: 'Автор', wrap: true, cell: row => row.author },
    { key: 'lines', label: 'Строк', align: 'right', cell: row => row.rows.length },
  ];
  function save() {
    onLink(selected === 'none' ? undefined : Number(selected));
    setOpen(false);
  }
  if (scenario === 'manager') return <Banner tone="info">Счёт скрыт. Нужно право чтения счетов.</Banner>;

  return <Card className="min-w-0">
    <div className="grid min-w-0 gap-4">
      <PageHeader title="Счёт СЧ-387" subtitle="ООО «Электроснаб» · 27.11.2026" />
      <dl className="grid gap-2 border-y border-outline-variant py-3 text-body-medium @lg/p6:grid-cols-2">
        <div><dt className="text-body-small text-on-surface-variant">Разноска</dt><dd>
          {isSplit ? '60 м на логистический парк · 40 м на школу' : 'Весь счёт на стройку'}</dd></div>
        <div><dt className="text-body-small text-on-surface-variant">Стройка</dt><dd>
          {isSplit ? 'Две стройки' : siteName('park')}</dd></div>
      </dl>
      <p className="text-body-medium">Кабель ВВГнг(А)-LS 3×2,5 · 100 м</p>
      {isSplit ? <Banner tone="warning">Чтобы связать счёт с заявкой, полностью разнесите его на одну стройку.
        В этом счёте есть доли двух строек.</Banner> : variant === 'a' ? <div className="@container/selector grid gap-2">
        <div className="grid items-start gap-2 @lg/selector:grid-cols-[minmax(0,1fr)_auto]">
          <Select label="Заявка на закупку" value={selected} onValueChange={setSelected}>
            <SelectItem value="none">Без заявки</SelectItem>
            {candidates.map(item => <SelectItem key={item.number} value={String(item.number)}>№ {item.number} · {item.author}</SelectItem>)}
            {displayed && !candidates.some(item => item.number === displayed.number) &&
              <SelectItem value={String(displayed.number)} disabled>№ {displayed.number}</SelectItem>}
          </Select>
          <Button variant="filled" className="@lg/selector:self-center" onClick={save} disabled={selected === String(linked ?? 'none')}>Сохранить связь</Button>
        </div>
      </div> : <Card title="Заявка на закупку" variant={variant === 'b' ? 'filled' : 'outlined'}
        actions={<><Button variant="tonal" onClick={() => { setSelected(String(linked ?? 'none')); setOpen(true); }}>
          {displayed ? 'Изменить связь' : variant === 'b' ? 'Выбрать заявку' : 'Открыть реестр заявок'}
        </Button>{displayed && <Button variant="text" onClick={() => { onLink(undefined); setSelected('none'); }}>Снять связь</Button>}</>}>
        <p className="text-body-medium">{displayed ? `№ ${displayed.number} · ${displayed.author}` : 'Заявка не указана'}</p>
        {displayed && <RequestStatus status={displayed.status} />}
      </Card>}
      {!isSplit && <p className="text-body-small text-on-surface-variant">Для новой связи доступны поданные заявки этой стройки.</p>}
      {variant === 'a' && linked !== undefined && <p role="status" className="text-body-small">Связано с заявкой № {linked}.</p>}
    </div>
    {open && <Modal open title="Заявки этой стройки" wide={variant === 'c'} fullScreen={narrow}
      onOpenChange={shown => !shown && setOpen(false)}
      footer={<ModalButtons><ModalExit>Отмена</ModalExit>
        <ModalPrimary disabled={selected === String(linked ?? 'none')} onClick={save}>Сохранить связь</ModalPrimary>
      </ModalButtons>}>
      <div className="grid min-w-0 gap-2">
        <p className="text-body-medium text-on-surface-variant">{siteName('park')}</p>
        {variant === 'c' && !narrow ? <DataGrid columns={columns} rows={candidates} rowKey={row => String(row.number)}
          onRowOpen={row => setSelected(String(row.number))} rowOpen={row => selected === String(row.number)} />
          : candidates.map(row => <ListItem key={row.number} headline={`Заявка № ${row.number}`}
            supporting={variant === 'c' ? `${row.author} · Строк: ${row.rows.length}` : row.author}
            selected={selected === String(row.number)} onClick={() => setSelected(String(row.number))} />)}
        {candidates.length === 0 && <p className="text-body-medium">На стройке нет поданных заявок.</p>}
      </div>
    </Modal>}
  </Card>;
}
