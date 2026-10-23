import type { SchemaField } from './schema';
import { FUNCTIONAL_TAG, hasTag } from './tags';

/**
 * Подсказывается ли поле значениями, введёнными ранее (issue #1529) — зеркало серверного `Suggestable`.
 *
 * Истина — на сервере; здесь копия, чтобы поле формы не звало адрес подсказок зря, а редактор типа
 * показывал причину вместо флажка. Расхождение двух копий ловит общий файл `suggestableCases.json`:
 * его считают и `suggestable.test.ts`, и `SuggestableCasesTests` на сервере.
 */
export const SUGGEST_REASONS = [
  'suggested',
  'type', 'computed', 'locked',
  'identity', 'inn', 'docNumber', 'personnelNumber', 'article', 'orgName', 'account',
  'manual',
] as const;

export type SuggestReason = (typeof SUGGEST_REASONS)[number];

/**
 * Тэги однозначности В ПОРЯДКЕ проверки — у поля с несколькими код первого из них. Значение у каждой
 * записи своё: подсказка чужого вела бы к дублю номера, ИНН или артикула.
 */
const UNIQUE_TAGS: readonly (readonly [string, SuggestReason])[] = [
  [FUNCTIONAL_TAG.identity, 'identity'],
  [FUNCTIONAL_TAG.orgInn, 'inn'],
  [FUNCTIONAL_TAG.docNumber, 'docNumber'],
  [FUNCTIONAL_TAG.employeePersonnelNumber, 'personnelNumber'],
  [FUNCTIONAL_TAG.nomenclatureArticle, 'article'],
  [FUNCTIONAL_TAG.orgName, 'orgName'],
  [FUNCTIONAL_TAG.employeeAccount, 'account'],
];

/** Код причины; порядок проверок: вид → вычисляемое → запертое → однозначность → выключено вручную. */
export function suggestReason(field: Pick<SchemaField, 'type' | 'computed' | 'locked' | 'tags'>): SuggestReason {
  if (field.type !== 'string') return 'type';
  if (field.computed) return 'computed';
  if (field.locked) return 'locked';

  const unique = UNIQUE_TAGS.find(([tag]) => hasTag(field.tags, tag));
  if (unique) return unique[1];

  return hasTag(field.tags, FUNCTIONAL_TAG.inputNoSuggest) ? 'manual' : 'suggested';
}
