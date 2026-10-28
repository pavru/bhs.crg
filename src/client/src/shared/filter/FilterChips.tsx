import { useId, useRef, useState, type ComponentProps, type ReactNode } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Icon } from '@/shared/ui/Icon';
import { Add, SearchOff, Tune, Warning } from '@/shared/ui/icons';
import type { FilterCondition, FilterNode } from '@/shared/api/types';
import { ConditionEditor } from './ConditionEditor';
import { ConditionHint } from './ConditionHint';
import { conditionNotice, noticeText } from './conditionNotice';
import {
  chipAdded, chipProblem, chipRemoved, chipReplaced, chipsView, chipText, conditionEntered, offeredColumns,
  offeredCondition, type FilterChange,
} from './chipsModel';
import { columnLabel, type FilterColumn } from './rowFilterModel';
import { Chip } from '@/shared/ui/Chip';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Чипы отбора над таблицей (ТЗ CORE-33; задача G1d, issue #1091): каждое условие — чип, который
 * называет колонку, оператор и значение, правится на месте и снимается одним действием.
 *
 * Чипы — лицо того же дерева условий, что правит расширенный режим, и снятие чипа меняет сам отбор,
 * а не только его вид. `onChange` отдаёт ИЗМЕНЕНИЕ отбора, а не готовое дерево (`FilterChange`): ряд
 * нарисован по отбору прошлой отрисовки, и применять правку положено к тому, что стоит сейчас. Значение вводится полем по виду колонки — у
 * колонки-выбора списком её значений, у срока календарём; набрать строку мимо вида нечем.
 *
 * Отбор, который рядом чипов не прочитать («ИЛИ», вложенные группы), чипами не подменяется: он назван
 * сложным и открывается в расширенном режиме. Негодное условие остаётся на экране чипом с причиной.
 */
export function FilterChips({ columns, filter, onChange, onAdvanced, suggested }: {
  /** Колонки таблицы с видами, операторами и перечнями — как их прислал сервер. */
  columns: FilterColumn[];
  filter: FilterNode | null;
  onChange: (change: FilterChange) => void;
  /** Открыть расширенный режим — то же дерево целиком. Нет — кнопки нет. */
  onAdvanced?: () => void;
  /**
   * Колонки, по которым отбор ПРЕДЛАГАЕТСЯ готовым местом под условие — их называет готовое
   * представление (G4, issue #1097): «Поставщик», «Состояние оплаты». Место — не условие: отбор не
   * ставится, пока человек не ввёл значение. Колонка, по которой условие уже стоит, местом не
   * предлагается — оно правится чипом.
   */
  suggested?: string[];
}) {
  const view = chipsView(filter);
  const conditions = view.mode === 'chips' ? view.conditions : [];
  const offered = view.mode === 'chips' ? offeredColumns(suggested, columns, conditions) : [];
  const problems = conditions
    .map(c => ({ text: chipText(c, columns), problem: chipProblem(c, columns) }))
    .filter(p => p.problem !== null);
  const hasAny = view.mode === 'complex' || conditions.length > 0;

  return (
    <div>
      <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Условия отбора">
        {view.mode === 'complex' ? (
          <span className={`${CHIP_CLS} border-outline-variant text-on-surface`}>
            <span className="pl-2.5 pr-1 py-1">Отбор сложный: условий — {view.count}</span>
            {onAdvanced && (
              <button type="button" onClick={onAdvanced}
                className="pr-2.5 py-1 text-primary hover:underline">
                изменить
              </button>
            )}
          </span>
        ) : conditions.map((cond, i) => (
          <ConditionChip key={`${i}:${cond.column}:${cond.op}`} cond={cond} columns={columns}
            onChange={next => onChange(chipReplaced(cond, next))}
            onRemove={() => onChange(chipRemoved(cond))} />
        ))}

        {offered.map(name => (
          <ConditionPopover key={`offer:${name}`} columns={columns} submit="Добавить" requireValue
            initial={offeredCondition(name, columns)}
            onSubmit={cond => onChange(chipAdded(cond))}>
            <button type="button" className={`${CHIP_CLS} border-dashed border-outline-variant text-fg3 hover:text-on-surface px-2.5 py-1 gap-1`}>
              <Icon icon={Add} size={18} aria-hidden /> {columnLabel(columns.find(c => c.name === name), name)}
            </button>
          </ConditionPopover>
        ))}

        {view.mode === 'chips' && (
          <ConditionPopover columns={columns} initial={NEW_CONDITION} submit="Добавить"
            onSubmit={cond => onChange(chipAdded(cond))}>
            <button type="button" className={`${CHIP_CLS} border-dashed border-outline-variant text-fg3 hover:text-on-surface px-2.5 py-1 gap-1`}>
              <Icon icon={Add} size={18} aria-hidden /> условие
            </button>
          </ConditionPopover>
        )}

        {onAdvanced && view.mode === 'chips' && (
          <button type="button" onClick={onAdvanced}
            className="inline-flex items-center gap-1 text-xs text-fg3 hover:text-on-surface px-1.5 py-1">
            <Icon icon={Tune} size={18} aria-hidden /> Расширенный
          </button>
        )}

        {hasAny && (
          <button type="button" onClick={() => onChange(() => null)}
            className="text-xs text-fg3 hover:text-error px-1.5 py-1">
            Снять отбор
          </button>
        )}
      </div>

      {problems.length > 0 && (
        <ul role="alert" className="mt-1 text-xs text-error">
          {problems.map((p, i) => <li key={i}>«{p.text}» не выполнится: {p.problem}.</li>)}
        </ul>
      )}
    </div>
  );
}

const CHIP_CLS = 'inline-flex items-center rounded-full border text-xs bg-surface';

const NEW_CONDITION: FilterCondition = { type: 'condition', column: '', op: 'eq', value: '' };

/**
 * Один чип: текст открывает правку, крестик снимает условие. Условие, которое не выполнится, — красным
 * со значком предупреждения; условие, которое выполнится, но не найдёт (UI-56), — целиком в тоне
 * предупреждения со значком справочника и подсказкой, без действия: замена — в окошке чипа. Значки
 * разные, а не только цвет. Пилюлю и крестик рисует `Chip` (#1469): крестик снятия — его `onRemove`.
 */
function ConditionChip({ cond, columns, onChange, onRemove }: {
  cond: FilterCondition;
  columns: FilterColumn[];
  onChange: (c: FilterCondition) => void;
  onRemove: () => void;
}) {
  const text = chipText(cond, columns);
  const problem = chipProblem(cond, columns);
  const notice = problem ? null : conditionNotice(cond, columns.find(c => c.name === cond.column));
  // Подсказка — у триггера поповера целиком (docs/UI.md, «Подсказка»): `ConditionPopover` отдаёт ему
  // то, что пришло от `Tooltip`, а `Chip` — своей кнопке-тексту. Годное условие — в цвете бренда
  // («отбор стоит»), `menu` — чип открывает окошко и нажатым не бывает. Пометка — заливкой поверх
  // тона (`bg-…!`, как пометка поверх рамки поля): тон `warning` у Chip без заливки, а пометка обязана
  // читаться целиком, а не одной рамкой.
  return (
    <Tooltip content={problem ? `Условие не выполнится: ${problem}` : notice ? noticeText(notice) : undefined}>
      <ConditionPopover columns={columns} initial={cond} submit="Применить" onSubmit={onChange}>
        <Chip menu selected={!problem && !notice} tone={problem ? 'danger' : notice ? 'warning' : undefined}
          icon={problem ? Warning : undefined} className={notice ? 'bg-warning-container!' : ''}
          data-notice={notice ? '' : undefined}
          onRemove={onRemove} removeLabel={`Снять условие «${text}»`}>
          {notice && <Icon icon={SearchOff} size={18} aria-label="нет в справочнике" className="inline align-[-2px] mr-1" />}
          {text}
        </Chip>
      </ConditionPopover>
    </Tooltip>
  );
}

/**
 * Окошко условия — одно на «добавить» и на правку чипа. Условие правится в черновике и уходит в
 * отбор одним действием: запрос к таблице идёт на каждое изменение отбора, и слать его на каждую
 * набранную букву значения незачем.
 *
 * Прочие пропсы (`trigger`) уходят триггеру: так `Tooltip` оборачивает окошко целиком, а его ref и
 * обработчики доходят до кнопки чипа.
 */
function ConditionPopover({ columns, initial, submit, onSubmit, requireValue, children, ...trigger }: {
  columns: FilterColumn[];
  initial: FilterCondition;
  submit: string;
  onSubmit: (c: FilterCondition) => void;
  /**
   * Без значения условие не добавляется — так у предложенного места: колонка в нём уже названа, и
   * пустое «Добавить» ставило бы отбор, которого человек не задавал. У «+ условие» этого нет: там
   * сравнение с пустой ячейкой набирают намеренно.
   */
  requireValue?: boolean;
  children: ReactNode;
} & Omit<ComponentProps<typeof Popover.Trigger>, 'asChild' | 'children' | 'onSubmit'>) {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(initial);
  const hintId = useId();
  const valueRef = useRef<HTMLInputElement>(null);

  function toggle(next: boolean) {
    // Открыли заново — начинаем с того, что стоит в отборе, а не с брошенного черновика.
    if (next) setDraft(initial);
    setOpen(next);
  }

  const waiting = requireValue === true && !conditionEntered(draft);

  // Запертой кнопки достаточно и для Enter: форму с запертой кнопкой отправки браузер по Enter из
  // поля не отправляет (проверено поломкой — живой прогон жмёт Enter в поле значения).
  function apply() {
    onSubmit(draft);
    setOpen(false);
  }

  return (
    <Popover.Root open={open} onOpenChange={toggle}>
      <Popover.Trigger asChild {...trigger}>{children}</Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6}
          className="z-50 w-72 rounded-lg bg-surface border border-outline-variant p-3 focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level2)' }}>
          <form className="group/condition" onSubmit={e => { e.preventDefault(); apply(); }}>
            <ConditionEditor stacked cond={draft} columns={columns} onChange={setDraft} hintId={hintId} valueRef={valueRef} />
            {/* Подсказка, а не запрет — как в диалоге (issue #1137): годно ли условие, решает сервер, а его
                отказ экран покажет на чипе. Пометка «нет в справочнике» тоже не запирает «Применить». */}
            {waiting
              ? <p className="mt-1.5 text-xs text-fg4">Введите значение — без него отбор не ставится.</p>
              : <ConditionHint id={hintId} cond={draft} columns={columns} onChange={setDraft} className="mt-1.5"
                  problemOf={chipProblem} fieldRef={valueRef} />}
            <div className="mt-2.5 flex justify-end">
              <button type="submit" disabled={!draft.column.trim() || waiting}
                className="px-3 py-1 rounded-md text-xs font-medium text-on-primary bg-primary disabled:opacity-50">
                {submit}
              </button>
            </div>
          </form>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
