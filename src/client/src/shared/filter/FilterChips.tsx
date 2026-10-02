import { useState, type ReactNode } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Plus, SlidersHorizontal, TriangleAlert, X } from 'lucide-react';
import type { FilterCondition, FilterGroup, FilterNode } from '@/shared/api/types';
import { ConditionEditor } from './ConditionEditor';
import { chipProblem, chipsView, chipText, withChip, withoutChip } from './chipsModel';
import { type FilterColumn } from './rowFilterModel';

/**
 * Чипы отбора над таблицей (ТЗ CORE-33; задача G1d, issue #1091): каждое условие — чип, который
 * называет колонку, оператор и значение, правится на месте и снимается одним действием.
 *
 * Чипы — лицо того же дерева условий, что правит расширенный режим: `onChange` отдаёт дерево, и
 * снятие чипа меняет сам отбор, а не только его вид. Значение вводится полем по виду колонки — у
 * колонки-выбора списком её значений, у срока календарём; набрать строку мимо вида нечем.
 *
 * Отбор, который рядом чипов не прочитать («ИЛИ», вложенные группы), чипами не подменяется: он назван
 * сложным и открывается в расширенном режиме. Негодное условие остаётся на экране чипом с причиной.
 */
export function FilterChips({ columns, filter, onChange, onAdvanced }: {
  /** Колонки таблицы с видами, операторами и перечнями — как их прислал сервер. */
  columns: FilterColumn[];
  filter: FilterNode | null;
  onChange: (filter: FilterGroup | null) => void;
  /** Открыть расширенный режим — то же дерево целиком. Нет — кнопки нет. */
  onAdvanced?: () => void;
}) {
  const view = chipsView(filter);
  const conditions = view.mode === 'chips' ? view.conditions : [];
  const problems = conditions
    .map(c => ({ text: chipText(c, columns), problem: chipProblem(c, columns) }))
    .filter(p => p.problem !== null);
  const hasAny = view.mode === 'complex' || conditions.length > 0;

  return (
    <div>
      <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Условия отбора">
        {view.mode === 'complex' ? (
          <span className={`${CHIP_CLS} border-stroke text-fg1`}>
            <span className="pl-2.5 pr-1 py-1">Отбор сложный: условий — {view.count}</span>
            {onAdvanced && (
              <button type="button" onClick={onAdvanced}
                className="pr-2.5 py-1 text-brand hover:underline">
                изменить
              </button>
            )}
          </span>
        ) : conditions.map((cond, i) => (
          <Chip key={`${i}:${cond.column}:${cond.op}`} cond={cond} columns={columns}
            onChange={next => onChange(withChip(conditions, next, i))}
            onRemove={() => onChange(withoutChip(conditions, i))} />
        ))}

        {view.mode === 'chips' && (
          <ConditionPopover columns={columns} initial={NEW_CONDITION} submit="Добавить"
            onSubmit={cond => onChange(withChip(conditions, cond))}>
            <button type="button" className={`${CHIP_CLS} border-dashed border-stroke text-fg3 hover:text-fg1 px-2.5 py-1 gap-1`}>
              <Plus size={12} aria-hidden /> условие
            </button>
          </ConditionPopover>
        )}

        {onAdvanced && view.mode === 'chips' && (
          <button type="button" onClick={onAdvanced}
            className="inline-flex items-center gap-1 text-xs text-fg3 hover:text-fg1 px-1.5 py-1">
            <SlidersHorizontal size={12} aria-hidden /> Расширенный
          </button>
        )}

        {hasAny && (
          <button type="button" onClick={() => onChange(null)}
            className="text-xs text-fg3 hover:text-danger px-1.5 py-1">
            Снять отбор
          </button>
        )}
      </div>

      {problems.length > 0 && (
        <ul role="alert" className="mt-1 text-xs text-danger">
          {problems.map((p, i) => <li key={i}>«{p.text}» не выполнится: {p.problem}.</li>)}
        </ul>
      )}
    </div>
  );
}

const CHIP_CLS = 'inline-flex items-center rounded-full border text-xs bg-surface';

const NEW_CONDITION: FilterCondition = { type: 'condition', column: '', op: 'eq', value: '' };

/** Один чип: текст открывает правку, крестик снимает условие. */
function Chip({ cond, columns, onChange, onRemove }: {
  cond: FilterCondition;
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  onRemove: () => void;
}) {
  const text = chipText(cond, columns);
  const problem = chipProblem(cond, columns);

  return (
    <span className={`${CHIP_CLS} ${problem ? 'border-danger text-danger' : 'border-stroke text-fg1'}`}
      title={problem ? `Условие не выполнится: ${problem}` : undefined}>
      <ConditionPopover columns={columns} initial={cond} submit="Применить" onSubmit={onChange}>
        <button type="button" className="inline-flex items-center gap-1 pl-2.5 pr-1 py-1 rounded-l-full hover:bg-muted">
          {problem && <TriangleAlert size={12} aria-hidden />}
          {text}
        </button>
      </ConditionPopover>
      <button type="button" onClick={onRemove} aria-label={`Снять условие «${text}»`}
        className="pl-0.5 pr-2 py-1 rounded-r-full text-fg4 hover:text-danger">
        <X size={12} aria-hidden />
      </button>
    </span>
  );
}

/**
 * Окошко условия — одно на «добавить» и на правку чипа. Условие правится в черновике и уходит в
 * отбор одним действием: запрос к таблице идёт на каждое изменение отбора, и слать его на каждую
 * набранную букву значения незачем.
 */
function ConditionPopover({ columns, initial, submit, onSubmit, children }: {
  columns: FilterColumn[];
  initial: FilterCondition;
  submit: string;
  onSubmit: (c: FilterCondition) => void;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(initial);
  // Подсказка, а не запрет — как в диалоге (issue #1137): годно ли условие, решает сервер, а его
  // отказ экран покажет на чипе.
  const problem = chipProblem(draft, columns);

  function toggle(next: boolean) {
    // Открыли заново — начинаем с того, что стоит в отборе, а не с брошенного черновика.
    if (next) setDraft(initial);
    setOpen(next);
  }

  function apply() {
    onSubmit(draft);
    setOpen(false);
  }

  return (
    <Popover.Root open={open} onOpenChange={toggle}>
      <Popover.Trigger asChild>{children}</Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6}
          className="z-50 w-72 rounded-lg bg-surface border border-stroke p-3 focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <form onSubmit={e => { e.preventDefault(); apply(); }}>
            <ConditionEditor stacked cond={draft} columns={columns} onChange={setDraft} />
            {problem && <p className="mt-1.5 text-xs text-danger">Похоже, условие не выполнится: {problem}.</p>}
            <div className="mt-2.5 flex justify-end">
              <button type="submit" disabled={!draft.column.trim()}
                className="px-3 py-1 rounded-md text-xs font-medium text-white bg-brand disabled:opacity-50">
                {submit}
              </button>
            </div>
          </form>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
