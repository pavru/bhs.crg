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
  'inn', 'docNumber', 'personnelNumber', 'article', 'orgName', 'account',
  'manual',
] as const;

export type SuggestReason = (typeof SUGGEST_REASONS)[number];

/**
 * Тэги однозначности В ПОРЯДКЕ проверки — у поля с несколькими код первого из них. Значение у каждой
 * записи своё: подсказка чужого вела бы к дублю номера, ИНН или артикула.
 *
 * `identity` здесь нет (решение 23.10.2026, issue #1533): компонент составного ключа сам по себе не
 * уникален — «Производитель» повторяется у многих позиций.
 */
const UNIQUE_TAGS: readonly (readonly [string, SuggestReason])[] = [
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

/** Коды, у которых в редакторе типа вместо флажка стоит строка причины (issue #1533). */
export type SuggestRefusal = Exclude<SuggestReason, 'suggested' | 'manual' | 'type' | 'computed'>;

/**
 * Почему поле не подсказывается — словами для редактора типа. `type` и `computed` строки не получают:
 * у многострочного текста и формулы подсказки не ждут, и объяснение было бы шумом в каждой карточке.
 */
export const SUGGEST_REFUSAL_TEXT: Record<SuggestRefusal, string> = {
  locked: 'Не подсказывается: значение кладёт модуль, поле заперто.',
  inn: 'Не подсказывается: ИНН у каждой организации свой.',
  docNumber: 'Не подсказывается: номер у каждого документа свой.',
  personnelNumber: 'Не подсказывается: табельный номер у каждого сотрудника свой.',
  article: 'Не подсказывается: артикул у каждой позиции свой — чужой привёл бы к дублю.',
  orgName: 'Не подсказывается: чужое название организации ведёт к дублю записи справочника.',
  account: 'Не подсказывается: учётная запись привязывается к одному сотруднику.',
};
