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

const planSchedulesMockup: LiveMockup = {
  id: 'plan-20-schedule-ui',
  title: 'П4 · Графики стройки',
  kind: 'design',
  entry: 'plan-schedules.html',
  pages: [
    { path: 'a', title: 'А · Рабочая таблица', width: 1440 },
    { path: 'b', title: 'Б · Список и карточка работы', width: 1440 },
    { path: 'c', title: 'В · Работы по разделам', width: 1440 },
  ],
  aliases: [],
  reference: 'openspec/changes/plan-20-schedule-ui/design.md · Раунд П4',
};

const workSummaryMockup: LiveMockup = {
  id: 'work-23-summary',
  title: 'Сводка · Р5',
  kind: 'design',
  entry: 'work23.html',
  pages: [
    { path: 'a', title: 'А · Компактный календарь', width: 1280 },
    { path: 'b', title: 'Б · Статусы словами', width: 1280 },
    { path: 'c', title: 'В · Календарь и подробности', width: 1280 },
  ],
  aliases: [],
  reference: 'openspec/changes/work-23-summary/design.md · Раунд Р5',
};

const planProgressMockup: LiveMockup = {
  id: 'plan-25-progress',
  title: 'П5 · Ход работ',
  kind: 'design',
  entry: 'plan-progress.html',
  pages: [
    { path: 'a', title: 'А · Общая таблица', width: 1440 },
    { path: 'b', title: 'Б · Строка и подробности', width: 1440 },
    { path: 'c', title: 'В · Работы по наличию данных', width: 1440 },
  ],
  aliases: [],
  reference: 'openspec/changes/plan-25-progress/design.md · Раунд П5',
};

const workCustomerFieldsMockup: LiveMockup = {
  id: 'work-24-customer-fields',
  title: 'Поля заказчика · Нужно к приёмке',
  kind: 'design',
  entry: 'work24.html',
  pages: [
    { path: 'a', title: 'А · Пометки рядом с полями' },
    { path: 'b', title: 'Б · Отдельная группа полей' },
    { path: 'c', title: 'В · Переход к незаполненным' },
  ],
  aliases: [],
  reference: 'openspec/changes/work-24-customer-fields/design.md',
};

const workMaterialsMockup: LiveMockup = {
  id: 'work-26-materials-hint',
  title: 'Материалы строки отчёта · Work-26',
  kind: 'design',
  entry: 'work26.html',
  pages: [
    { path: 'a', title: 'А · Ярусы в одном окне' },
    { path: 'b', title: 'Б · Источники на боковом листе' },
    { path: 'c', title: 'В · Подсказка в строке отчёта' },
  ],
  aliases: [],
  reference: 'openspec/changes/work-26-materials-hint/design.md',
};

export const liveMockups = [
  duplicatePaymentMockup, paymentRequisitesMockup, workEntryMockup, planOffersMockup, planCalendarsMockup,
  planSchedulesMockup, planProgressMockup, workSummaryMockup, workCustomerFieldsMockup, workMaterialsMockup,
  {
    id: 'mob-06-offline-entry',
    title: 'Мои отчёты · Полевой ввод MOB-06',
    kind: 'design',
    entry: 'mob06.html',
    pages: [
      { path: 'a', title: 'А · Карточка отчёта', width: 1024 },
      { path: 'b', title: 'Б · Поиск в отчёте', width: 1024 },
      { path: 'c', title: 'В · Ввод по шагам', width: 1024 },
    ],
    aliases: [],
    reference: 'openspec/changes/mob-06-offline-entry/design.md · Варианты для выбора',
  },
  {
    id: 'work-27-actual-works-contract',
    title: 'Сверка бригад · Публикация факта',
    kind: 'design',
    entry: 'work27.html',
    pages: [
      { path: 'a', title: 'А · Статус в шапке группы' },
      { path: 'b', title: 'Б · Сводка над строками' },
      { path: 'c', title: 'В · Раскрываемые подробности' },
    ],
    aliases: [],
    reference: 'openspec/changes/work-27-actual-works-contract/design.md',
  },
  {
    id: 'work-28-estimate-vs-actual',
    title: 'Трудозатраты и выработка · Р6',
    kind: 'design',
    entry: 'work28.html',
    pages: [
      { path: 'a', title: 'А · Сводка в строке', width: 1440 },
      { path: 'b', title: 'Б · Панель подробностей', width: 1440 },
      { path: 'c', title: 'В · Раскрытие на месте', width: 1440 },
    ],
    aliases: [],
    reference: 'openspec/changes/work-28-estimate-vs-actual/design.md · Раунд Р6',
  },
  {
    id: 'plan-26-purchase-requests',
    title: 'Заявки на закупку · П6',
    kind: 'design',
    entry: 'plan26.html',
    pages: [
      { path: 'a', title: 'А · Реестр и карточка', width: 1440 },
      { path: 'b', title: 'Б · По стройкам', width: 1440 },
      { path: 'c', title: 'В · Очередь по статусам', width: 1440 },
    ],
    aliases: [],
    reference: 'openspec/changes/archive/2026-11-27-plan-26-purchase-requests/design.md · Раунд П6',
  },
  {
    id: 'work-31-autosubmit-required-missing',
    title: 'Приёмка · Место и материалы после автоподачи',
    kind: 'design',
    entry: 'work31.html',
    pages: [
      { path: 'a', title: 'А · Пометка и список в карточке', width: 1280 },
      { path: 'b', title: 'Б · Очередь и панель отчёта', width: 1280 },
      { path: 'c', title: 'В · Пропуски в строках', width: 1280 },
    ],
    aliases: [],
    reference: 'openspec/changes/work-31-autosubmit-required-missing/design.md · Варианты интерфейса',
  },
  {
    id: 'work-32-closed-report-correction-ui',
    title: 'Исправление закрытого отчёта · Work-32',
    kind: 'design',
    entry: 'work32.html',
    pages: [
      { path: 'a', title: 'А · Правка в карточке', width: 1280 },
      { path: 'b', title: 'Б · Боковая панель', width: 1280 },
      { path: 'c', title: 'В · По шагам', width: 1280 },
    ],
    aliases: [],
    reference: 'openspec/changes/work-32-closed-report-correction-ui/design.md · D5',
  },
] satisfies LiveMockup[];
