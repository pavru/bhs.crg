import type { SchemaField } from '@/shared/api/schema';
import { SUGGEST_REFUSAL_TEXT, suggestReason } from '@/shared/api/suggestable';
import { FUNCTIONAL_TAG, tagCode } from '@/shared/api/tags';
import { Checkbox } from '@/shared/ui/Checkbox';
import { StatusBadge } from '@/shared/ui/StatusBadge';

type SuggestFields = Pick<SchemaField, 'type' | 'computed' | 'locked' | 'tags'>;

/**
 * Флажок «Подсказывать введённое ранее» в карточке поля редактора типа (issue #1533, ТЗ TYPE-2) —
 * вариант В макета «Флажок подсказки в карточке поля» (выбор владельца 23.10.2026).
 *
 * <p>Хранится тэгом `input.noSuggest`: снят — тэг стоит. Свойства поля «подсказывать» нет — умолчание
 * вычисляет правило `Suggestable`, и по его коду здесь три исхода: флажок (`suggested`, `manual`),
 * ничего (`type`, `computed`) или строка причины вместо флажка (однозначные и запертые поля).</p>
 */
export function SuggestFlag({ field, onChange }: {
  field: SuggestFields;
  onChange: (patch: Pick<SchemaField, 'tags'>) => void;
}) {
  const reason = suggestReason(field);
  if (reason === 'type' || reason === 'computed') return null;
  if (reason !== 'suggested' && reason !== 'manual')
    return <p className="text-caption text-fg3">{SUGGEST_REFUSAL_TEXT[reason]}</p>;

  const tags = field.tags ?? [];
  function toggle(on: boolean) {
    const next = on ? tags.filter(t => tagCode(t) !== FUNCTIONAL_TAG.inputNoSuggest) : [...tags, FUNCTIONAL_TAG.inputNoSuggest];
    onChange({ tags: next.length ? next : undefined });
  }
  return (
    <Checkbox size="sm" checked={reason === 'suggested'} onChange={toggle}
      label={<span className="text-xs text-on-surface-variant">Подсказывать введённое ранее</span>}
      description="Поле предлагает значения, введённые в нём в других записях. Снимите, если значения уникальны" />
  );
}

/**
 * Пометка «без подсказки» в шапке карточки (вариант В): чипа тэга `input.noSuggest` нет, и без неё
 * снятый флажок не был бы виден, пока карточку не раскрыли.
 */
export function SuggestOffBadge({ field }: { field: SuggestFields }) {
  return suggestReason(field) === 'manual' ? <StatusBadge tone="quiet">без подсказки</StatusBadge> : null;
}
