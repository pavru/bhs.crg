import { formatMoney, formatNumber } from '@/shared/format/format';
import type { MarkupLevel, OfferListItem, OfferStatus, OfferVatMode } from '@/shared/api/planningOfferTypes';

export const OFFERS_PATH = '/offers';
export const STATUS_LABELS: Record<OfferStatus, string> = {
  draft: 'Черновик', sent: 'Отправлено', accepted: 'Принято', rejected: 'Отклонено', 'no-answer': 'Без ответа',
};
export const VAT_LABELS: Record<OfferVatMode, string> = { none: 'Без НДС', included: 'НДС в том числе', 'on-top': 'НДС сверху' };
export const MARKUP_LABELS: Record<MarkupLevel, string> = { offer: 'КП', section: 'Глава', line: 'Строка' };

export function sourceLabel(offer: Pick<OfferListItem, 'source' | 'revision'>) {
  if (offer.source === 'pricelist') return 'Прайс-лист';
  return offer.revision ? `Смета · ред. ${offer.revision.number}` : 'Смета';
}

export const offerUrl = (id: string, version?: number) => `${OFFERS_PATH}/${id}${version === undefined ? '' : `?version=${version}`}`;
export const moneyText = (amount: number | null | undefined) => amount == null ? '—' : formatMoney(amount);
export const markupText = (value: number | null) => value === null ? '—' : `${formatNumber(value)} %`;
