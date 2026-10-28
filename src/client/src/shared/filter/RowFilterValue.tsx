import { useEffect, useRef, useState, type KeyboardEvent, type Ref } from 'react';
import { BookX } from 'lucide-react';
import type { FilterCondition } from '@/shared/api/types';
import { choiceStrays, opArity, valueFits, type FilterColumn } from './rowFilterModel';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Chip } from '@/shared/ui/Chip';
import { DateInput } from '@/shared/ui/DateInput';
import { DateRange } from '@/shared/ui/DateRange';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SuggestField } from '@/shared/ui/SuggestField';
import { shownValue } from './chipsModel';
import { conditionNotice } from './conditionNotice';

const FIELD_CLS = 'border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1';

/**
 * Что полю значения нужно знать о колонке: вид, у колонки-выбора — её перечень, у колонки с открытым
 * перечнем (`known`) — его и то, чем сверяется пометка. Имя колонки берётся из условия.
 */
type ValueColumn = Omit<FilterColumn, 'name'>;

/**
 * Одно значение — полем по виду колонки (issue #1133). Поле само даёт запись, которую разберёт
 * сервер: дата — ГГГГ-ММ-ДД, число — с точкой, флаг — true / false, выбор — слово из перечня колонки
 * (issue #1091): набрать произвольную строку в таком поле нечем.
 *
 * Дата — полем даты (issue #1465): маска языка пользователя, а не ОС, как было у нативного поля;
 * запись та же, ГГГГ-ММ-ДД.
 *
 * Значение, сохранённое раньше и в поле вида не помещающееся («1,5» у числа, слово вне перечня у
 * выбора), показывается ТЕКСТОМ: спрятать его значило бы показать условие пустым, хотя в базе оно
 * есть. Решается это один раз, при появлении поля, а не на каждом нажатии: иначе поле меняло бы вид
 * посреди ввода («1.» — ещё не число).
 *
 * У колонки с открытым перечнем (`known`, UI-56) поле — с подсказкой из него, а значение остаётся
 * свободным текстом: «содержит Комарова» задаётся, как раньше. Есть ли поле с подсказкой, решает
 * объявление колонки, а не загрузка перечня: иначе поле сменилось бы посреди ввода и потеряло фокус.
 */
function ValueInput({ column, value, onChange, placeholder, onEnter, onAccept, onBlur, describedBy, inputRef }: {
  column?: ValueColumn;
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  onEnter?: () => void;
  /** Принят пункт подсказки — у списка значений он сразу уходит в список. */
  onAccept?: (v: string) => void;
  onBlur?: () => void;
  /** Строка пометки под условием — полю в `aria-describedby`. */
  describedBy?: string;
  /** Текстовое поле — пометке: после замены фокус возвращается сюда. */
  inputRef?: Ref<HTMLInputElement>;
}) {
  const kind = column?.kind;
  const cls = `${FIELD_CLS} flex-1 min-w-0`;
  const [typed, setTyped] = useState(() => value === '' || valueFits(kind, value, column?.options));

  // Негодное значение стёрли — показывать текстом больше нечего, и поле становится полем своего вида.
  function change(next: string) {
    if (next === '') setTyped(true);
    onChange(next);
  }

  // Фиксация — закрытием списка, а не уходом с поля: пока список открыт, фокус живёт в нём, и уход
  // с триггера пришёл бы раньше выбора (#1469). `min-h-7`: в окошке чипа поля столбиком, и `flex-1`
  // (основа 0) сжимал бы кнопку-триггер до строки текста — у нативного поля высоту держали поля.
  if ((kind === 'boolean' || kind === 'choice') && typed)
    return (
      <Select size="sm" value={value} onValueChange={change} onOpenChange={open => { if (!open) onBlur?.(); }}
        emptyItem="— значение —" className="flex-1 min-w-0 min-h-7" aria-label={placeholder}>
        {kind === 'boolean'
          ? <><SelectItem value="true">да</SelectItem><SelectItem value="false">нет</SelectItem></>
          : (column?.options ?? []).map(o => <SelectItem key={o} value={o}>{o}</SelectItem>)}
      </Select>
    );

  if (kind === 'date' && typed)
    return <DateInput size="sm" label={placeholder} value={value} onChange={change} className="flex-1 min-w-0" />;

  // Enter уступает списку подсказки, когда в нём выбран пункт: «ком» → ↓ → Enter добавляет «Комарова 36»,
  // а не «ком» (как EditGrid в UI-8). Без выбранного пункта Enter добавляет набранное, как всегда.
  function enter(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key !== 'Enter' || !onEnter || e.currentTarget.getAttribute('aria-activedescendant')) return;
    e.preventDefault();
    onEnter();
  }

  if (column?.known && kind !== 'number')
    return (
      <SuggestField compact arrowOpens options={column.known.items} label={placeholder} placeholder={placeholder}
        value={value} onChange={change} onKeyDown={enter} onAccept={onAccept} onBlur={onBlur}
        aria-describedby={describedBy} className={cls} ref={inputRef} />
    );

  return (
    <input
      type={typed && kind === 'number' ? 'number' : 'text'}
      step="any"
      value={value}
      onChange={e => change(e.target.value)}
      onKeyDown={enter}
      ref={inputRef}
      onBlur={onBlur}
      placeholder={placeholder}
      aria-label={placeholder}
      className={cls}
    />
  );
}

/**
 * Список значений: введённое добавляется по Enter или кнопкой, каждое убирается своим крестиком.
 *
 * Набранное, но не добавленное значение добавляется и при уходе из поля, дата — и выбором в календаре. Иначе «набрал второе
 * значение и нажал „Сохранить“» сохраняло отбор без него — уже́ задуманного и без единого сигнала.
 */
function ValueList({ column, values, marked, describedBy, inputRef, onChange }: {
  column?: ValueColumn;
  values: string[];
  /** Значения вне открытого перечня колонки — жетоном пометки (UI-56). */
  marked?: ReadonlySet<string>;
  describedBy?: string;
  inputRef?: Ref<HTMLInputElement>;
  onChange: (v: string[]) => void;
}) {
  const [draft, setDraft] = useState('');
  const date = column?.kind === 'date';
  // Дата в списке — той же маской, что в поле и на чипе, а не записью для сервера.
  const shown = (v: string) => (date ? shownValue(v, column) : v);
  // На сейчас, а не из замыкания: дату добавляет фиксация поля, а она бывает и таймером — за это время
  // крестик мог снять значение из списка, и старый список вернул бы его (ревью PR #1554).
  const now = useRef({ draft, values });
  useEffect(() => { now.current.values = values; });
  /** Сколько раз добавляли — Enter переводит фокус к началу маски, только если дата ушла в список. */
  const adds = useRef(0);
  const addsAtEnter = useRef(0);
  const dateBox = useRef<HTMLDivElement>(null);
  /**
   * Фокус к началу маски — после отрисовки, а не в обработчике Enter: там в сегменте года ещё «26»,
   * и уход с него дописал бы год второй раз — вернув добавленную дату в поле.
   */
  const refocus = useRef(false);
  useEffect(() => {
    if (!refocus.current) return;
    refocus.current = false;
    dateBox.current?.querySelector('input')?.focus();
  });

  function edit(next: string) {
    now.current.draft = next;
    setDraft(next);
  }

  function add(typed = now.current.draft) {
    const value = typed.trim();
    if (!value) return;
    const { values: current } = now.current;
    if (!current.includes(value)) {
      now.current.values = [...current, value];
      onChange(now.current.values);
    }
    edit('');
    adds.current++;
  }

  return (
    <div className="flex-1 min-w-0 flex flex-wrap items-center gap-1">
      {values.map(v => (
        <ValueChip key={v} warning={marked?.has(v)} label={shown(v)} onRemove={() => onChange(values.filter(x => x !== v))} />
      ))}
      <div className="flex items-center gap-1 flex-1" style={{ minWidth: date ? '200px' : '120px' }}>
        {date ? (
          // Дату добавляет фиксация поля: уход, Enter, выбор в календаре. Enter здесь только не даёт
          // отправить форму и, если дата ушла в список, ведёт к началу маски, а не оставляет на годе.
          <div ref={dateBox} className="flex-1 min-w-0"
            onKeyDownCapture={e => { if (e.key === 'Enter') addsAtEnter.current = adds.current; }}
            onKeyDown={e => {
              if (e.key !== 'Enter' || !(e.target instanceof HTMLInputElement)) return;
              e.preventDefault();
              if (adds.current !== addsAtEnter.current) refocus.current = true;
            }}>
            <DateInput size="sm" label="Добавить значение" value={draft} onChange={edit} onCommit={add} className="w-full" />
          </div>
        ) : (
          <ValueInput column={column} value={draft} onChange={edit} placeholder="Добавить значение"
            onEnter={add} onAccept={add} onBlur={add} describedBy={describedBy} inputRef={inputRef} />
        )}
        <button
          type="button"
          onClick={() => add()}
          disabled={!draft.trim()}
          className="text-xs px-2 py-1 rounded text-fg2 bg-muted hover:bg-brand-subtle disabled:opacity-40"
        >
          Добавить
        </button>
      </div>
    </div>
  );
}

/**
 * Список значений колонки-выбора (issue #1091): перечень целиком, отмечается галочками. Набирать
 * нечего и нечем — в этом и смысл вида.
 *
 * Значение, сохранённое раньше и в перечень не входящее, не прячется: оно показано отдельно, названо
 * и снимается крестиком — иначе условие выглядело бы короче, чем оно есть в базе. Тон и форма — те же,
 * что у пометки «нет в справочнике» (UI-56): жетон предупреждения и строка под отметками, не красное.
 */
function OptionList({ options, values, strays, onChange }: {
  options: string[];
  values: string[];
  /** Значения вне перечня — `choiceStrays`. */
  strays: string[];
  onChange: (v: string[]) => void;
}) {
  // Порядок значений в условии — порядок перечня, а не порядок щелчков: один и тот же выбор даёт
  // одно и то же условие.
  const toggle = (option: string) => onChange(values.includes(option)
    ? values.filter(v => v !== option)
    : [...options.filter(o => o === option || values.includes(o)), ...strays]);

  return (
    <div className="flex-1 min-w-0 flex flex-wrap items-center gap-x-3 gap-y-1">
      {options.map(o => (
        <label key={o} className="inline-flex items-center gap-1 text-xs text-fg1 cursor-pointer">
          <Checkbox size="sm" checked={values.includes(o)} onChange={() => toggle(o)} />
          {o}
        </label>
      ))}
      {strays.map(v => (
        <ValueChip key={v} warning stray label={v} onRemove={() => onChange(values.filter(x => x !== v))} />
      ))}
      {strays.map(v => (
        <p key={`note:${v}`} className="flex w-full items-center gap-1 text-xs text-warning">
          <BookX size={12} aria-hidden className="shrink-0" />«{v}» — нет в перечне колонки
        </p>
      ))}
    </div>
  );
}

/**
 * Значение списка жетоном: `Chip` со снятием (#1469) — крестик рисует он. Нажать в самом жетоне нечего,
 * поэтому его текст вне Tab: с клавиатуры до жетона доходят крестиком «Убрать «…»».
 * `warning` — значение вне справочника или перечня (UI-56): тон предупреждения и значок, а не только цвет.
 */
function ValueChip({ label, warning = false, stray = false, onRemove }: {
  label: string;
  warning?: boolean;
  /** Значение вне перечня колонки-выбора — для строки под отметками и для теста. */
  stray?: boolean;
  onRemove: () => void;
}) {
  return (
    <Chip tone={warning ? 'warning' : 'neutral'} tabIndex={-1} data-stray={stray ? '' : undefined}
      onRemove={onRemove} removeLabel={`Убрать «${label}»`}>
      {/* У значения вне перечня причину называет строка под отметками — значку имя не нужно. */}
      {warning && (stray
        ? <BookX size={10} aria-hidden className="inline align-[-1px] mr-1" />
        : <BookX size={10} aria-label="нет в справочнике" className="inline align-[-1px] mr-1" />)}
      {label}
    </Chip>
  );
}

/**
 * Две границы «между». У даты — `DateRange` (issue #1465): «до» раньше «от» поправляется при уходе из
 * поля, как в любом периоде. Сохранённая граница, которая не дата, — повод показать обе по
 * отдельности, как одно значение: период из текста и даты поправлять не по чему.
 */
function Bounds({ column, from, to, onChange }: {
  column?: ValueColumn;
  from: string;
  to: string;
  onChange: (from: string, to: string) => void;
}) {
  const [period] = useState(() => column?.kind === 'date' && [from, to].every(v => v === '' || valueFits('date', v)));
  if (period)
    return <DateRange size="sm" label="Границы" labelHidden from={from} to={to} onChange={onChange} className="flex-1" />;
  return (
    <div className="flex-1 min-w-0 flex items-center gap-1">
      <ValueInput column={column} value={from} placeholder="от" onChange={v => onChange(v, to)} />
      <span className="text-xs text-fg4">—</span>
      <ValueInput column={column} value={to} placeholder="до" onChange={v => onChange(from, v)} />
    </div>
  );
}

/**
 * Поле значения условия — по оператору: ничего, одно значение, две границы или список. Форму
 * значения (`value` или `values`) здесь не меняем: её выставляет смена оператора (`withOperator`).
 */
export function RowFilterValue({ cond, column, hintId, valueRef, onChange }: {
  cond: FilterCondition;
  column?: ValueColumn;
  /** `id` строки под условием (`ConditionHint`): поле ссылается на неё, когда в ней пометка. */
  hintId?: string;
  /** Текстовое поле значения (одно значение или добавление в список) — для фокуса после замены. */
  valueRef?: Ref<HTMLInputElement>;
  onChange: (c: FilterCondition) => void;
}) {
  // Сменили колонку — поле появляется заново: вид у новой колонки другой.
  const field = `${cond.column}:${column?.kind ?? ''}`;
  const notice = conditionNotice(cond, column);
  const describedBy = notice ? hintId : undefined;

  switch (opArity(cond.op)) {
    case 'none':
      return <div className="flex-1" />;

    case 'one':
      return (
        <ValueInput key={field} column={column} value={cond.value ?? ''} placeholder="Значение"
          describedBy={describedBy} inputRef={valueRef} onChange={value => onChange({ ...cond, value })} />
      );

    case 'two': {
      const [from = '', to = ''] = cond.values ?? [];
      return <Bounds key={field} column={column} from={from} to={to}
        onChange={(f, t) => onChange({ ...cond, values: [f, t] })} />;
    }

    case 'list':
      return column?.kind === 'choice'
        ? <OptionList key={field} options={column.options ?? []} values={cond.values ?? []} strays={choiceStrays(cond, column)}
            onChange={values => onChange({ ...cond, values })} />
        : <ValueList key={field} column={column} values={cond.values ?? []} describedBy={describedBy} inputRef={valueRef}
            marked={notice?.kind === 'values' ? new Set(notice.misses.map(m => m.value)) : undefined}
            onChange={values => onChange({ ...cond, values })} />;
  }
}
