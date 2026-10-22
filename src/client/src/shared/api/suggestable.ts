import type { SchemaField } from './schema';
import { FUNCTIONAL_TAG, tagCode } from './tags';

/**
 * Подсказывается ли поле значениями, введёнными ранее (issue #1529) — зеркало серверного `Suggestable`.
 *
 * Истина — на сервере; здесь копия, чтобы поле формы не звало адрес подсказок зря, а редактор типа
 * показывал причину вместо флажка. Расхождение двух копий ловит общий файл `suggestableCases.json`:
 * его считают и `suggestable.test.ts`, и `SuggestableCasesTests` на сервере.
 */
export type SuggestReason =
  | 'suggested'
  | 'type' | 'computed' | 'locked'
  | 'identity' | 'inn' | 'docNumber' | 'personnelNumber' | 'article' | 'orgName'
  | 'manual';

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
];

/** Код причины; порядок проверок: вид → вычисляемое → запертое → однозначность → выключено вручную. */
export function suggestReason(field: Pick<SchemaField, 'type' | 'computed' | 'locked' | 'tags'>): SuggestReason {
  if (field.type !== 'string') return 'type';
  if (field.computed) return 'computed';
  if (field.locked) return 'locked';

  const codes = new Set((field.tags ?? []).map(tagCode));
  for (const [tag, reason] of UNIQUE_TAGS)
    if (codes.has(tag)) return reason;

  return codes.has(FUNCTIONAL_TAG.inputNoSuggest) ? 'manual' : 'suggested';
}
