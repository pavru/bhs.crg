import type { RefObject } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { SearchOff } from '@/shared/ui/icons';
import type { FilterCondition } from '@/shared/api/types';
import { conditionNotice, missActions, missText, noticeText } from './conditionNotice';
import { choiceStrays, conditionProblem, type FilterColumn } from './rowFilterModel';

/**
 * Строка под условием — одна на окошко чипа и строку расширенного диалога (UI-56): проблема («не
 * выполнится», красным) или, если её нет, пометка «не найдёт» — тоном предупреждения, со значком `BookX`
 * (не `TriangleAlert`: тот у проблемы) и действиями замены. Вид — вариант А макета «Подсказка стройки в
 * отборе» (выбор владельца 24.10.2026).
 *
 * <p>Пометка — не тревога: без `role="alert"` и без живой области; поле связано с ней `aria-describedby`
 * (`id`). Пока открыт список подсказки, её не видно — человек ещё выбирает: для этого родитель условия
 * несёт класс `group/condition`. После замены фокус возвращается в поле значения (`fieldRef`).</p>
 *
 * <p>Значения вне закрытого перечня колонки-выбора здесь не повторяются: о них тоном пометки говорит сам
 * список отметок (`OptionList`), и красная строка о том же значении рядом с жёлтой противоречила бы ей.</p>
 */
export function ConditionHint({ cond, columns, onChange, id, fieldRef, className = '', problemOf = conditionProblem }: {
  cond: FilterCondition;
  columns: FilterColumn[];
  /** Чем искать проблему: у чипа таблицы сверх общих правил — колонка, которой в таблице нет (`chipProblem`). */
  problemOf?: (cond: FilterCondition, columns: FilterColumn[]) => string | null;
  onChange: (c: FilterCondition) => void;
  /** Тот же `id`, что ушёл полю значения в `aria-describedby`. */
  id?: string;
  /** Поле значения — в него возвращается фокус после замены. */
  fieldRef?: RefObject<HTMLInputElement | null>;
  className?: string;
}) {
  const column = columns.find(c => c.name === cond.column);
  const strays = choiceStrays(cond, column);
  const rest = strays.length > 0 ? { ...cond, values: (cond.values ?? []).filter(v => !strays.includes(v)) } : cond;
  // Остались одни значения вне перечня — сказать сверх списка отметок нечего.
  const problem = strays.length > 0 && rest.values?.length === 0 ? null : problemOf(rest, columns);
  if (problem) return <p className={`${className} text-body-small text-error`}>Похоже, условие не выполнится: {problem}.</p>;

  const notice = conditionNotice(cond, column);
  if (!notice) return null;
  const lines = notice.kind === 'pattern'
    ? [{ key: '', text: noticeText(notice), actions: [] }]
    : notice.misses.map(m => ({ key: m.value, text: `«${m.value}» — ${missText(m)}`, actions: missActions(cond, m) }));

  function act(next: FilterCondition) {
    onChange(next);
    fieldRef?.current?.focus();
  }

  return (
    <div id={id} className={`${className} space-y-0.5 text-body-small text-warning group-has-[[aria-expanded=true]]/condition:hidden`}>
      {lines.map(line => (
        <p key={line.key} className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
          {/* Значок — с текстом, а не отдельным переносом: окошко чипа узкое. */}
          <span className="inline-flex items-start gap-1">
            <Icon icon={SearchOff} size={18} aria-hidden className="mt-0.5 shrink-0" />
            <span>{line.text}</span>
          </span>
          {line.actions.map(a => (
            <button key={a.label} type="button" onClick={() => act(a.next)} className="text-primary hover:underline">
              {a.label}
            </button>
          ))}
        </p>
      ))}
    </div>
  );
}
