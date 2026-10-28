import { Apartment, Assignment, Balance, BarChart, Bookmark, BugReport, Build, Calculate, CalendarCheck, Database, Description, DocumentScanner, Group, HistoryIcon, Label, Layers, LinkIcon, LocalShipping, MenuBook, Print, ReceiptLong, Settings, Table, TableChart, VerifiedUser, type FilledIconDef, type IconDef } from './icons';

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
  icon: IconDef;
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
  /**
   * Ранг страницы входа (plan-02, ТЗ AUTH-16.1): после входа открывается видимый пункт с наименьшим
   * рангом — см. `entryPage.ts`. Пункту ядра ранг ниже Главной (`HOME_ENTRY_RANK`) запрещён.
   */
  entry?: number;
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
  /** Значок с вариантом заливки: выбранный раздел в рейле и нижней навигации залит ([UI-60]). */
  icon: FilledIconDef;
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
  { id: 'constructions', label: 'Стройки', icon: Apartment, title: 'Стройки',
    hint: 'Объекты, разделы и комплекты документов', panelOnlyAt: '/document-sets' },
  { id: 'data',   label: 'Данные', icon: Database,   title: 'Данные',
    hint: 'Справочники, наборы данных и сверка' },
  // Подпись «Затраты» (было «Учёт»), заголовок прежний — решение макета П0 (plan-02, 22.10.2026).
  { id: 'costs',  label: 'Затраты', icon: Calculate, title: 'Учёт затрат',
    hint: 'Счета, накладные, закрытие месяца' },
  // Модуль «Планирование» (plan-02, макет П0) — последним рабочим разделом: ни у кого привычное не
  // сдвигается. Подпись «Планы»: «Планирование» в 80 px не помещается, «Сметы» сужает раздел, а «План»
  // занят модулем ИД (% плана у разделов). Пока у раздела нет видимых пунктов, его нет в рейле.
  { id: 'plan',   label: 'Планы',  icon: Assignment, title: 'Планирование',
    hint: 'Сметы, коммерческие предложения, прайс-лист и календарь' },
  // Подпись «Система», заголовок «Администрирование»: полное слово в 80 px рейла не помещается
  // (решение владельца, 17.10.2026).
  { id: 'system', label: 'Система', icon: Build,    title: 'Администрирование',
    hint: 'Типы, шаблоны, пользователи и параметры', unscoped: true },
];

const COSTS_REPORTS = 'Отчёты и закрытие месяца';
const PLAN_DIRECTORIES = 'Справочники';

export const workNav: NavItem[] = [
  { to: '/document-sets',   label: 'Стройки',            icon: Apartment,  permission: 'core.constructions.read', section: 'constructions', rail: 'constructions' },
  { to: '/common-data',     label: 'Общие данные',        icon: Database,    permission: 'core.catalog.read', section: 'catalog', rail: 'data' },
  { to: '/datasets',        label: 'Наборы данных',       icon: Layers,      permission: 'core.datasets.read', section: 'datasets', rail: 'data' },
  { to: '/quality-docs',    label: 'Документы качества',  icon: VerifiedUser, module: 'id', rail: 'data' },
  // Право И модуль: доступ к модулю и право заводить счета — разные вещи (у бухгалтера есть первое
  // без второго), а пункт обязан закрываться тем же правом, что стоит на группе адресов экрана.
  { to: '/invoices',        label: 'Счета',               icon: ReceiptLong, module: 'costs',
    permission: 'costs.invoice.read', rail: 'costs' },
  // Накладные закрыты СВОИМ правом (ТЗ COST-29): право на счета их не открывает, и наоборот.
  { to: '/waybills',        label: 'Накладные',           icon: LocalShipping, module: 'costs',
    permission: 'costs.waybill.read', rail: 'costs' },
  // «Реестр счетов» — готовое представление таблицы счетов (ТЗ COST-20.1, задача G4). Закрыт МОДУЛЕМ,
  // без права: ключ таблицы — код модуля, и открывается она всякому, кому открыт «Счета и накладные».
  // Суммы в ней закрывает право на счета — причиной у колонки, а не отказом экрана.
  { to: '/tables/costs.invoices/registry', label: 'Реестр счетов', icon: Table, module: 'costs',
    rail: 'costs', group: COSTS_REPORTS },
  // «Затраты по стройке» (ТЗ COST-20, задача G5): суммы по всем стройкам экземпляра — своим правом на
  // отчёты, а не правом на счета. Оно включает чтение счетов, так что ссылки отчёта в реестр открыты.
  { to: '/site-costs',      label: 'Затраты по стройке',  icon: BarChart, module: 'costs',
    permission: 'costs.report.read', rail: 'costs', group: COSTS_REPORTS },
  // Закрытие периода — служба ядра (ТЗ CORE-35): пункт не зависит от модулей. Право то же, что на
  // адресах закрытия и отмены; сами границы читает любой вошедший, но экран существует ради действия.
  { to: '/periods',         label: 'Учётный период',      icon: CalendarCheck, permission: 'core.period.close',
    section: 'periods', rail: 'costs', group: COSTS_REPORTS },
  { to: '/reconciliations', label: 'Сверка',              icon: Balance,       permission: 'core.reconciliation.run',
    section: 'reconciliations', rail: 'data' },
  // Сметы всех строек (plan-09, ТЗ PLAN-20) — первый пункт раздела «Планы»: с ним раздел появляется в рейле.
  // Право И модуль, как у «Счетов»: адреса смет закрыты `plan.estimate.read` под воротами модуля.
  { to: '/estimates',       label: 'Сметы',               icon: TableChart, module: 'plan',
    permission: 'plan.estimate.read', rail: 'plan' },
  // Запомненные сопоставления смет (plan-09, ответ П1): справочник модуля — им исправляют неверно запомненное.
  { to: '/rate-matches',    label: 'Запомненные сопоставления', icon: Bookmark, module: 'plan',
    permission: 'plan.estimate.read', rail: 'plan', group: PLAN_DIRECTORIES },
];

/**
 * Страницы под адресом «Строек», принадлежащие ДРУГОМУ модулю или разделу (issue #1001, ревью PR
 * #1330): комплект лежит под `/document-sets`, но это модуль ИД. По одному префиксу пункта меню он
 * считался бы «Стройками» и в пространстве без ИД открывался бы без плашки «вне пространства».
 *
 * Панели карточки стройки сюда не вписываются: их записи следуют из реестра панелей
 * (`constructionPanels.ts`, plan-02), полный список — `allPageOwners` там же.
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
];

const DOCS = 'Документы';
const PEOPLE = 'Люди и доступ';
const WATCH = 'Наблюдение';
const GENERAL = 'Общее';

export const settingsNav: NavItem[] = [
  { to: '/document-types',       label: 'Типы документов',       icon: MenuBook, permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/templates',            label: 'Шаблоны',               icon: Description, permission: 'id.config.edit', rail: 'system', group: DOCS },
  { to: '/composite-types',      label: 'Составные типы',        icon: Layers,   permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/field-types',          label: 'Типы полей',            icon: Label,      permission: 'core.types.edit', rail: 'system', group: DOCS },
  // «Сопоставление полей» (issue #1101) — рядом с типами и тем же правом: экран пишет схему типа
  // (тэг типа, тэг поля) адресом редактора. Право на настройку системы его не открывает.
  { to: '/field-mapping',        label: 'Сопоставление полей',   icon: LinkIcon,    permission: 'core.types.edit', rail: 'system', group: DOCS },
  { to: '/recognition-profiles', label: 'Профили распознавания', icon: DocumentScanner, permission: 'core.recognition.settings', rail: 'system', group: DOCS },
  { to: '/print-profiles',       label: 'Профили печати',        icon: Print,  permission: 'core.system.manage', rail: 'system', group: DOCS },
  { to: '/users',                label: 'Пользователи',          icon: Group,    permission: 'core.users.manage', rail: 'system', group: PEOPLE },
  { to: '/roles',                label: 'Роли и права',          icon: VerifiedUser, permission: 'core.users.manage', rail: 'system', group: PEOPLE },
  { to: '/activity',             label: 'Журнал действий',       icon: HistoryIcon,  permission: 'core.audit.read', rail: 'system', group: WATCH },
  { to: '/bug-reports',          label: 'Сообщения об ошибках',  icon: BugReport,      permission: 'core.support.review', rail: 'system', group: WATCH },
  // «Параметры системы», а не «Настройки» (issue #1429): рядом с «Администрированием» слово
  // «настройки» читалось бы как «всё, что здесь», а это один экран.
  { to: '/settings',             label: 'Параметры системы',     icon: Settings, permission: 'core.system.manage', rail: 'system', group: GENERAL },
];
