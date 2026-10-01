import { useState } from 'react';
import { X } from 'lucide-react';
import type { FilterCondition } from '@/shared/api/types';
import { opArity, valueFits } from './rowFilterModel';

const FIELD_CLS = 'border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1';

/**
 * Одно значение — полем по виду колонки (issue #1133). Поле само даёт запись, которую разберёт
 * сервер: дата — ГГГГ-ММ-ДД, число — с точкой, флаг — true / false.
 *
 * Значение, сохранённое раньше и в поле вида не помещающееся («1,5» у числа), показывается ТЕКСТОМ:
 * спрятать его значило бы показать условие пустым, хотя в базе оно есть. Решается это один раз, при
 * появлении поля, а не на каждом нажатии: иначе поле меняло бы вид посреди ввода («1.» — ещё не число).
 */
function ValueInput({ kind, value, onChange, placeholder, onEnter }: {
  kind?: string;
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  onEnter?: () => void;
}) {
  const cls = `${FIELD_CLS} flex-1 min-w-0`;
  const [typed] = useState(() => value === '' || valueFits(kind, value));

  if (kind === 'boolean' && typed)
    return (
      <select value={value} onChange={e => onChange(e.target.value)} className={cls} aria-label={placeholder}>
        <option value="">— значение —</option>
        <option value="true">да</option>
        <option value="false">нет</option>
      </select>
    );

  return (
    <input
      type={typed && kind === 'number' ? 'number' : typed && kind === 'date' ? 'date' : 'text'}
      step="any"
      value={value}
      onChange={e => onChange(e.target.value)}
      onKeyDown={e => { if (e.key === 'Enter' && onEnter) { e.preventDefault(); onEnter(); } }}
      placeholder={placeholder}
      aria-label={placeholder}
      className={cls}
    />
  );
}

/** Список значений: введённое добавляется по Enter или кнопкой, каждое убирается своим крестиком. */
function ValueList({ kind, values, onChange }: {
  kind?: string;
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
        <ValueInput kind={kind} value={draft} onChange={setDraft} placeholder="Добавить значение" onEnter={add} />
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
 * Поле значения условия — по оператору: ничего, одно значение, две границы или список. Форму
 * значения (`value` или `values`) здесь не меняем: её выставляет смена оператора (`withOperator`).
 */
export function RowFilterValue({ cond, kind, onChange }: {
  cond: FilterCondition;
  kind?: string;
  onChange: (c: FilterCondition) => void;
}) {
  // Сменили колонку — поле появляется заново: вид у новой колонки другой.
  const field = `${cond.column}:${kind ?? ''}`;

  switch (opArity(cond.op)) {
    case 'none':
      return <div className="flex-1" />;

    case 'one':
      return (
        <ValueInput key={field} kind={kind} value={cond.value ?? ''} placeholder="Значение"
          onChange={value => onChange({ ...cond, value })} />
      );

    case 'two': {
      const [from = '', to = ''] = cond.values ?? [];
      return (
        <div className="flex-1 min-w-0 flex items-center gap-1">
          <ValueInput key={`${field}:from`} kind={kind} value={from} placeholder="от"
            onChange={v => onChange({ ...cond, values: [v, to] })} />
          <span className="text-xs text-fg4">—</span>
          <ValueInput key={`${field}:to`} kind={kind} value={to} placeholder="до"
            onChange={v => onChange({ ...cond, values: [from, v] })} />
        </div>
      );
    }

    case 'list':
      return (
        <ValueList key={field} kind={kind} values={cond.values ?? []}
          onChange={values => onChange({ ...cond, values })} />
      );
  }
}
