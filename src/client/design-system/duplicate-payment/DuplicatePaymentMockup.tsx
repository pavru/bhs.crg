import { useState } from 'react';
import { InvoiceStatusIndicator } from '@/features/costs/InvoiceStatusIndicator';
import type { InvoiceStatus, StatusItem } from '@/features/costs/invoiceStatus';
import { Button } from '@/shared/ui/Button';
import { ListItem, LIST_CONTAINER } from '@/shared/ui/ListItem';
import { Modal } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { ruPlural } from '@/shared/utils/pluralize';
import { duplicatePaymentMockup } from '../live-mockups';
import { DuplicateRow, FileLink, InvoiceFacts, PairReview, PaymentStatus, type Variant } from './PairReview';
import { date, invoices, number, pairKey, scenarios, supplier, type Invoice, type InvoiceId, type Scenario } from './fixtures';

const params = new URLSearchParams(location.search);
const page = duplicatePaymentMockup.pages.find(item => item.path === params.get('page')) ?? duplicatePaymentMockup.pages[0];
const variant = page.path as Variant;

export function DuplicatePaymentMockup() {
  const [scenario, setScenario] = useState<Scenario>(scenarios.find(item => item.id === params.get('scenario')) ?? scenarios[0]);
  return (
    <div className="mx-auto grid max-w-5xl gap-4 p-4 md:p-6">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-title-large">{page.title}</h1>
        <div className="w-full max-w-72">
          <Select label="Сценарий" density="dense" value={scenario.id} onValueChange={id => {
            const next = scenarios.find(item => item.id === id);
            if (next) setScenario(next);
          }}>
            {scenarios.map(item => <SelectItem key={item.id} value={item.id}>{item.label}</SelectItem>)}
          </Select>
        </div>
      </header>
      <InvoiceExample key={scenario.id} scenario={scenario} />
    </div>
  );
}

function InvoiceExample({ scenario }: { scenario: Scenario }) {
  const [active, setActive] = useState<InvoiceId>('А');
  const [layer, setLayer] = useState<'status' | 'review' | 'payment' | null>(null);
  const [target, setTarget] = useState<InvoiceId>('Б');
  const [confirmed, setConfirmed] = useState<string[]>(scenario.id === 'confirmed' ? [pairKey('А', 'Б')] : []);
  const [notice, setNotice] = useState('');
  const available = scenario.multiple ? invoices : invoices.slice(0, 2);
  const current = available.find(invoice => invoice.id === active) ?? available[0];
  const isPaid = (id: InvoiceId) => scenario.id === 'paid' && id === 'Б';
  const paid = isPaid(active);
  const canPay = scenario.id !== 'readonly';
  const blockers = paid ? [] : available.filter(invoice => invoice.id !== active && !confirmed.includes(pairKey(active, invoice.id)));
  const canOpenPayment = canPay && !paid && blockers.length === 0;
  const openInvoice = (id: InvoiceId) => { setActive(id); setLayer(null); };
  const review = (id: InvoiceId) => { setTarget(id); setLayer('review'); };
  const openStatus = () => {
    if (variant === 'sheet' && blockers.length) review(blockers[0].id);
    else setLayer('status');
  };
  const confirm = (id: InvoiceId) => {
    setConfirmed(previous => [...previous, pairKey(active, id)]);
    setNotice(`Пара ${pairKey(active, id)} подтверждена.`);
    if (variant !== 'sheet' || blockers.length === 1) setLayer('status');
  };
  const items: StatusItem[] = blockers.map(invoice => ({
    key: invoice.id, level: 'gate', text: `Счёт ${invoice.id} · № ${number}`, blocks: ['pay'], actions: [],
  }));
  const status: InvoiceStatus = {
    items, count: items.length, running: false, mood: items.length ? 'gate' : 'none', tone: items.length ? 'warning' : 'quiet',
    phrase: items.length ? `${items.length} ${ruPlural(items.length, 'замечание', 'замечания', 'замечаний')}` : 'Состояние счёта',
  };
  const paymentButton = (primary: boolean) => canOpenPayment && <Button variant={primary ? 'filled' : 'text'}
    onClick={() => setLayer('payment')}>Внести оплату…</Button>;

  return (
    <main className="grid gap-4 md:grid-cols-[14rem_minmax(0,1fr)]">
      <nav className="rounded-lg border border-outline-variant bg-surface py-3" aria-label="Счета">
        <h2 className="px-4 pb-2 text-title-medium">Счета</h2>
        <ul className={LIST_CONTAINER}>{available.map(invoice => <li key={invoice.id}>
          <ListItem onClick={() => openInvoice(invoice.id)} selected={active === invoice.id}
            headline={`Счёт ${invoice.id} · № ${number}`} supporting={<span>{invoice.site} · {invoice.amount}</span>} />
        </li>)}</ul>
      </nav>
      <article aria-labelledby="active-invoice" className="min-w-0 rounded-lg border border-outline-variant bg-surface">
        <header className="grid gap-3 border-b border-outline-variant p-4 md:p-6">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="active-invoice" className="text-headline-small">Счёт {active} · № {number}</h2>
            <InvoiceStatusIndicator status={status} open={layer === 'status' || layer === 'review'} onOpen={openStatus} />
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <StatusText tone="primary">Разобран</StatusText><PaymentStatus paid={paid} />
          </div>
          <p className="text-body-medium text-on-surface-variant">{supplier} · {date} · {current.amount}</p>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <FileLink invoice={current} />{paymentButton(true)}
          </div>
        </header>
        <div className="grid gap-4 p-4 md:p-6">
          <section className="invoice-facts rounded-md bg-surface-container-low p-4">
            <h3 className="mb-3 text-title-medium">Реквизиты</h3><InvoiceFacts invoice={current} />
          </section>
          <section className="rounded-md bg-surface-container-low p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-title-medium">Сроки и оплата</h3>{paymentButton(false)}
            </div>
            <p className="mt-2 text-body-medium text-on-surface-variant">{paid ? 'Оплачен 20.11.2026' : `К оплате · ${current.amount}`}</p>
          </section>
          <p role="status" className={notice ? 'text-body-medium text-on-surface-variant' : 'sr-only'}>{notice}</p>
        </div>
      </article>
      <SideSheet open={layer === 'status'} variant="modal" title="Состояние счёта" onClose={() => setLayer(null)}>
        {notice && <p className="mb-3 text-body-medium text-on-surface-variant" role="status">{notice}</p>}
        {blockers.length ? <section className="grid gap-2">
          <h3 className="text-title-small">Мешает оплате</h3>
          <p className="text-body-medium text-on-surface-variant">Совпали поставщик, номер и дата счёта.</p>
          <ul className={LIST_CONTAINER} aria-label="Блокирующие счета">
            {blockers.map(invoice => <li key={invoice.id}>
              <DuplicateRow current={current} invoice={invoice} paid={isPaid(invoice.id)} onSelect={() => openInvoice(invoice.id)}
                action={variant === 'compare' || canPay && variant === 'pair'
                  ? <Button variant="text" onClick={() => review(invoice.id)}>{variant === 'compare' ? 'Сверить…' : 'Это не дубль…'}</Button>
                  : undefined} />
            </li>)}
          </ul>
        </section> : <p className="text-body-medium">Нет замечаний к счёту.</p>}
      </SideSheet>
      {layer === 'review' && blockers.length > 0 && <PairReview variant={variant} key={active}
        current={current} blockers={blockers} initialOther={target} canPay={canPay} isPaid={isPaid} notice={notice}
        onOpenInvoice={openInvoice} onConfirm={confirm} onClose={() => setLayer(variant === 'sheet' ? null : 'status')} />}
      <Modal open={layer === 'payment'} onOpenChange={open => { if (!open) setLayer(null); }} title="Внести оплату" nothingToLose>
        <p>Счёт {active} · № {number} · {current.amount}</p>
      </Modal>
    </main>
  );
}

export function FileExample({ invoice }: { invoice: Invoice }) {
  return (
    <main className="invoice-facts mx-auto mt-6 grid max-w-xl gap-4 rounded-lg border border-outline-variant bg-surface p-6">
      <p className="text-label-medium text-on-surface-variant">Образец файла счёта</p>
      <h1 className="text-headline-small">Счёт № {number} от {date}</h1>
      <InvoiceFacts invoice={invoice} />
    </main>
  );
}
