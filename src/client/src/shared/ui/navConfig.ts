import { FolderOpen, BookOpen, FileText, Settings, Layers, Database, Tag, ShieldCheck, Users, ScanText, Scale, Bug, History, ReceiptText, Table2, Truck, CalendarCheck, ChartNoAxesColumn } from 'lucide-react';

/**
 * Пункты навигации — общий источник для сайдбара (AppShell) и командной палитры (Ctrl+K).
 *
 * Каждый пункт называет, чем он открывается (ТЗ AUTH-16): правом, модулем или и тем и другим.
 * Названий ролей здесь нет и быть не может — навигация строится только по ответу
 * `/api/account/access` (AUTH-14).
 *
 * ⚠️ Право у пункта — то же, что стоит на ГРУППЕ АДРЕСОВ, которую открывает экран. Разойдясь,
 * они дают худший из ответов: пункт виден, а всё внутри отвечает отказом. Проверяется это не
 * глазами — тест сверяет состав пунктов с тем, что отдаёт живое приложение.
 */
export interface NavItem {
  to: string;
  label: string;
  icon: typeof FolderOpen;
  /** Право, без которого пункт не показывается. */
  permission?: string;
  /** Модуль, без доступа к которому пункт не показывается. */
  module?: string;
}

export const workNav: NavItem[] = [
  { to: '/document-sets',   label: 'Стройки',            icon: FolderOpen,  permission: 'core.constructions.read' },
  { to: '/common-data',     label: 'Общие данные',        icon: Database,    permission: 'core.catalog.read' },
  { to: '/datasets',        label: 'Наборы данных',       icon: Layers,      permission: 'core.datasets.read' },
  { to: '/quality-docs',    label: 'Документы качества',  icon: ShieldCheck, module: 'id' },
  // Право И модуль: доступ к модулю и право заводить счета — разные вещи (у бухгалтера есть первое
  // без второго), а пункт обязан закрываться тем же правом, что стоит на группе адресов экрана.
  { to: '/invoices',        label: 'Счета',               icon: ReceiptText, module: 'costs',
    permission: 'costs.invoice.read' },
  // Накладные закрыты СВОИМ правом (ТЗ COST-29): право на счета их не открывает, и наоборот.
  { to: '/waybills',        label: 'Накладные',           icon: Truck, module: 'costs',
    permission: 'costs.waybill.read' },
  // «Реестр счетов» — готовое представление таблицы счетов (ТЗ COST-20.1, задача G4). Закрыт МОДУЛЕМ,
  // без права: ключ таблицы — код модуля, и открывается она всякому, кому открыт «Счета и накладные».
  // Суммы в ней закрывает право на счета — причиной у колонки, а не отказом экрана.
  { to: '/tables/costs.invoices/registry', label: 'Реестр счетов', icon: Table2, module: 'costs' },
  // «Затраты по стройке» (ТЗ COST-20, задача G5): суммы по всем стройкам экземпляра — своим правом на
  // отчёты, а не правом на счета. Оно включает чтение счетов, так что ссылки отчёта в реестр открыты.
  { to: '/site-costs',      label: 'Затраты по стройке',  icon: ChartNoAxesColumn, module: 'costs',
    permission: 'costs.report.read' },
  // Закрытие периода — служба ядра (ТЗ CORE-35): пункт не зависит от модулей. Право то же, что на
  // адресах закрытия и отмены; сами границы читает любой вошедший, но экран существует ради действия.
  { to: '/periods',         label: 'Учётный период',      icon: CalendarCheck, permission: 'core.period.close' },
  { to: '/reconciliations', label: 'Сверка',              icon: Scale,       permission: 'core.reconciliation.run' },
];

export const settingsNav: NavItem[] = [
  { to: '/document-types',       label: 'Типы документов',       icon: BookOpen, permission: 'core.types.edit' },
  { to: '/composite-types',      label: 'Составные типы',        icon: Layers,   permission: 'core.types.edit' },
  { to: '/field-types',          label: 'Типы полей',            icon: Tag,      permission: 'core.types.edit' },
  { to: '/templates',            label: 'Шаблоны',               icon: FileText, permission: 'id.config.edit' },
  { to: '/recognition-profiles', label: 'Профили распознавания', icon: ScanText, permission: 'core.recognition.settings' },
  { to: '/users',                label: 'Пользователи',          icon: Users,    permission: 'core.users.manage' },
  { to: '/roles',                label: 'Роли и права',          icon: ShieldCheck, permission: 'core.users.manage' },
  { to: '/activity',             label: 'Журнал действий',       icon: History,  permission: 'core.audit.read' },
  { to: '/bug-reports',          label: 'Сообщения об ошибках',  icon: Bug,      permission: 'core.support.review' },
  { to: '/settings',             label: 'Настройки',             icon: Settings, permission: 'core.system.manage' },
];
