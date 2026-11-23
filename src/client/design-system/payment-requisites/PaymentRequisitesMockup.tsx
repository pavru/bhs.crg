import { useState, type FormEvent } from 'react';
import { InvoiceStatusIndicator } from '@/features/costs/InvoiceStatusIndicator';
import { InvoiceStatusSheet } from '@/features/costs/InvoiceStatusSheet';
import { goTo, readinessAnchor } from '@/features/costs/invoiceReadiness';
import { blocksText, type InvoiceStatus, type StatusBlock, type StatusItem } from '@/features/costs/invoiceStatus';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { ListItem, LIST_CONTAINER } from '@/shared/ui/ListItem';
import { Modal } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { Warning } from '@/shared/ui/icons';
import { ruPlural } from '@/shared/utils/pluralize';
import { paymentRequisitesMockup } from '../live-mockups';

const fields = [
  { key: 'Поставщик', title: 'Поставщик', problem: 'Поставщик удалён' },
  { key: 'Номер', title: 'Номер счёта', problem: 'Не указан номер счёта' },
  { key: 'Дата', title: 'Дата счёта', problem: 'Не указана дата счёта' },
  { key: 'Итого', title: 'Сумма к оплате', problem: 'Не указана сумма к оплате' },
  { key: 'Плательщик', title: 'Плательщик', problem: 'Не указан плательщик' },
] as const;
type Field = typeof fields[number];
type Key = Field['key'];
type Requisites = Record<Key, string>;
const complete: Requisites = {
  Поставщик: 'ООО «Северный кабель»', Номер: 'СК-240', Дата: '2026-11-20', Итого: '48800', Плательщик: 'ООО «Вектор»',
};
const cases = [
  { id: 'all', title: 'Четыре препятствия оплате', parsed: false, lost: true,
    initial: { Поставщик: '', Номер: '', Дата: '', Итого: '', Плательщик: complete.Плательщик } },
  { id: 'payer', title: 'Пустой плательщик', parsed: false, lost: false, initial: { ...complete, Плательщик: '' } },
  { id: 'parsed', title: 'Старый разобранный счёт', parsed: true, lost: false, initial: { ...complete, Номер: '' } },
] as const;
type Scenario = typeof cases[number];

const params = new URLSearchParams(location.search);
const variant = paymentRequisitesMockup.pages.find(page => page.path === params.get('page')) ?? paymentRequisitesMockup.pages[0];

function Example({ scenario }: { scenario: Scenario }) {
  const [saved, setSaved] = useState<Requisites>(scenario.initial);
  const [draft, setDraft] = useState<Requisites>(scenario.initial);
  const [sheet, setSheet] = useState(false);
  const [payment, setPayment] = useState(false);
  const [notice, setNotice] = useState('');
  const dirty = fields.some(({ key }) => draft[key] !== saved[key]);
  const missing = fields.filter(({ key }) => key === 'Итого' ? !Number(saved[key]) : !saved[key].trim());
  const paymentGaps = missing.filter(({ key }) => key !== 'Плательщик');
  const items: StatusItem[] = missing.map(field => {
    const blocks: StatusBlock[] = scenario.parsed ? [] : ['parsed'];
    if (field.key !== 'Плательщик') blocks.push('pay');
    return {
      key: field.key,
      level: 'gate',
      text: field.key === 'Поставщик' && !scenario.lost ? 'Не указан поставщик' : field.problem,
      blocks,
      actions: [{ kind: 'goto', label: `К полю «${field.title}»`, short: 'К полю', place: target(field.key) }],
    };
  });
  const group = variant.path === 'group' && paymentGaps.length > 0;
  const payItems = items.filter(item => item.blocks?.includes('pay'));
  const displayed: StatusItem[] = group ? [
    { key: 'payment-requisites', level: 'gate', text: 'Заполните реквизиты оплаты', actions: [], requisiteRows: payItems },
    ...items.filter(item => !item.blocks?.includes('pay')),
  ] : items;
  const count = displayed.length;
  const status: InvoiceStatus = {
    items: displayed, count, running: false, mood: count ? 'gate' : 'none', tone: count ? 'warning' : 'quiet',
    phrase: count ? `${count} ${ruPlural(count, 'замечание', 'замечания', 'замечаний')}` : 'Состояние счёта',
  };
  const hold = paymentGaps.length ? 'Заполните реквизиты оплаты' : dirty ? 'Сначала сохраните реквизиты' : undefined;
  const pay = (primary: boolean) => <Button variant={primary ? 'filled' : 'text'} disabled={!!hold}
    title={hold} onClick={() => setPayment(true)}>Внести оплату…</Button>;
  const save = (event: FormEvent) => {
    event.preventDefault();
    setSaved(draft);
    setNotice('Реквизиты сохранены');
  };
  const toField = (key: string) => goTo(target(key));
  const form = (
    <Card title="Реквизиты" variant="outlined">
      <form onSubmit={save} className="grid gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          {fields.map(field => (
            <div key={field.key} data-readiness={readinessAnchor(target(field.key))} className="min-w-0">
              <TextField label={field.title} type={field.key === 'Дата' ? 'date' : 'text'} value={draft[field.key]}
                onChange={event => { setDraft({ ...draft, [field.key]: event.target.value }); setNotice(''); }} />
            </div>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button type="submit" variant="outlined" disabled={!dirty}>Сохранить реквизиты</Button>
          <span role="status" className="text-body-small text-on-surface-variant">{notice}</span>
        </div>
      </form>
    </Card>
  );
  return (
    <article className="grid gap-4">
      <Card variant="outlined">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="grid gap-2">
            <h2 className="text-headline-small">Счёт{saved.Номер && ` № ${saved.Номер}`}</h2>
            <div className="flex flex-wrap gap-3">
              <StatusText tone="primary">{scenario.parsed ? 'Разобран' : 'Черновик'}</StatusText>
              <StatusText tone="quiet">Не оплачен</StatusText>
            </div>
          </div>
          {pay(true)}
        </div>
        <div className="mt-2 flex"><InvoiceStatusIndicator status={status} open={sheet} onOpen={() => setSheet(true)} /></div>
      </Card>
      <div className={variant.path === 'inline' && payItems.length ? 'grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]' : 'grid'}>
        {variant.path === 'inline' && payItems.length > 0 && (
          <div className="lg:order-2">
            <Card title="Мешает оплате" variant="outlined" icon={Warning} iconTone="warning">
              <GapList items={payItems} onSelect={toField} />
            </Card>
          </div>
        )}
        {form}
      </div>
      <Card title="Сроки и оплата" variant="outlined"><div className="flex">{pay(false)}</div></Card>
      <InvoiceStatusSheet open={sheet} onClose={() => setSheet(false)} status={status} saving={false}
        savedAt="20.11.2026, 14:20" onAction={action => { if (action.kind === 'goto') goTo(action.place); }} />
      <Modal open={payment} onOpenChange={setPayment} title="Внести оплату" nothingToLose>
        <p className="text-body-medium">Счёт № {saved.Номер} · {saved.Итого} ₽</p>
      </Modal>
    </article>
  );
}

function target(key: string) {
  return { place: 'field', key } as const;
}

function GapList({ items, onSelect }: { items: StatusItem[]; onSelect: (key: string) => void }) {
  return (
    <ul className={LIST_CONTAINER}>
      {items.map(item => (
        <li key={item.key}>
          <ListItem passive headline={<span className="whitespace-normal">{item.text}</span>}
            supporting={item.blocks && <StatusText tone="warning">{blocksText(item.blocks)}</StatusText>}
            action={<Button title={item.actions[0].label} variant="text" onClick={() => onSelect(item.key)}>К полю</Button>} />
        </li>
      ))}
    </ul>
  );
}

export function PaymentRequisitesMockup() {
  const [scenario, setScenario] = useState<Scenario>(cases.find(item => item.id === params.get('scenario')) ?? cases[0]);
  return (
    <main className="mx-auto grid max-w-5xl gap-4 p-4 md:p-6">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-title-large">{variant.title}</h1>
        <div className="w-full max-w-72">
          <Select label="Сценарий" density="dense" value={scenario.id}
            onValueChange={id => setScenario(cases.find(item => item.id === id) ?? cases[0])}>
            {cases.map(item => <SelectItem key={item.id} value={item.id}>{item.title}</SelectItem>)}
          </Select>
        </div>
      </header>
      <Example key={scenario.id} scenario={scenario} />
    </main>
  );
}
