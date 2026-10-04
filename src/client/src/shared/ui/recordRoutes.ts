import type { AccessInfo } from '@/shared/api/access';
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
  /** Параметр адреса, которым экран называет открытую запись. */
  param: string;
  /** Что сказано на ссылке: «Открыть счёт». */
  label: string;
}

/** Счёт на оплату — на экране счетов; он же читает параметр, чтобы открыть названный счёт. */
export const INVOICE_RECORD: RecordRoute = { screen: '/invoices', param: 'invoice', label: 'Открыть счёт' };

/** Ключ — код типа записи, тем же письмом, что у модуля на сервере (`CostsRecordTypes`). */
export const RECORD_ROUTES: Readonly<Record<string, RecordRoute>> = {
  'СчётНаОплату': INVOICE_RECORD,
};

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
  if (!route) return null;
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
    to: `${screen.screen}?${new URLSearchParams({ [screen.param]: id })}`,
    label: name ? `${screen.label}: ${name}` : screen.label,
  };
}
