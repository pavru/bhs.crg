import type { MouseEvent } from 'react';
import { BookX } from 'lucide-react';
import type { FilterCondition } from '@/shared/api/types';
import { conditionNotice, missActions, missText, NONE_FITS } from './conditionNotice';
import { conditionProblem, type FilterColumn } from './rowFilterModel';

/**
 * Строка под условием — одна на окошко чипа и строку расширенного диалога (UI-56): проблема («не
 * выполнится», красным) или, если её нет, пометка «не найдёт» — тоном предупреждения, со значком `BookX`
 * (не `TriangleAlert`: тот у проблемы) и действиями замены. Вид — вариант А макета «Подсказка стройки в
 * отборе» (выбор владельца 24.10.2026).
 *
 * <p>Пометка — не тревога: без `role="alert"` и без живой области; поле связано с ней `aria-describedby`
 * (`id`). Пока открыт список подсказки, её не видно — человек ещё выбирает. Для этого родитель условия
 * несёт класс `group/condition` и признак `data-condition`: после замены фокус возвращается в поле.</p>
 */
export function ConditionHint({ cond, columns, onChange, id, className = '', problemOf = conditionProblem }: {
  cond: FilterCondition;
  columns: FilterColumn[];
  /** Чем искать проблему: у чипа таблицы сверх общих правил — колонка, которой в таблице нет (`chipProblem`). */
  problemOf?: (cond: FilterCondition, columns: FilterColumn[]) => string | null;
  onChange: (c: FilterCondition) => void;
  /** Тот же `id`, что ушёл полю значения в `aria-describedby`. */
  id?: string;
  className?: string;
}) {
  const problem = problemOf(cond, columns);
  if (problem) return <p className={`${className} text-xs text-danger`}>Похоже, условие не выполнится: {problem}.</p>;

  const notice = conditionNotice(cond, columns.find(c => c.name === cond.column));
  if (!notice) return null;
  const lines = notice.kind === 'pattern'
    ? [{ key: '', text: `${NONE_FITS[0].toUpperCase()}${NONE_FITS.slice(1)}`, actions: [] }]
    : notice.misses.map(m => ({ key: m.value, text: `«${m.value}» — ${missText(m)}`, actions: missActions(cond, m) }));

  function act(e: MouseEvent<HTMLButtonElement>, next: FilterCondition) {
    const field = e.currentTarget.closest('[data-condition]')?.querySelector<HTMLInputElement>('input[role="combobox"]');
    onChange(next);
    field?.focus();
  }

  return (
    <div id={id} className={`${className} space-y-0.5 text-xs text-warning group-has-[[aria-expanded=true]]/condition:hidden`}>
      {lines.map(line => (
        <p key={line.key} className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
          {/* Значок — с текстом, а не отдельным переносом: окошко чипа узкое. */}
          <span className="inline-flex items-start gap-1">
            <BookX size={12} aria-hidden className="mt-0.5 shrink-0" />
            <span>{line.text}</span>
          </span>
          {line.actions.map(a => (
            <button key={a.label} type="button" onClick={e => act(e, a.next)} className="text-brand hover:underline">
              {a.label}
            </button>
          ))}
        </p>
      ))}
    </div>
  );
}
