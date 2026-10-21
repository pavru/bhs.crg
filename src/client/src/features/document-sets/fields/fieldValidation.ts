import { ruToISO, truncISO } from '@/shared/utils/date';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, FieldConstraints } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import { dateMaskText, formatDateInMask, formatLocale } from '@/shared/format/format';
import { parseDateCell } from './pasteCoerce';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export function isMissing(field: SchemaField, val: unknown): boolean {
  if (!field.required) return false;
  // Поле с замком (ТЗ CORE-20.2) заполняет код модуля, и форма не даёт его править. Требовать
  // заполнить то, чего заполнить нельзя, — претензия без пути: человек её не снимет никогда.
  // Здесь, а не в каждой из трёх форм по схеме: обязательность они спрашивают ровно отсюда.
  if (field.locked) return false;
  if (field.type === 'boolean') return false;
  if (field.type === 'complex') return false;
  if (isFieldRef(val)) return false;
  return val == null || String(val).trim() === '';
}

/**
 * Образец, набранный текстом (проверка ограничений в настройке типа), — тем же разбором, что
 * вставка в таблицу: дату в маске языка пользователя (issue #1461) и с точностью типа — «10.2026» у
 * «месяц и год» (ревью PR #1544). Сравнение с пределами идёт по ГГГГ-ММ-ДД, и «10/18/2026» как текст
 * сравнилось бы неверно.
 */
export function validateSample(sample: string, def: PrimitiveTypeDef): string | null {
  if (def.baseType !== 'date') return validateConstraint(sample, def);
  const precision = def.constraints.datePrecision ?? 'day';
  const iso = parseDateCell(sample, formatLocale(), precision);
  return iso ? validateConstraint(iso, def) : `Не дата (ожидается ${dateMaskText(precision)})`;
}

export function validateConstraint(value: unknown, def: PrimitiveTypeDef): string | null {
  if (value == null || value === '') return null;
  const c: FieldConstraints = def.constraints;
  if (def.baseType === 'string') {
    const str = String(value);
    if (c.pattern) {
      try {
        if (!new RegExp(c.pattern).test(str))
          return c.patternMessage ?? `Не соответствует формату: ${c.pattern}`;
      } catch { /* invalid regex */ }
    }
    if (c.minLength != null && str.length < c.minLength) return `Мин. длина: ${c.minLength} симв.`;
    if (c.maxLength != null && str.length > c.maxLength) return `Макс. длина: ${c.maxLength} симв.`;
  } else if (def.baseType === 'number') {
    const num = Number(value);
    if (isNaN(num)) return 'Введите число';
    if (c.integer && !Number.isInteger(num)) return 'Введите целое число';
    if (c.min != null && num < c.min) return `Мин. значение: ${c.min}`;
    if (c.max != null && num > c.max) return `Макс. значение: ${c.max}`;
  } else if (def.baseType === 'date') {
    const iso = ruToISO(String(value));
    const prec = c.datePrecision ?? 'day';
    // Предел — в записи маски поля, под которым стоит сообщение (ревью PR #1544): у en-US
    // «01.07.2026» под полем MM/DD/YYYY читалось бы как 7 января.
    // Сравнение — по точности (#1464): при min 15.07.2026 и точности «месяц» 07.2026 допустим —
    // сообщение «не ранее 07.2026» под ним противоречило бы само себе.
    if (c.minDate && truncISO(iso, prec) < truncISO(c.minDate, prec)) return `Дата не ранее ${formatDateInMask(c.minDate, prec)}`;
    if (c.maxDate && truncISO(iso, prec) > truncISO(c.maxDate, prec)) return `Дата не позднее ${formatDateInMask(c.maxDate, prec)}`;
  }
  return null;
}
