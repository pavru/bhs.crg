import type { SchemaField } from '@/shared/api/schema';
import type { DatePrecision, EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { parseNumber } from '@/shared/utils/parseNumber';
import { dateMask } from '@/shared/format/dateMask';
import { resolveLocale } from '@/shared/hooks/useLocale';
import { readSegments, splitDate } from '@/shared/ui/dateSegments';

/**
 * Приведение вставленной ячейки к типу поля (issue #1064).
 *
 * <p>Правило одно на все типы: значение либо укладывается в тип ЦЕЛИКОМ, либо это отказ — `null`.
 * Отказ означает «ячейка осталась пустой и названа человеку», а не «пусто» и не значение по
 * умолчанию. Догадка дороже отказа: неразобранное человек видит и правит, а разобранное неверно
 * уезжает в данные молча.</p>
 *
 * <p>Раньше строгим был только разбор чисел, и два соседних типа в той же функции жили по старому
 * правилу: `boolean` любой неопознанный текст («нет данных», «2 шт») превращал в уверенное «нет»,
 * а `date` при непонятном формате клал в поле сырую строку — `DateInput` показать её не может, и
 * поле выглядит пустым при непустых данных. Оба исхода — тот же дефект, ради которого писался
 * строгий разбор, поэтому правило здесь общее.</p>
 */

export interface CoerceContext {
  /** Региональная настройка как есть, включая `system`: по ней читаются числа и порядок даты. */
  locale: string;
  primitiveTypes: PrimitiveTypeDef[];
  enumTypes: EnumTypeDef[];
}

const norm = (s: string) => s.trim().toLowerCase();

const TRUE_WORDS = ['1', 'да', 'true', 'yes', '+', 'y', 'истина'];
const FALSE_WORDS = ['0', 'нет', 'false', 'no', '-', 'n', 'ложь'];

/**
 * Дата → хранимый вид `YYYY-MM-DD`. Разбор — общий с полем даты (`splitDate`): дата в порядке языка
 * пользователя (issue #1461: `ДД.ММ.ГГГГ` у ru-RU, `MM/DD/YYYY` у en-US; разделитель — точка, дробь
 * или дефис) и ISO. Порядок — только из языка, по значению не угадывается: «01.02.2026» — это
 * 1 февраля у ru-RU и 2 января у en-US, и угадывать здесь значило бы молча путать месяц с днём.
 * Язык обязателен: молча подставленный русский порядок был бы той же подменой.
 * Точность типа сужает дату, как у поля: «10.2026» у «месяц и год» → `2026-10-01`.
 * Двузначный год не принимается намеренно (поле его дополняет, таблица — нет: в поле человек видит
 * итог сразу): `15.01.26` — это и 1926-й, и 2026-й. Календарная несуразица (`31.02.2026`) — отказ:
 * подставлять 3 марта нельзя.
 */
export function parseDateCell(raw: string, locale: string, precision: DatePrecision = 'day'): string | null {
  const got = splitDate(raw, precision, dateMask(locale).order);
  if (!got || got.y.length !== 4) return null;
  const r = readSegments(got, precision);
  return r.kind === 'date' ? r.iso : null;
}

/** Да/нет. Всё, что не опознано, — отказ, а НЕ «нет»: уверенное «нет» здесь и есть враньё. */
export function parseBooleanCell(raw: string): boolean | null {
  const t = norm(raw);
  if (TRUE_WORDS.includes(t)) return true;
  if (FALSE_WORDS.includes(t)) return false;
  return null;
}

/** Вариант списка: сверяем и с кодом, и с подписью, а храним КОД (реестр EnumType, issue #59). */
function parseEnumCell(field: SchemaField, raw: string, ctx: CoerceContext): string | null {
  const def = field.typeId ? ctx.enumTypes.find(e => e.id === field.typeId) : undefined;
  const opts = def
    ? def.values.map(v => ({ code: v.code, label: v.label }))
    : (field.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
  const t = norm(raw);
  return opts.find(o => norm(o.code) === t || norm(o.label) === t)?.code ?? null;
}

/** База пользовательского типа: по ней и разбираем (`primitive` — не «строка», issue #1064). */
function baseTypeOf(field: SchemaField, ctx: CoerceContext): PrimitiveTypeDef['baseType'] | null {
  const def = field.typeId ? ctx.primitiveTypes.find(p => p.id === field.typeId) : undefined;
  return def?.baseType ?? null;
}

/** Точность даты пользовательского типа (issue #60): по ней сужаются разбор и маска в отказе. */
function precisionOf(field: SchemaField, ctx: CoerceContext): DatePrecision {
  const def = field.typeId ? ctx.primitiveTypes.find(p => p.id === field.typeId) : undefined;
  return def?.constraints.datePrecision ?? 'day';
}

/**
 * Значение ячейки в виде, пригодном для хранения. `null` — не разобрано: ячейка останется пустой
 * и будет названа человеку.
 */
export function coerceScalar(field: SchemaField, raw: string, ctx: CoerceContext): unknown {
  switch (field.type) {
    case 'number': return parseNumber(raw, ctx.locale);
    case 'boolean': return parseBooleanCell(raw);
    case 'enum': return parseEnumCell(field, raw, ctx);
    case 'date': return parseDateCell(raw, resolveLocale(ctx.locale));
    case 'primitive': {
      const base = baseTypeOf(field, ctx);
      if (base === 'number') return parseNumber(raw, ctx.locale);
      if (base === 'date') return parseDateCell(raw, resolveLocale(ctx.locale), precisionOf(field, ctx));
      // База `string` — как обычная строка. Неизвестный тип (не доехал справочник) тоже: отказывать
      // из-за незагруженного справочника нельзя, это отказ не по данным.
      return raw;
    }
    default: return raw;
  }
}

/** Чем объяснить отказ по ячейке: названием того, чем значение не оказалось. */
export function rejectReason(field: SchemaField, ctx: CoerceContext): string {
  const type = field.type === 'primitive' ? baseTypeOf(field, ctx) : field.type;
  if (type === 'number') return 'не число';
  if (type === 'date') return `не дата (ожидается ${dateMask(resolveLocale(ctx.locale), precisionOf(field, ctx)).text})`;
  if (type === 'boolean') return 'не «да» и не «нет»';
  if (type === 'enum') return 'нет такого варианта в списке';
  return 'не разобрано';
}

/**
 * Уведомление после возврата записей из архива при вставке — одно на вставку ([UI-71]); `null` —
 * возвращать было нечего. Частичный возврат — ошибкой, и в ней же названо вернувшееся, а не вторым
 * уведомлением за ней.
 */
export function unarchiveNotice(returned: number, failed: number): { variant: 'success' | 'error'; text: string } | null {
  if (failed > 0) {
    const missed = `Не удалось вернуть из архива: ${failed}. Эти значения вставлены встроенно.`;
    return { variant: 'error', text: returned > 0 ? `Возвращено из архива: ${returned}. ${missed}` : missed };
  }
  return returned > 0 ? { variant: 'success', text: `Возвращено из архива: ${returned}.` } : null;
}
