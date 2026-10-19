import type { SchemaField } from '@/shared/api/schema';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { parseNumber } from '@/shared/utils/parseNumber';
import { dateMask } from '@/shared/format/dateMask';
import { resolveLocale } from '@/shared/hooks/useLocale';

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

const ISO = /^(\d{4})-(\d{1,2})-(\d{1,2})$/;
const THREE = /^(\d{1,4})[./-](\d{1,4})[./-](\d{1,4})$/;

/**
 * Дата → хранимый вид `YYYY-MM-DD`. Принимаются дата в порядке языка пользователя (issue #1461:
 * `ДД.ММ.ГГГГ` у ru-RU, `MM/DD/YYYY` у en-US; разделитель — точка, дробь или дефис) и готовый ISO.
 * Порядок — только из языка, по значению не угадывается: «01.02.2026» — это 1 февраля у ru-RU и
 * 2 января у en-US, и угадывать здесь значило бы молча путать месяц с днём.
 * Двузначный год не принимается намеренно: `15.01.26` — это и 1926-й, и 2026-й.
 */
export function parseDateCell(raw: string, locale = 'ru-RU'): string | null {
  const t = raw.trim();
  const iso = ISO.exec(t);
  let y: number, m: number, d: number;
  if (iso) {
    [y, m, d] = [Number(iso[1]), Number(iso[2]), Number(iso[3])];
  } else {
    const g = THREE.exec(t);
    if (!g) return null;
    const v = Object.fromEntries(dateMask(locale).order.map((p, i) => [p, g[i + 1]]));
    if (v.y.length !== 4 || v.d.length > 2 || v.m.length > 2) return null;
    [y, m, d] = [Number(v.y), Number(v.m), Number(v.d)];
  }
  // Календарную несуразицу (`31.02.2026`) ловим сверкой с самой датой: подставлять 3 марта нельзя.
  const dt = new Date(Date.UTC(y, m - 1, d));
  if (dt.getUTCFullYear() !== y || dt.getUTCMonth() !== m - 1 || dt.getUTCDate() !== d) return null;
  return `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
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
      if (base === 'date') return parseDateCell(raw, resolveLocale(ctx.locale));
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
  if (type === 'date') return `не дата (ожидается ${dateMask(resolveLocale(ctx.locale)).text})`;
  if (type === 'boolean') return 'не «да» и не «нет»';
  if (type === 'enum') return 'нет такого варианта в списке';
  return 'не разобрано';
}
