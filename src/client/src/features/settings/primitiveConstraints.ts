import type { FieldConstraints, PrimitiveTypeDef } from '@/shared/api/types';

/**
 * Ограничения своего базового типа. При создании стрелка по «Базовому типу» выбирает сразу ([UI-68]), и
 * смена типа введённое не стирает: человек может вернуться. Сохраняются только свои — чужие типу сервер
 * не ждёт.
 */
const CONSTRAINT_KEYS: Record<PrimitiveTypeDef['baseType'], readonly (keyof FieldConstraints)[]> = {
  string: ['pattern', 'patternMessage', 'minLength', 'maxLength'],
  number: ['min', 'max', 'integer'],
  date: ['minDate', 'maxDate', 'datePrecision'],
};
export function constraintsOf(baseType: PrimitiveTypeDef['baseType'], c: FieldConstraints): FieldConstraints {
  return Object.fromEntries(Object.entries(c)
    .filter(([k]) => CONSTRAINT_KEYS[baseType].includes(k as keyof FieldConstraints))) as FieldConstraints;
}
