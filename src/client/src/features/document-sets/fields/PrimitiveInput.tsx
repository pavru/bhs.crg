import { isoToRu, ruToISO } from '@/shared/utils/date';
import { DateInput } from '@/shared/ui/DateInput';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, FieldConstraints } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import { fieldInputClass } from './constants';

export function isMissing(field: SchemaField, val: unknown): boolean {
  if (!field.required) return false;
  if (field.type === 'boolean') return false;
  if (field.type === 'complex') return false;
  if (isFieldRef(val)) return false;
  return val == null || String(val).trim() === '';
}

export function validateConstraint(value: unknown, def: PrimitiveTypeDef): string | null {
  if (value == null || value === '') return null;
  const c: FieldConstraints = def.constraints;
  if (def.baseType === 'string') {
    const str = String(value);
    if (c.pattern) {
      try {
        if (!new RegExp(c.pattern).test(str))
          return c.patternMessage ?? `Не соответствует формату: ${c.pattern}`;
      } catch { /* invalid regex */ }
    }
    if (c.minLength != null && str.length < c.minLength) return `Мин. длина: ${c.minLength} симв.`;
    if (c.maxLength != null && str.length > c.maxLength) return `Макс. длина: ${c.maxLength} симв.`;
  } else if (def.baseType === 'number') {
    const num = Number(value);
    if (isNaN(num)) return 'Введите число';
    if (c.integer && !Number.isInteger(num)) return 'Введите целое число';
    if (c.min != null && num < c.min) return `Мин. значение: ${c.min}`;
    if (c.max != null && num > c.max) return `Макс. значение: ${c.max}`;
  } else if (def.baseType === 'date') {
    const iso = ruToISO(String(value));
    if (c.minDate && iso < c.minDate) return `Дата не ранее ${isoToRu(c.minDate)}`;
    if (c.maxDate && iso > c.maxDate) return `Дата не позднее ${isoToRu(c.maxDate)}`;
  }
  return null;
}

export function PrimitiveInput({ field, value, onChange, invalid, primitiveTypeDef }: {
  field: SchemaField; value: unknown; onChange: (val: unknown) => void; invalid: boolean;
  primitiveTypeDef?: PrimitiveTypeDef;
}) {
  const strVal = value == null ? '' : String(value);
  const cls = fieldInputClass(invalid);
  if (field.type === 'text')
    return <textarea value={strVal} onChange={e => onChange(e.target.value)} rows={3} className={cls + ' resize-y'} />;
  if (field.type === 'boolean')
    return (
      <label className="flex items-center gap-2 cursor-pointer">
        <input type="checkbox" checked={!!value} onChange={e => onChange(e.target.checked)} className="w-4 h-4 rounded border-stroke-strong text-brand" />
        <span className="text-sm text-fg2">{field.title}</span>
      </label>
    );
  if (field.type === 'enum') {
    const opts = (field.options ?? []).filter(o => o !== '');
    if (opts.length === 0)
      return <p className="text-xs text-fg4 italic py-1">Нет вариантов — добавьте их в схеме типа документа</p>;
    return (
      <select value={strVal} onChange={e => onChange(e.target.value)} className={cls}>
        <option value="">— выберите —</option>
        {opts.map(opt => <option key={opt} value={opt}>{opt}</option>)}
      </select>
    );
  }
  if (field.type === 'primitive' && primitiveTypeDef) {
    const bt = primitiveTypeDef.baseType;
    if (bt === 'date') {
      return <DateInput value={strVal} onChange={v => onChange(v)} className={cls} />;
    }
    const step = bt === 'number' && primitiveTypeDef.constraints.integer ? 1 : undefined;
    return (
      <input
        type={bt === 'number' ? 'number' : 'text'}
        step={step}
        placeholder={primitiveTypeDef.description}
        value={strVal}
        onChange={e => {
          const v = e.target.value;
          onChange(bt === 'number' ? (v === '' ? '' : Number(v)) : v);
        }}
        className={cls}
      />
    );
  }
  if (field.type === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)} className={cls} />;
  }
  return (
    <input
      type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={e => {
        const v = e.target.value;
        onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
      }}
      className={cls}
    />
  );
}
