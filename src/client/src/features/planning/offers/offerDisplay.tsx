import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import type { OfferMoney, OfferStatus, OfferVat } from '@/shared/api/planningOfferTypes';
import { markupText, moneyText, STATUS_LABELS, VAT_LABELS } from './offerFormat';

const STATUS_TONES: Record<OfferStatus, StatusTone> = {
  draft: 'neutral', sent: 'primary', accepted: 'success', rejected: 'error', 'no-answer': 'neutral',
};

export function OfferStatusText({ status }: { status: OfferStatus }) {
  return <StatusText tone={STATUS_TONES[status]}>{STATUS_LABELS[status]}</StatusText>;
}

export function OfferTotals({ money, vat }: { money: OfferMoney; vat: OfferVat }) {
  return <dl aria-label="Итог КП" className="grid gap-1 rounded-md bg-surface-container-low px-4 py-3 text-body-medium tabular-nums">
    <div className="flex justify-between gap-3"><dt>Сумма строк</dt><dd>{moneyText(money.linesTotal)}</dd></div>
    <div className="flex justify-between gap-3"><dt>{VAT_LABELS[vat.mode]}{vat.rate !== null && ` · ${markupText(vat.rate)}`}</dt>
      {money.vat !== null && <dd>{moneyText(money.vat)}</dd>}</div>
    <div className="flex flex-wrap justify-between gap-x-3 text-title-medium"><dt>{money.incomplete ? 'Неполный итог' : 'Итого'}</dt>
      <dd>{moneyText(money.total)}</dd></div>
  </dl>;
}
