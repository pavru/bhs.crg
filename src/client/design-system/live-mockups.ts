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

export const liveMockups = [duplicatePaymentMockup, paymentRequisitesMockup, workEntryMockup];
