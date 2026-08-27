import { formatDateRu, ruToISO } from '@/shared/utils/date';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, FieldConstraints } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export function isMissing(field: SchemaField, val: unknown): boolean {
  if (!field.required) return false;
  if (field.type === 'boolean') return false;
  if (field.type === 'complex') return false;
  if (isFieldRef(val)) return false;
  return val == null || String(val).trim() === '';
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
    if (c.minDate && iso < c.minDate) return `Дата не ранее ${formatDateRu(c.minDate, prec)}`;
    if (c.maxDate && iso > c.maxDate) return `Дата не позднее ${formatDateRu(c.maxDate, prec)}`;
  }
  return null;
}

/**
 * Значение для календарного контрола: тот понимает только полный ISO и на «12.05.2024» показывает
 * ПУСТОЕ поле, хотя значение есть и уйдёт обратно в базу при сохранении.
 *
 * Русскую дату в поле даты кладёт распознавание (модель отдаёт то, что прочла в скане) и старые
 * записи, заполненные тогда, когда поле рисовалось простым текстом. Показать пусто вместо «12.05.2024»
 * значит спрятать расхождение: человек его не исправит, а аудит значений (#644) будет ругаться на то,
 * чего в форме не видно. Хранимое значение при этом НЕ переписываем — только показываем; правкой
 * контрол сам запишет полный ISO.
 */
