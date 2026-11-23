import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { estimateLines, priceLines, statusLabels, type Offer, type Scene } from './sample';
import type { OfferAction } from './OfferCard';

const changes = [
  { id: 'add', kind: 'Добавлена', name: 'Монтаж коробки ответвительной', before: '—', after: '4 шт. · 5 600 ₽' },
  { id: 'change', kind: 'Изменена', name: 'Монтаж кабельного лотка', before: '150 м · 63 000 ₽', after: '180 м · 75 600 ₽' },
  { id: 'remove', kind: 'Снята', name: 'Подключение щита освещения', before: '1 шт. · 12 850 ₽', after: '—' },
  { id: 'replace', kind: 'Неоднозначно', name: 'Прокладка кабеля ВВГнг · повторяющаяся расценка', before: '200 м · 60 000 ₽', after: 'Две строки-кандидата; своя наценка не перенесётся' },
];

function RebasePreview({ offer, refreshed }: { offer: Offer; refreshed: boolean }) {
  const target = (offer.latest ?? 4) + Number(refreshed);
  const rows = refreshed ? [...changes, { id: 'new', kind: 'Добавлена', name: 'Маркировка кабельных линий', before: '—', after: '12 шт. · 2 040 ₽' }] : changes;
  return <div className="grid grid-cols-1 gap-3">
    {refreshed && <Banner tone="warning">Смета изменилась после предпросмотра. Сверьте новую разницу и подтвердите ещё раз.</Banner>}
    <p className="text-body-medium">Редакция {offer.revision} → редакция {target}. {offer.status === 'draft' ? 'Обновится текущий черновик.' : 'Появится новый черновик. Отправленная версия останется в истории.'}</p>
    <DataGrid density="compact" rows={rows} rowKey={row => row.id} columns={[
      { key: 'kind', label: 'Изменение', cell: row => <StatusText tone={row.kind === 'Неоднозначно' ? 'warning' : 'neutral'}>{row.kind}</StatusText> },
      { key: 'line', label: 'Позиция', rowHeader: true, wrap: true, cell: row => row.name },
      { key: 'before', label: 'Было', wrap: true, cell: row => row.before },
      { key: 'after', label: 'Станет', wrap: true, cell: row => row.after },
    ]} />
    <p className="text-body-medium">Наценка 20 % остаётся у главы «Электроснабжение»: глава однозначно сопоставлена.</p>
  </div>;
}

function PrintPreview({ offer }: { offer: Offer }) {
  return <div className="grid grid-cols-1 gap-3">
    <p className="text-body-medium text-on-surface-variant">Печать по шаблону по умолчанию.</p>
    <section className="grid gap-3 rounded-md border border-outline-variant bg-surface p-4" aria-label="Печатная форма">
      <div><h3 className="text-title-large">Коммерческое предложение {offer.number}</h3>
        <p className="text-body-medium">Версия {offer.version} · {offer.sentOn ?? '23.11.2026'} · {offer.customer}</p></div>
      <DataGrid rows={offer.source === 'estimate' ? estimateLines : priceLines} rowKey={row => row.id} framed={false} density="compact" columns={[
        { key: 'line', label: 'Позиция', rowHeader: true, wrap: true, cell: row => row.name },
        { key: 'volume', label: 'Объём', align: 'right', cell: row => row.quantity },
        { key: 'price', label: 'Цена продажи', align: 'right', cell: row => row.price },
        { key: 'amount', label: 'Сумма', align: 'right', cell: row => row.amount },
      ]} />
      <p className="text-title-medium">{offer.incomplete ? 'Неполный итог' : 'Итого'} · {offer.total}</p>
    </section>
  </div>;
}

const titles: Record<OfferAction, string> = {
  send: 'Отметить КП отправленным', outcome: 'Записать итог КП', next: 'Новая версия КП',
  rebase: 'Обновить КП по смете', print: 'Предпросмотр печати КП',
};

export function OfferDialog({ offer, action, scene, onClose, onApply }: {
  offer: Offer; action: OfferAction; scene: Scene; onClose: () => void; onApply: (offer: Offer) => void;
}) {
  const [date, setDate] = useState('2026-11-23');
  const [outcome, setOutcome] = useState<'accepted' | 'rejected' | 'no-answer'>('accepted');
  const [refreshed, setRefreshed] = useState(false);
  const [error, setError] = useState(false);
  const draft = offer.status === 'draft';
  function apply() {
    if (action === 'rebase' && scene === 'stale-preview' && !refreshed) { setRefreshed(true); return; }
    if (scene === 'forbidden' || scene === 'validation' || scene === 'stale-offer') { setError(true); return; }
    if (action === 'send') onApply({ ...offer, status: 'sent', sentOn: date.split('-').reverse().join('.') });
    if (action === 'outcome') onApply({ ...offer, status: outcome });
    if (action === 'next') onApply({ ...offer, status: 'draft', sentOn: undefined, version: offer.version + 1 });
    if (action === 'rebase') {
      const target = (offer.latest ?? 4) + Number(refreshed);
      onApply({ ...offer, status: 'draft', sentOn: undefined, revision: target, latest: target, version: draft ? offer.version : offer.version + 1 });
    }
  }
  const confirm = action === 'rebase' ? draft ? 'Обновить черновик' : 'Создать черновик по новой редакции' : action === 'next' ? 'Создать версию' : 'Подтвердить';
  return <Modal open title={titles[action]} wide={action === 'rebase' || action === 'print'} nothingToLose
    onOpenChange={open => { if (!open) onClose(); }} footer={action === 'print' ? undefined : <ModalPrimary
      disabled={action === 'send' && !date} onClick={apply}>{confirm}</ModalPrimary>}>
    <div className="grid gap-3">
      {action === 'send' && <><DateField label="Дата отправки" value={date} onChange={setDate} />
        <p className="text-body-medium">После отправки КП можно править только новой версией.</p></>}
      {action === 'outcome' && <Select label="Итог" value={outcome} onValueChange={value => setOutcome(value as typeof outcome)}>
        {(['accepted', 'rejected', 'no-answer'] as const).map(value => <SelectItem key={value} value={value}>{statusLabels[value]}</SelectItem>)}
      </Select>}
      {action === 'next' && <p className="text-body-medium">Версия {offer.version} останется в истории. Версия {offer.version + 1} откроется как черновик.</p>}
      {action === 'rebase' && <RebasePreview offer={offer} refreshed={refreshed} />}
      {action === 'print' && <PrintPreview offer={offer} />}
      {error && <Banner tone="error">{scene === 'forbidden' ? 'Доступ к действию закрыт. Введённые данные остались в форме.'
        : scene === 'stale-offer' ? 'КП тем временем изменили. Перечитайте карточку. Введённые данные остались в форме.'
          : 'Сервер отклонил данные. Проверьте заполненные поля.'}</Banner>}
    </div>
  </Modal>;
}

export function CreateOfferDialog({ revision, scene, seed, onClose, onCreate }: {
  revision?: number; scene: Scene; seed: Offer; onClose: () => void; onCreate: (created: Offer) => void;
}) {
  const [date, setDate] = useState('2026-11-23');
  const [markup, setMarkup] = useState(revision ? '10' : '0');
  const [vat, setVat] = useState(revision ? 'on-top' : 'none');
  const [rate, setRate] = useState('20');
  const [site, setSite] = useState(seed.site);
  const [volumes, setVolumes] = useState(['150', '2']);
  const [error, setError] = useState(false);
  return <Modal open title={revision ? `Сделать КП из редакции ${revision}` : 'КП по прайс-листу'} wide={!revision}
    isDirty={error || undefined} onOpenChange={open => { if (!open) onClose(); }} footer={<ModalPrimary
      disabled={!date || !markup || !revision && volumes.every(volume => !volume)} onClick={() => {
        if (scene === 'forbidden' || scene === 'validation') { setError(true); return; }
        onCreate({ ...seed, id: 'created', number: 'КП-2026-0043', site, revision, latest: revision ? 4 : undefined,
          version: 1, status: 'draft', sentOn: undefined });
      }}>Создать черновик</ModalPrimary>}>
    <div className="grid gap-3">
      {revision ? <p className="text-body-medium">РТК · Корпус 1 · Электромонтажные работы</p> : <Select label="Стройка" value={site} onValueChange={setSite}>
        <SelectItem value="Склад · Блок Б">Склад · Блок Б</SelectItem><SelectItem value="РТК · Корпус 1">РТК · Корпус 1</SelectItem>
      </Select>}
      <div className="grid gap-3 sm:grid-cols-2"><DateField label="Дата КП" value={date} onChange={setDate} />
        <NumberInput label="Наценка КП, %" value={markup} onChange={setMarkup} /></div>
      <Select label="Режим НДС" value={vat} onValueChange={setVat}>
        <SelectItem value="none">Без НДС</SelectItem><SelectItem value="included">В том числе</SelectItem><SelectItem value="on-top">Сверху</SelectItem>
      </Select>
      {vat !== 'none' && <NumberInput label="Ставка НДС, %" value={rate} onChange={setRate} />}
      {!revision && <div className="grid gap-2">
        <h3 className="text-title-medium">Виды работ</h3>
        {priceLines.map((line, index) => <NumberInput key={line.id} label={`${line.name} · объём`} value={volumes[index]} onChange={value => setVolumes(previous => previous.map((old, i) => i === index ? value : old))} />)}
        <p className="text-body-medium text-on-surface-variant">Цены и пометки на дату КП появятся после ответа сервера.</p>
      </div>}
      {error && <Banner tone="error">{scene === 'forbidden' ? 'Нет права создать КП.' : 'Проверьте дату, наценку и объёмы.'} Введённые данные остались в форме.</Banner>}
    </div>
  </Modal>;
}
