import type { FieldConstraints, PrimitiveTypeDef } from '@/shared/api/types';

/**
 * Ограничения своего базового типа. При создании стрелка по «Базовому типу» выбирает сразу ([UI-68]), и
 * смена типа введённое не стирает: человек может вернуться. Сохраняются только свои — чужие типу сервер
 * не ждёт.
 */
type BaseType = PrimitiveTypeDef['baseType'];

/**
 * Чей ключ — по каждому ключу `FieldConstraints`, а не списком по типу: новое ограничение без строки здесь
 * не компилируется, а не выбрасывается молча при создании (ревью PR #1709).
 */
const OWNER: { [K in keyof Required<FieldConstraints>]: BaseType } = {
  pattern: 'string', patternMessage: 'string', minLength: 'string', maxLength: 'string',
  min: 'number', max: 'number', integer: 'number',
  minDate: 'date', maxDate: 'date', datePrecision: 'date',
};
export function constraintsOf(baseType: BaseType, c: FieldConstraints): FieldConstraints {
  return Object.fromEntries(Object.entries(c)
    .filter(([k]) => OWNER[k as keyof FieldConstraints] === baseType)) as FieldConstraints;
}
