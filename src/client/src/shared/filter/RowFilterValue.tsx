import { useState } from 'react';
import { X } from 'lucide-react';
import type { FilterCondition } from '@/shared/api/types';
import { opArity, valueFits, type FilterColumn } from './rowFilterModel';

const FIELD_CLS = 'border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1';

/** Что полю значения нужно знать о колонке: вид и, у колонки-выбора, её перечень. */
type ValueColumn = Pick<FilterColumn, 'kind' | 'options'>;

/**
 * Одно значение — полем по виду колонки (issue #1133). Поле само даёт запись, которую разберёт
 * сервер: дата — ГГГГ-ММ-ДД, число — с точкой, флаг — true / false, выбор — слово из перечня колонки
 * (issue #1091): набрать произвольную строку в таком поле нечем.
 *
 * Значение, сохранённое раньше и в поле вида не помещающееся («1,5» у числа, слово вне перечня у
 * выбора), показывается ТЕКСТОМ: спрятать его значило бы показать условие пустым, хотя в базе оно
 * есть. Решается это один раз, при появлении поля, а не на каждом нажатии: иначе поле меняло бы вид
 * посреди ввода («1.» — ещё не число).
 */
function ValueInput({ column, value, onChange, placeholder, onEnter, onBlur }: {
  column?: ValueColumn;
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  onEnter?: () => void;
  onBlur?: () => void;
}) {
  const kind = column?.kind;
  const cls = `${FIELD_CLS} flex-1 min-w-0`;
  const [typed, setTyped] = useState(() => value === '' || valueFits(kind, value, column?.options));

  // Негодное значение стёрли — показывать текстом больше нечего, и поле становится полем своего вида.
  function change(next: string) {
    if (next === '') setTyped(true);
    onChange(next);
  }

  if ((kind === 'boolean' || kind === 'choice') && typed)
    return (
      <select value={value} onChange={e => change(e.target.value)} onBlur={onBlur} className={cls} aria-label={placeholder}>
        <option value="">— значение —</option>
        {kind === 'boolean'
          ? <><option value="true">да</option><option value="false">нет</option></>
          : (column?.options ?? []).map(o => <option key={o} value={o}>{o}</option>)}
      </select>
    );

  return (
    <input
      type={typed && kind === 'number' ? 'number' : typed && kind === 'date' ? 'date' : 'text'}
      step="any"
      value={value}
      onChange={e => change(e.target.value)}
      onKeyDown={e => { if (e.key === 'Enter' && onEnter) { e.preventDefault(); onEnter(); } }}
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
 * Набранное, но не добавленное значение добавляется и при уходе из поля. Иначе «набрал второе
 * значение и нажал „Сохранить“» сохраняло отбор без него — уже́ задуманного и без единого сигнала.
 */
function ValueList({ column, values, onChange }: {
  column?: ValueColumn;
  values: string[];
  onChange: (v: string[]) => void;
}) {
  const [draft, setDraft] = useState('');

  function add() {
    const value = draft.trim();
    if (!value) return;
    if (!values.includes(value)) onChange([...values, value]);
    setDraft('');
  }

  return (
    <div className="flex-1 min-w-0 flex flex-wrap items-center gap-1">
      {values.map(v => (
        <span key={v} className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs bg-brand-subtle text-fg1">
          {v}
          <button
            type="button"
            onClick={() => onChange(values.filter(x => x !== v))}
            className="text-fg4 hover:text-danger"
            aria-label={`Убрать «${v}»`}
          >
            <X size={10} />
          </button>
        </span>
      ))}
      <div className="flex items-center gap-1 flex-1" style={{ minWidth: '120px' }}>
        <ValueInput column={column} value={draft} onChange={setDraft} placeholder="Добавить значение"
          onEnter={add} onBlur={add} />
        <button
          type="button"
          onClick={add}
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
 * и снимается крестиком — иначе условие выглядело бы короче, чем оно есть в базе.
 */
function OptionList({ options, values, onChange }: {
  options: string[];
  values: string[];
  onChange: (v: string[]) => void;
}) {
  const strays = values.filter(v => !options.includes(v));
  // Порядок значений в условии — порядок перечня, а не порядок щелчков: один и тот же выбор даёт
  // одно и то же условие.
  const toggle = (option: string) => onChange(values.includes(option)
    ? values.filter(v => v !== option)
    : [...options.filter(o => o === option || values.includes(o)), ...strays]);

  return (
    <div className="flex-1 min-w-0 flex flex-wrap items-center gap-x-3 gap-y-1">
      {options.map(o => (
        <label key={o} className="inline-flex items-center gap-1 text-xs text-fg1 cursor-pointer">
          <input type="checkbox" checked={values.includes(o)} onChange={() => toggle(o)} />
          {o}
        </label>
      ))}
      {strays.map(v => (
        <span key={v} className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs bg-muted text-danger"
          title="Этого значения в перечне колонки нет">
          {v}
          <button type="button" onClick={() => onChange(values.filter(x => x !== v))}
            className="hover:text-danger" aria-label={`Убрать «${v}»`}>
            <X size={10} />
          </button>
        </span>
      ))}
    </div>
  );
}

/**
 * Поле значения условия — по оператору: ничего, одно значение, две границы или список. Форму
 * значения (`value` или `values`) здесь не меняем: её выставляет смена оператора (`withOperator`).
 */
export function RowFilterValue({ cond, column, onChange }: {
  cond: FilterCondition;
  column?: ValueColumn;
  onChange: (c: FilterCondition) => void;
}) {
  // Сменили колонку — поле появляется заново: вид у новой колонки другой.
  const field = `${cond.column}:${column?.kind ?? ''}`;

  switch (opArity(cond.op)) {
    case 'none':
      return <div className="flex-1" />;

    case 'one':
      return (
        <ValueInput key={field} column={column} value={cond.value ?? ''} placeholder="Значение"
          onChange={value => onChange({ ...cond, value })} />
      );

    case 'two': {
      const [from = '', to = ''] = cond.values ?? [];
      return (
        <div className="flex-1 min-w-0 flex items-center gap-1">
          <ValueInput key={`${field}:from`} column={column} value={from} placeholder="от"
            onChange={v => onChange({ ...cond, values: [v, to] })} />
          <span className="text-xs text-fg4">—</span>
          <ValueInput key={`${field}:to`} column={column} value={to} placeholder="до"
            onChange={v => onChange({ ...cond, values: [from, v] })} />
        </div>
      );
    }

    case 'list':
      return column?.kind === 'choice'
        ? <OptionList key={field} options={column.options ?? []} values={cond.values ?? []}
            onChange={values => onChange({ ...cond, values })} />
        : <ValueList key={field} column={column} values={cond.values ?? []}
            onChange={values => onChange({ ...cond, values })} />;
  }
}
