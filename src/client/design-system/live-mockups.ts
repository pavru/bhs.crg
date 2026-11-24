import type { MockupPage } from './mockup-model';

export interface LiveMockup {
  id: string;
  title: string;
  kind: 'design';
  entry: string;
  pages: MockupPage[];
  aliases: string[];
  reference: string;
}

export const duplicatePaymentMockup: LiveMockup = {
  id: 'costs-invoice-duplicate-payment-block',
  title: 'Счёт · Проверка совпадений перед оплатой',
  kind: 'design',
  entry: 'duplicate-payment.html',
  pages: [
    { path: 'pair', title: 'А · Компактное окно пары' },
    { path: 'sheet', title: 'Б · Боковая панель совпадений' },
    { path: 'compare', title: 'В · Сверка двух карточек' },
  ],
  aliases: [],
  reference: 'openspec/changes/costs-invoice-duplicate-payment-block/design.md · D5',
};

export const paymentRequisitesMockup: LiveMockup = {
  id: 'costs-invoice-payment-requisites',
  title: 'Счёт · Обязательные реквизиты оплаты',
  kind: 'design',
  entry: 'payment-requisites.html',
  pages: [
    { path: 'items', title: 'А · Отдельные пункты' },
    { path: 'group', title: 'Б · Одна группа реквизитов' },
    { path: 'inline', title: 'В · Перечень рядом с первой частью' },
  ],
  aliases: [],
  reference: 'openspec/changes/costs-invoice-payment-requisites/design.md · D4',
};

const workEntryMockup: LiveMockup = {
  id: 'work-22-web-entry-ui',
  title: 'Веб-ввод отчёта · Р3',
  kind: 'design',
  entry: 'work22.html',
  pages: [
    { path: 'a', title: 'А · Реестр и сетка' },
    { path: 'b', title: 'Б · Карточки на одной странице' },
    { path: 'c', title: 'В · Ввод по шагам' },
  ],
  aliases: [],
  reference: 'openspec/changes/work-22-web-entry-ui/design.md · Раунд Р3',
};

export const planOffersMockup: LiveMockup = {
  id: 'plan-17-offers-ui',
  title: 'П3 · Коммерческие предложения и прайс-лист',
  kind: 'design',
  entry: 'plan-offers.html',
  pages: [
    { path: 'page', title: 'А · Карточка на всю страницу' },
    { path: 'split', title: 'Б · Реестр и карточка рядом' },
    { path: 'sections', title: 'В · Карточка по разделам' },
  ],
  aliases: [],
  reference: 'openspec/changes/plan-17-offers-ui/design.md',
};

const planCalendarsMockup: LiveMockup = {
  id: 'plan-18-calendars',
  title: 'П4 · Календари компании и стройки',
  kind: 'design',
  entry: 'plan-calendars.html',
  pages: [
    { path: 'year', title: 'А · Год целиком' },
    { path: 'quarter', title: 'Б · Квартал и подробности дня' },
    { path: 'exceptions', title: 'В · Исключения и обзор года' },
  ],
  aliases: [],
  reference: 'openspec/changes/archive/2026-11-24-plan-18-calendars/design.md · Раунд П4',
};

export const liveMockups = [duplicatePaymentMockup, paymentRequisitesMockup, workEntryMockup, planOffersMockup, planCalendarsMockup];
