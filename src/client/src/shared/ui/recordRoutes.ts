import type { AccessInfo } from '@/shared/api/access';
import type { RecordLevel, RecordOpens } from '@/shared/api/recordSearch';
import { workNav, settingsNav } from './navConfig';
import { allowed } from './navAccess';

/**
 * Где открывается запись — по коду её типа (задача G4, issue #1097).
 *
 * Зачем это отдельным списком. Экран таблиц общий для всех модулей и про счета не знает: таблица
 * называет тип записи, стоящей за строкой (`TableDeclaration.recordType`), а ключ строки — её
 * идентификатор. Какой экран эту запись открывает, сказано здесь — рядом с навигацией, потому что
 * это то же знание: какие экраны у клиента есть. Модуль на сервере адресов клиента не знает.
 *
 * ⚠️ Права у записи своего нет — оно берётся у РАЗДЕЛА (`navConfig`), которому принадлежит экран:
 * тем же правом закрыт и маршрут (`RequireAccess`). Второе описание «экран → право» разошлось бы с
 * первым молча — ссылка видна, а за ней «Раздел недоступен». Тест держит, что у каждого экрана
 * отсюда раздел есть: экран без раздела открывался бы всем.
 */
export interface RecordRoute {
  /** Экран записи — адрес раздела из `navConfig`. */
  screen: string;
  /** Параметр адреса, которым экран называет открытую запись. Без него запись — шаг пути:
   *  `/document-sets/<id>` (стройка открывается своей страницей, а не выбором в списке). */
  param?: string;
  /** Что сказано на ссылке: «Открыть счёт». */
  label: string;
  /** Адрес, к которому прикладывается запись, если он не сам экран: панель наборов данных стройки
   *  (`/document-sets/<id>/datasets`) закрыта правом экрана строек, а адрес у неё свой. */
  path?: string;
  /** Экран свой на каждом уровне (набор данных): маршрут уровня записи — с ЕГО экраном, правом которого
   *  он закрыт, — либо null, если у уровня адреса нет. Без уровня (строка таблицы) ссылки нет вовсе. */
  at?: (level: RecordLevel) => RecordRoute | null;
  /** Адрес целиком — у записи, чей экран стоит под другой записью (shell-09, Р-4): комплект — под стройкой,
   *  шаблон — под типом документа. Его знает только карточка (`opens`), поэтому строка таблицы и палитра
   *  такой записи ссылки не дают. `screen` остаётся — им проверяется право раздела. */
  href?: (id: string, opens: RecordOpens) => string;
}

/** Счёт на оплату — на экране счетов; он же читает параметр, чтобы открыть названный счёт. */
export const INVOICE_RECORD = { screen: '/invoices', param: 'invoice', label: 'Открыть счёт' } as const satisfies RecordRoute;

/** Стройка — своей страницей под экраном строек (shell-05, палитра находит записи). */
export const CONSTRUCTION_RECORD = { screen: '/document-sets', label: 'Открыть стройку' } as const satisfies RecordRoute;

/**
 * Набор данных — на экране своего уровня (shell-05, решение «А»): системный — на «Наборах данных», набор
 * стройки, раздела и комплекта — на их панели «Наборы данных». Здесь — экран группы (чип, Tab, «Ещё в
 * разделе»); адрес найденного набора строит `hitRoute` по его уровню. Набор экран называет `?file=`.
 */
const DATASET_SYSTEM = { screen: '/datasets', param: 'file', label: 'Открыть набор данных' } as const satisfies RecordRoute;

/** Набор уровня: системный — на «Наборах данных», под стройкой — панель уровня; без стройки адреса нет. */
function dataSetAt(level: RecordLevel): RecordRoute | null {
  const { scope, scopeId, constructionId } = level;
  if (scope === 'System') return DATASET_SYSTEM;
  if (!constructionId || !scopeId) return null;
  const site = `/document-sets/${encodeURIComponent(constructionId)}`;
  const path = scope === 'Construction' ? `${site}/datasets`
    : scope === 'Section' ? `${site}/sections/${encodeURIComponent(scopeId)}/datasets`
    : scope === 'Set' ? `${site}/sets/${encodeURIComponent(scopeId)}/datasets`
    : null;
  // Панель уровня закрыта правом экрана строек — им она и открыта.
  return path ? { ...DATASET_SYSTEM, screen: '/document-sets', path } : null;
}

export const DATASET_RECORD = { ...DATASET_SYSTEM, at: dataSetAt } as const satisfies RecordRoute;

/**
 * Запись общих данных системного уровня — на странице «Общие данные» (issue #1517): экран открывает
 * названную окном записи. Записи стройки, раздела и комплекта там нет — на них ссылки не дают.
 */
export const CATALOG_RECORD = { screen: '/common-data', param: 'entry', label: 'Открыть в справочнике' } as const satisfies RecordRoute;

/** Комплект документов — под своей стройкой; экран закрыт правом «Строек» (shell-09). */
export const SET_RECORD = {
  screen: '/document-sets', label: 'Открыть комплект',
  href: (id: string, opens: RecordOpens) =>
    `/document-sets/${encodeURIComponent(opens.under)}/sets/${encodeURIComponent(id)}`,
} as const satisfies RecordRoute;

/** Шаблон документа — на странице шаблонов, под своим типом, текущей версией, а не отмеченной (shell-09, Р-5). */
export const TEMPLATE_RECORD = {
  screen: '/templates', label: 'Открыть шаблон',
  href: (id: string, opens: RecordOpens) =>
    `/templates?${new URLSearchParams({ type: opens.under, template: opens.id ?? id })}`,
} as const satisfies RecordRoute;

/** Ключ — код типа записи, тем же письмом, что у модуля на сервере (`CostsRecordTypes`). */
export const RECORD_ROUTES: Readonly<Record<string, RecordRoute>> = {
  'СчётНаОплату': INVOICE_RECORD,
  // Коды типов ядра — те же, что у групп поиска ядра на сервере (`CoreRecordSearch`).
  'Стройка': CONSTRUCTION_RECORD,
  'НаборДанных': DATASET_RECORD,
  // Коды типов модуля `id` — те же, что у его групп поиска на сервере (`IdRecordSearch`).
  'КомплектДокументов': SET_RECORD,
  'ШаблонДокумента': TEMPLATE_RECORD,
};

/**
 * Экран найденной записи — либо null. У типа с одним экраном это экран группы; у типа с экраном на каждом
 * уровне (`at`) — экран уровня записи, закрытый правом СВОЕГО раздела: набор стройки — правом строек,
 * системный — правом наборов данных. Нет уровня или экран закрыт — строки нет: она вела бы в отказ.
 */
export function hitRoute(group: RecordRoute, level: RecordLevel | null | undefined, access: AccessInfo): RecordRoute | null {
  // Адрес под другой записью строит только карточка «Недавних» (`recentTarget`): у найденного его нет.
  if (group.href) return null;
  if (!group.at) return group;
  const route = level ? group.at(level) : null;
  return route ? openTo(route, access) : null;
}

export interface RecordLink {
  to: string;
  label: string;
}

/**
 * Экран записей типа, открытый этому человеку, — либо null: у типа нет экрана, либо экран закрыт.
 * Спрашивается ОДИН раз на таблицу: тип и доступ у всех её строк одни.
 */
export function recordScreen(recordType: string | null | undefined, access: AccessInfo): RecordRoute | null {
  // Свой ключ, а не унаследованный: код типа приходит с сервера, и «constructor» нашёл бы функцию.
  const route = recordType && Object.hasOwn(RECORD_ROUTES, recordType) ? RECORD_ROUTES[recordType] : undefined;
  return route ? openTo(route, access) : null;
}

/** Экран открыт этому человеку — правом его раздела (`navConfig`), тем же, что закрывает маршрут. */
export function openTo(route: RecordRoute, access: AccessInfo): RecordRoute | null {
  const section = [...workNav, ...settingsNav].find(item => item.to === route.screen);
  return section && allowed(section, access) ? route : null;
}

/**
 * Ссылка на запись — либо null: экрана нет (`recordScreen`) или нет ключа. Ссылку, по которой придёт
 * отказ, не обещаем: закрытый экран узнаётся до щелчка, а не после.
 *
 * @param name чем эта запись отличается от соседних — идёт в подпись: «Открыть счёт: ООО «Кабель»».
 *   Без него двести ссылок страницы назывались бы одинаково, и в списке ссылок читалки экрана
 *   нужную было бы не выбрать.
 */
export function recordLink(screen: RecordRoute | null, id: string | null, name?: string): RecordLink | null {
  // Экран уровня без уровня не выбрать: строка таблицы его не знает, и ссылка на экран группы привела бы
  // набор стройки на системную страницу, где его нет (ревью #1915).
  // Так же экран под другой записью (комплект под стройкой): `/document-sets/<id комплекта>` — страница
  // стройки, которой нет (shell-09, Р-4).
  if (!screen || !id || screen.at || screen.href) return null;
  return {
    to: recordHref(screen, id),
    label: name ? `${screen.label}: ${name}` : screen.label,
  };
}

/** Адрес записи на её экране. Ключ кодируется: в адресе он — значение, а не кусок пути. */
export function recordHref(screen: RecordRoute, id: string): string {
  const base = screen.path ?? screen.screen;
  return screen.param
    ? `${base}?${new URLSearchParams({ [screen.param]: id })}`
    : `${base}/${encodeURIComponent(id)}`;
}
