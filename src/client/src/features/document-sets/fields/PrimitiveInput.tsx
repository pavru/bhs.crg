import { ruToISO } from '@/shared/utils/date';
import { DateInput } from '@/shared/ui/DateInput';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { SuggestField } from '@/shared/ui/SuggestField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { Checkbox } from '@/shared/ui/Checkbox';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import { fieldInputClass } from './constants';
import { useSuggestSource } from './suggestContext';

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
  /**
   * Звёздочку обязательности у нередактируемого поля не рисуем (issue #958): она — указание тому,
   * кто заполняет, а у read-only поля ввода нет вовсе. Значение кладёт либо источник данных, либо
   * код модуля; человеку звёздочка обещала бы работу, которой он сделать не может.
   *
   * Само требование при этом никуда не девается — его держит генерация, а не форма.
   */
  const askRequired = readOnly ? undefined : field.required;
  // Подсказка по введённому ранее (issue #1533): только в форме записи, назвавшей тип-владелец, и
  // только у поля, которое правило `Suggestable` подсказывает. Иначе — прежнее поле, адрес не зовётся.
  const source = useSuggestSource(field);

  if (field.type === 'text')
    return label != null
      ? <TextAreaField label={label} required={askRequired} hint={hint} invalid={invalid}
          value={strVal} readOnly={readOnly} onChange={e => onChange(e.target.value)} className="resize-y" />
      : <textarea value={strVal} onChange={e => onChange(e.target.value)} rows={3} readOnly={readOnly} className={cls + ' resize-y'} />;
  if (field.type === 'boolean')
    return (
      // Read-only флажок НЕ отключаем (issue #958): `disabled` убирает его из обхода клавиатурой и
      // читается вспомогательными средствами как «сейчас недоступно», хотя значение настоящее и
      // окончательное. У флажка нет своего `readOnly`, поэтому правку гасим сами: клик отменяем,
      // состояние объявляем через `aria-readonly`.
      <label className={`flex items-center gap-2 ${readOnly ? 'cursor-default' : 'cursor-pointer'}`}>
        <Checkbox checked={!!value} aria-readonly={readOnly || undefined}
          onChange={v => { if (!readOnly) onChange(v); }}
          onClick={e => { if (readOnly) e.preventDefault(); }}
          className={readOnly ? 'opacity-70' : ''} />
        <span className="text-body-medium text-on-surface-variant">{label ?? field.title}</span>
      </label>
    );
  if (field.type === 'enum') {
    // Подпись отдаём самому контролу (issue #574): в слое формы она живёт в вырезе рамки, как у
    // соседних строк, чисел и дат. Рамку ошибки там рисует оболочка — свой border-error не нужен.
    const NO_OPTIONS = 'Нет вариантов — добавьте их в схеме типа документа';
    const opts = enumTypeDef
      ? enumTypeDef.values.map(v => ({ code: v.code, label: v.label }))
      : (field.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
    const empty = opts.length === 0;
    if (empty && label == null)
      return <p className="text-body-small text-on-surface-variant italic py-1">{NO_OPTIONS}</p>;
    if (readOnly) {
      // Отключённый список — не то же, что показанный выбор (issue #958): из `disabled`-контрола
      // значение не выделить и не скопировать, а клавиатура его пропускает. Показываем ПОДПИСЬ
      // выбранного варианта обычным read-only полем — код варианта человеку ничего не говорит.
      const shown = opts.find(o => o.code === strVal)?.label ?? strVal;
      return label != null
        ? <TextField label={label} required={undefined} hint={hint} invalid={invalid}
            value={shown} readOnly onChange={() => {}} />
        : <input type="text" value={shown} readOnly aria-label={field.title}
            onChange={() => {}} className={cls} />;
    }
    return (
      <Select value={strVal || undefined} onValueChange={onChange} disabled={empty}
        placeholder={empty ? '—' : '— выберите —'}
        label={label} required={label != null ? askRequired : undefined}
        // Про отсутствие вариантов сообщаем подписью ПОД полем, а не вместо поля: иначе исчезала бы
        // и подпись, и на форме оставалась висеть фраза без имени поля, к которому она относится.
        hint={empty ? NO_OPTIONS : hint} invalid={invalid}
        aria-label={label == null ? field.title : undefined}
        className={label == null && invalid ? 'border-error' : ''}>
        {opts.map(o => <SelectItem key={o.code} value={o.code}>{o.label}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'primitive' && primitiveTypeDef) {
    const bt = primitiveTypeDef.baseType;
    if (bt === 'date') {
      const { datePrecision: prec = 'day', minDate: min, maxDate: max } = primitiveTypeDef.constraints;
      return label != null
        ? <DateField label={label} required={askRequired} hint={hint ?? primitiveTypeDef.description}
            invalid={invalid} value={dateForDisplay(strVal)} onChange={v => onChange(v)} precision={prec} readOnly={readOnly} min={min} max={max} />
        : <DateInput label={field.title} value={dateForDisplay(strVal)} onChange={v => onChange(v)} precision={prec} size="md" invalid={invalid}
            className="w-full" readOnly={readOnly} required={askRequired} min={min} max={max} />;
    }
    const step = bt === 'number' && primitiveTypeDef.constraints.integer ? 1 : undefined;
    const onCh = (v: string) => onChange(bt === 'number' ? (v === '' ? '' : Number(v)) : v);
    if (label != null)
      return <TextField label={label} required={askRequired} hint={hint ?? primitiveTypeDef.description}
        type={bt === 'number' ? 'number' : 'text'} step={step} value={strVal} readOnly={readOnly}
        invalid={invalid} onChange={e => onCh(e.target.value)} />;
    return (
      <input type={bt === 'number' ? 'number' : 'text'} step={step} placeholder={primitiveTypeDef.description}
        value={strVal} readOnly={readOnly} onChange={e => onCh(e.target.value)} className={cls} />
    );
  }
  if (field.type === 'date') {
    return label != null
      ? <DateField label={label} required={askRequired} hint={hint} invalid={invalid}
          value={dateForDisplay(strVal)} onChange={v => onChange(v)} readOnly={readOnly} />
      : <DateInput label={field.title} value={dateForDisplay(strVal)} onChange={v => onChange(v)} size="md" invalid={invalid}
          className="w-full" readOnly={readOnly} required={askRequired} />;
  }
  if (source && !readOnly)
    return label != null
      ? <SuggestField source={source} label={label} required={askRequired} hint={hint} invalid={invalid}
          value={strVal} onChange={v => onChange(v)} />
      // Голое поле формы, не клетка: ↓ открывает список, как у поля с подписью.
      : <SuggestField source={source} compact arrowOpens label={field.title} value={strVal} onChange={v => onChange(v)} className={cls} />;
  const onCh = (v: string) => onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
  if (label != null)
    return <TextField label={label} required={askRequired} hint={hint}
      type={field.type === 'number' ? 'number' : 'text'} value={strVal} readOnly={readOnly}
      invalid={invalid} onChange={e => onCh(e.target.value)} />;
  return (
    <input type={field.type === 'number' ? 'number' : 'text'} value={strVal} readOnly={readOnly}
      onChange={e => onCh(e.target.value)} className={cls} />
  );
}
