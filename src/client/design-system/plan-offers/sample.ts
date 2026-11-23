export type Variant = 'page' | 'split' | 'sections';
export type Persona = 'manager' | 'cost' | 'reader' | 'editor' | 'estimate' | 'price-editor';
export type Scene = 'normal' | 'stale-offer' | 'stale-preview' | 'validation' | 'forbidden';
export type OfferStatus = 'draft' | 'sent' | 'accepted' | 'rejected' | 'no-answer';

export const personas: Record<Persona, string> = {
  manager: 'Менеджер проекта',
  cost: 'Менеджер с себестоимостью',
  reader: 'Только чтение КП',
  editor: 'Правка КП без отправки',
  estimate: 'Сметчик без доступа к КП',
  'price-editor': 'Чтение КП и правка прайс-листа',
};

export const scenes: Record<Scene, string> = {
  normal: 'Обычная работа',
  'stale-offer': '409 · КП или цена изменились',
  'stale-preview': '409 · Смета изменилась после предпросмотра',
  validation: '400 · Не принято значение',
  forbidden: '403 · Право отозвано',
};

export const variantNotes: Record<Variant, string> = {
  page: 'Реестр открывает отдельную карточку. Наценки видны рядом со строками, производные — под строками сметы.',
  split: 'Карточка открывается рядом с реестром. Наценки правятся в боковой панели, производные открываются вкладкой сметы.',
  sections: 'Карточка открывается на всю ширину. Наценки и версии вынесены в разделы, производные показаны внутри выбранной редакции.',
};

export const statusLabels: Record<OfferStatus, string> = {
  draft: 'Черновик', sent: 'Отправлено', accepted: 'Принято', rejected: 'Отклонено', 'no-answer': 'Без ответа',
};

export interface Offer {
  id: string; number: string; site: string; customer: string; source: 'estimate' | 'pricelist';
  revision?: number; latest?: number; version: number; status: OfferStatus; sentOn?: string;
  net: string; vat?: string; total: string; incomplete?: boolean;
}

export const offers: Offer[] = [
  { id: 'main', number: 'КП-2026-0042', site: 'РТК · Корпус 1', customer: 'ООО «РТК»', source: 'estimate',
    revision: 3, latest: 4, version: 2, status: 'draft', net: '135 850 ₽', vat: '27 170 ₽', total: '163 020 ₽' },
  { id: 'secondary', number: 'КП-2026-0041', site: 'РТК · Корпус 1', customer: 'ООО «РТК»', source: 'estimate',
    revision: 3, latest: 4, version: 1, status: 'sent', sentOn: '20.11.2026', net: '135 850 ₽', vat: '27 170 ₽', total: '163 020 ₽' },
  { id: 'price', number: 'КП-2026-0040', site: 'Склад · Блок Б', customer: 'ООО «Север»', source: 'pricelist',
    version: 1, status: 'draft', net: '52 500 ₽', total: '52 500 ₽', incomplete: true },
];

export const offerSource = (offer: Offer) => offer.source === 'estimate' ? `Смета · ред. ${offer.revision}` : 'Прайс-лист';

export interface Line {
  id: string; name: string; section?: string; quantity: string; price: string; amount: string;
  markup: string; level: 'КП' | 'Глава' | 'Строка'; cost?: string; margin?: string; noPrice?: boolean;
}

export const estimateLines: Line[] = [
  { id: 'a', name: 'Монтаж кабельного лотка', section: '01 Электроснабжение', quantity: '150 м', price: '420 ₽', amount: '63 000 ₽', markup: '20 %', level: 'Глава', cost: '52 500 ₽', margin: '10 500 ₽' },
  { id: 'b', name: 'Прокладка кабеля ВВГнг', section: '01 Электроснабжение', quantity: '200 м', price: '300 ₽', amount: '60 000 ₽', markup: '25 %', level: 'Строка', cost: '48 000 ₽', margin: '12 000 ₽' },
  { id: 'c', name: 'Подключение щита освещения', section: '02 Освещение', quantity: '1 шт.', price: '12 850 ₽', amount: '12 850 ₽', markup: '10 %', level: 'КП', cost: '11 681,82 ₽', margin: '1 168,18 ₽' },
];

export const priceLines: Line[] = [
  { id: 'a', name: 'Монтаж кабельного лотка', quantity: '150 м', price: '350 ₽', amount: '52 500 ₽', markup: '0 %', level: 'КП' },
  { id: 'b', name: 'Проверка цепей управления', quantity: '2 шт.', price: '—', amount: '—', markup: '0 %', level: 'КП', noPrice: true },
];

export const revisions = [2, 3, 4];
