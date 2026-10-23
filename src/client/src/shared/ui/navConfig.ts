import { FolderOpen, BookOpen, FileText, Settings, Layers, Database, Tag, ShieldCheck, Users, ScanText, Printer, Scale, Bug, History, ReceiptText, Table2, Truck, CalendarCheck, ChartNoAxesColumn, Link2, Building2, Calculator, Wrench, ClipboardList } from 'lucide-react';

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
  /**
   * Право, без которого пункт не показывается. Список — «любое из» (plan-02): пункт виден при любом
   * праве списка, а его адреса стоят под `AppPolicies.AnyPermission` с тем же списком.
   */
  permission?: string | string[];
  /** Модуль, без доступа к которому пункт не показывается. */
  module?: string;
  /**
   * Раздел ЯДРА, которому принадлежит пункт (issue #1001): рабочее пространство показывает его, если
   * раздел объявил хоть один модуль пространства (`CoreSections` на сервере, те же коды). Пункт
   * модуля раздела не называет — он входит в пространство своим модулем.
   */
  section?: string;
  /** Раздел рейла, в подпанели которого стоит пункт (issue #1429). */
  rail: RailId;
  /**
   * Группа подпанели. Пункты одной группы стоят подряд; пункт без группы — в начале подпанели, над
   * группами. Порядок пунктов в массиве и есть порядок в подпанели.
   */
  group?: string;
}

/**
 * Разделы рейла (issue #1429): рейл называет РАЗДЕЛ, а страницы раздела — в его подпанели. Так рейл
 * короток на любом экране, а двадцать пунктов перестают быть одним списком, который надо прочесть
 * целиком, чтобы найти нужное.
 */
export type RailId = 'constructions' | 'data' | 'costs' | 'plan' | 'system';

export interface RailSectionDef {
  id: RailId;
  /** Подпись в рейле: под значком 80 px, поэтому одно короткое слово. */
  label: string;
  icon: typeof FolderOpen;
  /** Заголовок подпанели — полное имя раздела. */
  title: string;
  /** Строка под заголовком: что в разделе, чтобы не открывать его наугад. */
  hint: string;
  /**
   * Подпанель только на странице списка раздела, а не в карточке под ним. Карточка стройки ведёт
   * свою навигацию (`ListDetailShell`), и две панели навигации рядом оставили бы содержимому
   * 650 px из 1440 (эпик #1428).
   */
  panelOnlyAt?: string;
  /**
   * Раздел не сужается пространством. «Система» — администрирование, а не контекст работы: прятать
   * «Пользователей» за пространством «Счета» значило бы заставлять администратора переключаться
   * ради своего же меню (ТЗ AUTH-16.1).
   */
  unscoped?: boolean;
}

export const railSections: RailSectionDef[] = [
  { id: 'constructions', label: 'Стройки', icon: Building2, title: 'Стройки',
    hint: 'Объекты, разделы и комплекты документов', panelOnlyAt: '/document-sets' },
  { id: 'data',   label: 'Данные', icon: Database,   title: 'Данные',
    hint: 'Справочники, наборы данных и сверка' },
  // Подпись «Затраты» (было «Учёт»), заголовок прежний — решение макета П0 (plan-02, 22.10.2026).
  { id: 'costs',  label: 'Затраты', icon: Calculator, title: 'Учёт затрат',
    hint: 'Счета, накладные, закрытие месяца' },
  // Модуль «Планирование» (plan-02, макет П0) — последним рабочим разделом: ни у кого привычное не
  // сдвигается. Подпись «Планы»: «Планирование» в 80 px не помещается, «Сметы» сужает раздел, а «План»
  // занят модулем ИД (% плана у разделов). Пока у раздела нет видимых пунктов, его нет в рейле.
  { id: 'plan',   label: 'Планы',  icon: ClipboardList, title: 'Планирование',
    hint: 'Сметы, коммерческие предложения, прайс-лист и календарь' },
  // Подпись «Система», заголовок «Администрирование»: полное слово в 80 px рейла не помещается
  // (решение владельца, 17.10.2026).
  { id: 'system', label: 'Система', icon: Wrench,    title: 'Администрирование',
    hint: 'Типы, шаблоны, пользователи и параметры', unscoped: true },
];

const COSTS_REPORTS = 'Отчёты и закрытие месяца';

export const workNav: NavItem[] = [
  { to: '/document-sets',   label: 'Стройки',            icon: Building2,  permission: 'core.constructions.read', section: 'constructions', rail: 'constructions' },
  { to: '/common-data',     label: 'Общие данные',        icon: Database,    permission: 'core.catalog.read', section: 'catalog', rail: 'data' },
  { to: '/datasets',        label: 'Наборы данных',       icon: Layers,      permission: 'core.datasets.read', section: 'datasets', rail: 'data' },
  { to: '/quality-docs',    label: 'Документы качества',  icon: ShieldCheck, module: 'id', rail: 'data' },
  // Право И модуль: доступ к модулю и право заводить счета — разные вещи (у бухгалтера есть первое
  // без второго), а пункт обязан закрываться тем же правом, что стоит на группе адресов экрана.
  { to: '/invoices',        label: 'Счета',               icon: ReceiptText, module: 'costs',
    permission: 'costs.invoice.read', rail: 'costs' },
  // Накладные закрыты СВОИМ правом (ТЗ COST-29): право на счета их не открывает, и наоборот.
  { to: '/waybills',        label: 'Накладные',           icon: Truck, module: 'costs',
    permission: 'costs.waybill.read', rail: 'costs' },
  // «Реестр счетов» — готовое представление таблицы счетов (ТЗ COST-20.1, задача G4). Закрыт МОДУЛЕМ,
  // без права: ключ таблицы — код модуля, и открывается она всякому, кому открыт «Счета и накладные».
  // Суммы в ней закрывает право на счета — причиной у колонки, а не отказом экрана.
  { to: '/tables/costs.invoices/registry', label: 'Реестр счетов', icon: Table2, module: 'costs',
    rail: 'costs', group: COSTS_REPORTS },
  // «Затраты по стройке» (ТЗ COST-20, задача G5): суммы по всем стройкам экземпляра — своим правом на
  // отчёты, а не правом на счета. Оно включает чтение счетов, так что ссылки отчёта в реестр открыты.
  { to: '/site-costs',      label: 'Затраты по стройке',  icon: ChartNoAxesColumn, module: 'costs',
    permission: 'costs.report.read', rail: 'costs', group: COSTS_REPORTS },
  // Закрытие периода — служба ядра (ТЗ CORE-35): пункт не зависит от модулей. Право то же, что на
  // адресах закрытия и отмены; сами границы читает любой вошедший, но экран существует ради действия.
  { to: '/periods',         label: 'Учётный период',      icon: CalendarCheck, permission: 'core.period.close',
    section: 'periods', rail: 'costs', group: COSTS_REPORTS },
  { to: '/reconciliations', label: 'Сверка',              icon: Scale,       permission: 'core.reconciliation.run',
    section: 'reconciliations', rail: 'data' },
];

/**
 * Страницы под адресом «Строек», принадлежащие ДРУГОМУ модулю или разделу (issue #1001, ревью PR
 * #1330): комплект лежит под `/document-sets`, но это модуль ИД; панель «Наборы данных» стройки и
 * раздела — раздел ядра `datasets`. По одному префиксу пункта меню они считались бы «Стройками» и в
 * пространстве без них открывались бы без плашки «вне пространства».
 *
 * ⚠️ Список повторяет маршруты `DocumentSetsPage`. Разойтись им не даёт тест
 * (`workspaceRoutes.test.ts`): каждый маршрут комплекта обязан найтись здесь, а маршрут стройки и
 * раздела — нет.
 */
export interface PageOwner {
  match: RegExp;
  label: string;
  module?: string;
  section?: string;
}

export const pageOwners: PageOwner[] = [
  { match: /^\/document-sets\/[^/]+\/sets\/[^/]+/, label: 'Комплект документов', module: 'id' },
  { match: /^\/document-sets\/[^/]+(\/sections\/[^/]+)?\/datasets$/, label: 'Наборы данных стройки', section: 'datasets' },
];

const DOCS = 'Документы';
const PEOPLE = 'Люди и доступ';
const WATCH = 'Наблюдение';
const GENERAL = 'Общее';

export const settingsNav: NavItem[] = [
  { to: '/document-types',       label: 'Типы документов',       icon: BookOpen, permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/templates',            label: 'Шаблоны',               icon: FileText, permission: 'id.config.edit', rail: 'system', group: DOCS },
  { to: '/composite-types',      label: 'Составные типы',        icon: Layers,   permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/field-types',          label: 'Типы полей',            icon: Tag,      permission: 'core.types.edit', rail: 'system', group: DOCS },
  // «Сопоставление полей» (issue #1101) — рядом с типами и тем же правом: экран пишет схему типа
  // (тэг типа, тэг поля) адресом редактора. Право на настройку системы его не открывает.
  { to: '/field-mapping',        label: 'Сопоставление полей',   icon: Link2,    permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/recognition-profiles', label: 'Профили распознавания', icon: ScanText, permission: 'core.recognition.settings', rail: 'system', group: DOCS },
  { to: '/print-profiles',       label: 'Профили печати',        icon: Printer,  permission: 'core.system.manage', rail: 'system', group: DOCS },
  { to: '/users',                label: 'Пользователи',          icon: Users,    permission: 'core.users.manage', rail: 'system', group: PEOPLE },
  { to: '/roles',                label: 'Роли и права',          icon: ShieldCheck, permission: 'core.users.manage', rail: 'system', group: PEOPLE },
  { to: '/activity',             label: 'Журнал действий',       icon: History,  permission: 'core.audit.read', rail: 'system', group: WATCH },
  { to: '/bug-reports',          label: 'Сообщения об ошибках',  icon: Bug,      permission: 'core.support.review', rail: 'system', group: WATCH },
  // «Параметры системы», а не «Настройки» (issue #1429): рядом с «Администрированием» слово
  // «настройки» читалось бы как «всё, что здесь», а это один экран.
  { to: '/settings',             label: 'Параметры системы',     icon: Settings, permission: 'core.system.manage', rail: 'system', group: GENERAL },
];
