import type { AccessInfo } from '@/shared/api/access';
import type { RecordLevel } from '@/shared/api/recordSearch';
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
export const DATASET_RECORD = { screen: '/datasets', param: 'file', label: 'Открыть набор данных' } as const satisfies RecordRoute;

/**
 * Запись общих данных системного уровня — на странице «Общие данные» (issue #1517): экран открывает
 * названную окном записи. Записи стройки, раздела и комплекта там нет — на них ссылки не дают.
 */
export const CATALOG_RECORD = { screen: '/common-data', param: 'entry', label: 'Открыть в справочнике' } as const satisfies RecordRoute;

/** Ключ — код типа записи, тем же письмом, что у модуля на сервере (`CostsRecordTypes`). */
export const RECORD_ROUTES: Readonly<Record<string, RecordRoute>> = {
  'СчётНаОплату': INVOICE_RECORD,
  // Коды типов ядра — те же, что у групп поиска ядра на сервере (`CoreRecordSearch`).
  'Стройка': CONSTRUCTION_RECORD,
  'НаборДанных': DATASET_RECORD,
};

/** Панель наборов данных уровня под стройкой; уровень без стройки или без своей записи адреса не имеет. */
function dataSetPath(level: RecordLevel): string | null {
  const { scope, scopeId, constructionId } = level;
  if (!constructionId || !scopeId) return null;
  const site = `${CONSTRUCTION_RECORD.screen}/${encodeURIComponent(constructionId)}`;
  if (scope === 'Construction') return `${site}/datasets`;
  if (scope === 'Section') return `${site}/sections/${encodeURIComponent(scopeId)}/datasets`;
  if (scope === 'Set') return `${site}/sets/${encodeURIComponent(scopeId)}/datasets`;
  return null;
}

/**
 * Экран найденной записи — либо null. У большинства типов он один (`group` — экран группы); у набора
 * данных — свой на каждом уровне, и закрыт он правом СВОЕГО раздела: набор стройки — правом строек,
 * системный — правом наборов данных. Нет уровня или экран закрыт — строки нет: она вела бы в отказ.
 */
export function hitRoute(recordType: string, group: RecordRoute, level: RecordLevel | null | undefined,
  access: AccessInfo): RecordRoute | null {
  if (recordType !== 'НаборДанных') return group;
  if (level?.scope === 'System') return openTo(DATASET_RECORD, access);
  const path = level ? dataSetPath(level) : null;
  return path ? openTo({ ...DATASET_RECORD, screen: CONSTRUCTION_RECORD.screen, path }, access) : null;
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
  if (!screen || !id) return null;
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
