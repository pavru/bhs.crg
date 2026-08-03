import { formatDateRu, ruToISO } from '@/shared/utils/date';
import { DateInput } from '@/shared/ui/DateInput';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, FieldConstraints, EnumTypeDef } from '@/shared/api/types';
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
    const prec = c.datePrecision ?? 'day';
    if (c.minDate && iso < c.minDate) return `Дата не ранее ${formatDateRu(c.minDate, prec)}`;
    if (c.maxDate && iso > c.maxDate) return `Дата не позднее ${formatDateRu(c.maxDate, prec)}`;
  }
  return null;
}

/**
 * Значение для календарного контрола: тот понимает только полный ISO и на «12.05.2024» показывает
 * ПУСТОЕ поле, хотя значение есть и уйдёт обратно в базу при сохранении.
 *
 * Русскую дату в поле даты кладёт распознавание (модель отдаёт то, что прочла в скане) и старые
 * записи, заполненные тогда, когда поле рисовалось простым текстом. Показать пусто вместо «12.05.2024»
 * значит спрятать расхождение: человек его не исправит, а аудит значений (#644) будет ругаться на то,
 * чего в форме не видно. Хранимое значение при этом НЕ переписываем — только показываем; правкой
 * контрол сам запишет полный ISO.
 */
function dateForDisplay(value: string): string {
  return ruToISO(value);
}

export function PrimitiveInput({ field, value, onChange, invalid, primitiveTypeDef, enumTypeDef, readOnly, label, hint }: {
  field: SchemaField; value: unknown; onChange: (val: unknown) => void; invalid: boolean;
  primitiveTypeDef?: PrimitiveTypeDef; enumTypeDef?: EnumTypeDef; readOnly?: boolean;
  /** Если задано — контрол сам рендерит подпись в вырезе рамки (issue #110 G1, #565): всплывающую
   *  у однострочных полей, постоянно поднятую у текста, даты и перечисления (там у контрола нет
   *  состояния «пусто» в смысле CSS). Чекбокс подписывает себя сам (field.title). */
  label?: string; hint?: string;
}) {
  const strVal = value == null ? '' : String(value);
  const cls = fieldInputClass(invalid, readOnly);

  if (field.type === 'text')
    return label != null
      ? <TextAreaField label={label} required={field.required} hint={hint} invalid={invalid}
          value={strVal} readOnly={readOnly} onChange={e => onChange(e.target.value)} className="resize-y" />
      : <textarea value={strVal} onChange={e => onChange(e.target.value)} rows={3} readOnly={readOnly} className={cls + ' resize-y'} />;
  if (field.type === 'boolean')
    return (
      <label className="flex items-center gap-2 cursor-pointer">
        <input type="checkbox" checked={!!value} onChange={e => onChange(e.target.checked)} disabled={readOnly} className="w-4 h-4 rounded border-stroke-strong text-brand" />
        <span className="text-sm text-fg2">{label ?? field.title}</span>
      </label>
    );
  if (field.type === 'enum') {
    // Подпись отдаём самому контролу (issue #574): в слое формы она живёт в вырезе рамки, как у
    // соседних строк, чисел и дат. Рамку ошибки там рисует оболочка — свой border-danger не нужен.
    const NO_OPTIONS = 'Нет вариантов — добавьте их в схеме типа документа';
    const opts = enumTypeDef
      ? enumTypeDef.values.map(v => ({ code: v.code, label: v.label }))
      : (field.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
    const empty = opts.length === 0;
    if (empty && label == null)
      return <p className="text-xs text-fg4 italic py-1">{NO_OPTIONS}</p>;
    return (
      <Select value={strVal || undefined} onValueChange={onChange} disabled={readOnly || empty}
        placeholder={empty ? '—' : '— выберите —'}
        label={label} required={label != null ? field.required : undefined}
        // Про отсутствие вариантов сообщаем подписью ПОД полем, а не вместо поля: иначе исчезала бы
        // и подпись, и на форме оставалась висеть фраза без имени поля, к которому она относится.
        hint={empty ? NO_OPTIONS : hint} invalid={invalid}
        aria-label={label == null ? field.title : undefined}
        className={label == null && invalid ? 'border-danger' : ''}>
        {opts.map(o => <SelectItem key={o.code} value={o.code}>{o.label}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'primitive' && primitiveTypeDef) {
    const bt = primitiveTypeDef.baseType;
    if (bt === 'date') {
      const prec = primitiveTypeDef.constraints.datePrecision ?? 'day';
      return label != null
        ? <DateField label={label} required={field.required} hint={hint ?? primitiveTypeDef.description}
            invalid={invalid} value={dateForDisplay(strVal)} onChange={v => onChange(v)} precision={prec} disabled={readOnly} />
        : <DateInput value={dateForDisplay(strVal)} onChange={v => onChange(v)} precision={prec} className={cls} disabled={readOnly} />;
    }
    const step = bt === 'number' && primitiveTypeDef.constraints.integer ? 1 : undefined;
    const onCh = (v: string) => onChange(bt === 'number' ? (v === '' ? '' : Number(v)) : v);
    if (label != null)
      return <TextField label={label} required={field.required} hint={hint ?? primitiveTypeDef.description}
        type={bt === 'number' ? 'number' : 'text'} step={step} value={strVal} readOnly={readOnly}
        invalid={invalid} onChange={e => onCh(e.target.value)} />;
    return (
      <input type={bt === 'number' ? 'number' : 'text'} step={step} placeholder={primitiveTypeDef.description}
        value={strVal} readOnly={readOnly} onChange={e => onCh(e.target.value)} className={cls} />
    );
  }
  if (field.type === 'date') {
    return label != null
      ? <DateField label={label} required={field.required} hint={hint} invalid={invalid}
          value={dateForDisplay(strVal)} onChange={v => onChange(v)} disabled={readOnly} />
      : <DateInput value={dateForDisplay(strVal)} onChange={v => onChange(v)} className={cls} disabled={readOnly} />;
  }
  const onCh = (v: string) => onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
  if (label != null)
    return <TextField label={label} required={field.required} hint={hint}
      type={field.type === 'number' ? 'number' : 'text'} value={strVal} readOnly={readOnly}
      invalid={invalid} onChange={e => onCh(e.target.value)} />;
  return (
    <input type={field.type === 'number' ? 'number' : 'text'} value={strVal} readOnly={readOnly}
      onChange={e => onCh(e.target.value)} className={cls} />
  );
}
