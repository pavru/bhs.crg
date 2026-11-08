import type { ReactNode } from 'react';
import { ruToISO } from '@/shared/utils/date';
import { inMask } from '@/shared/format/dateMask';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { SuggestField } from '@/shared/ui/SuggestField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { Checkbox } from '@/shared/ui/Checkbox';
import type { FieldTrailingProps } from '@/shared/ui/fieldBox';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, EnumTypeDef, DatePrecision } from '@/shared/api/types';
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

/**
 * Дата только для чтения — готовым текстом в записи маски поля (D7 sharedui-md3-19), а не `DateField`:
 * своего хвоста у поля даты нет (знак ошибки стоит в его поле ввода), а готовый текст и копируется
 * целиком. Нераспознаваемое значение показываем как есть — по той же причине, что `dateForDisplay`.
 * Язык — из `useLocale`, как у `DateInput`: смени его человек при открытой форме, дата из источника
 * обязана перейти на новую маску вместе с редактируемыми (ревью PR #1775).
 */
function shownDate(value: string, locale: string, precision: DatePrecision = 'day') {
  return inMask(dateForDisplay(value), resolveLocale(locale), precision);
}

/**
 * Поле только для чтения — значение кладёт источник данных или модуль, а не человек ([UI-86]).
 *
 * Хвост и ошибка — только у него. У редактируемого поля кнопка привязки стоит в углу ячейки, а ошибку
 * под полем рисует форма: так она стоит одинаково у строки, перечисления, даты и подсказки по введённому.
 */
interface ReadOnlyView {
  /** Сообщение об ошибке значения — на месте подсказки, а не строкой под полем ([UI-86]). */
  error?: string;
  /**
   * Хвост: замок и кнопка привязки, слитая со знаком происхождения ([UI-86]). `trailingMark` — перед кнопкой
   * есть ещё знак 18 (см. `FieldTrailingProps`). Флажок хвоста не рисует: признаки у него — рядом с его
   * собственной подписью.
   */
  trailing?: ReactNode;
  trailingMark?: boolean;
  /** Пояснения знаков хвоста — ид элементов, которые диктор прочтёт вместе с полем. */
  describedBy?: string;
}

export interface PrimitiveInputProps extends ReadOnlyView {
  field: SchemaField; value: unknown; onChange: (val: unknown) => void; invalid: boolean;
  primitiveTypeDef?: PrimitiveTypeDef; enumTypeDef?: EnumTypeDef; readOnly?: boolean;
  /**
   * Подпись в вырезе рамки (issue #110 G1, #565): всплывающая у однострочных полей, постоянно поднятая у
   * текста, даты и перечисления. Обязательна (D7 sharedui-md3-19): поле без подписи было сырым полем ввода со
   * своей рамкой, а у поля формы по схеме имя есть всегда. Флажок подписывает себя ею же.
   */
  label: string; hint?: string;
  /**
   * Значение положено распознаванием и не проверено ([UI-88]): знак «со скана» в хвосте поля, подсказка и
   * снятие знака правкой — у самого поля `shared/ui`. Задаёт форма счёта у полей, дописанных в тип
   * (costs-invoice-screen-13-undo, D-13.6); документы не задают, у реквизитов метки нет.
   *
   * <p>Флажку и подсказке по введённому знака не передаём: у их полей его нет, а со скана они и не
   * приходят — распознавание кладёт в дописанное поле только строку, текст, число и дату.</p>
   */
  fromScan?: boolean;
}

export function PrimitiveInput({ field, value, onChange, invalid, primitiveTypeDef, enumTypeDef, readOnly, label, hint,
  error, trailing, trailingMark, describedBy, fromScan }: PrimitiveInputProps) {
  const strVal = value == null ? '' : String(value);
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
  const [locale] = useLocale();
  // Знак перед кнопкой тип поля пускает только вместе с самим хвостом. Знак «со скана» с замком не ставится
  // ([UI-88]): у запертого поля значение кладёт источник, а не распознавание.
  const tail: FieldTrailingProps = trailing != null && trailingMark ? { trailing, trailingMark: true } : { trailing, fromScan };
  /** Поле только для чтения с готовым значением — строка, число, перечисление, дата. */
  const shown = (text: string, ownHint = hint) =>
    <TextField label={label} hint={ownHint} invalid={invalid} error={error} aria-describedby={describedBy}
      value={text} readOnly onChange={() => {}} {...tail} />;

  if (field.type === 'text')
    return readOnly
      ? <TextAreaField label={label} hint={hint} invalid={invalid} error={error} aria-describedby={describedBy}
          value={strVal} readOnly onChange={() => {}} className="resize-y" {...tail} />
      : <TextAreaField label={label} required={askRequired} hint={hint} invalid={invalid}
          value={strVal} onChange={e => onChange(e.target.value)} className="resize-y" fromScan={fromScan} />;
  if (field.type === 'boolean')
    return (
      // Read-only флажок НЕ отключаем (issue #958): `disabled` убирает его из обхода клавиатурой и
      // читается вспомогательными средствами как «сейчас недоступно», хотя значение настоящее и
      // окончательное. У флажка нет своего `readOnly`, поэтому правку гасим сами: клик отменяем,
      // состояние объявляем через `aria-readonly` — по нему же флажок рисует обводку outline-variant
      // ([UI-69]), как у поля только для чтения.
      <label className={`flex items-center gap-2 ${readOnly ? 'cursor-default' : 'cursor-pointer'}`}>
        <Checkbox checked={!!value} aria-readonly={readOnly || undefined}
          onChange={v => { if (!readOnly) onChange(v); }}
          onClick={e => { if (readOnly) e.preventDefault(); }} />
        <span className="text-body-medium text-on-surface-variant">{label}</span>
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
    // Отключённый список — не то же, что показанный выбор (issue #958): из `disabled`-контрола
    // значение не выделить и не скопировать, а клавиатура его пропускает. Показываем ПОДПИСЬ
    // выбранного варианта обычным read-only полем — код варианта человеку ничего не говорит.
    if (readOnly) return shown(opts.find(o => o.code === strVal)?.label ?? strVal);
    return (
      <Select value={strVal || undefined} onValueChange={onChange} disabled={empty}
        placeholder={empty ? '—' : '— выберите —'}
        label={label} required={askRequired}
        // Про отсутствие вариантов сообщаем подписью ПОД полем, а не вместо поля: иначе исчезала бы
        // и подпись, и на форме оставалась висеть фраза без имени поля, к которому она относится.
        hint={empty ? NO_OPTIONS : hint} invalid={invalid} fromScan={fromScan}>
        {opts.map(o => <SelectItem key={o.code} value={o.code}>{o.label}</SelectItem>)}
      </Select>
    );
  }
  if (field.type === 'primitive' && primitiveTypeDef) {
    const bt = primitiveTypeDef.baseType;
    const typeHint = hint ?? primitiveTypeDef.description;
    if (bt === 'date') {
      const { datePrecision: prec = 'day', minDate: min, maxDate: max } = primitiveTypeDef.constraints;
      if (readOnly) return shown(shownDate(strVal, locale, prec), typeHint);
      return <DateField label={label} required={askRequired} hint={typeHint} invalid={invalid}
        value={dateForDisplay(strVal)} onChange={v => onChange(v)} precision={prec} min={min} max={max} fromScan={fromScan} />;
    }
    if (readOnly) return shown(strVal, typeHint);
    const step = bt === 'number' && primitiveTypeDef.constraints.integer ? 1 : undefined;
    const onCh = (v: string) => onChange(bt === 'number' ? (v === '' ? '' : Number(v)) : v);
    return <TextField label={label} required={askRequired} hint={typeHint}
      type={bt === 'number' ? 'number' : 'text'} step={step} value={strVal}
      invalid={invalid} onChange={e => onCh(e.target.value)} fromScan={fromScan} />;
  }
  if (field.type === 'date') {
    if (readOnly) return shown(shownDate(strVal, locale));
    return <DateField label={label} required={askRequired} hint={hint} invalid={invalid}
      value={dateForDisplay(strVal)} onChange={v => onChange(v)} fromScan={fromScan} />;
  }
  if (readOnly) return shown(strVal);
  if (source)
    return <SuggestField source={source} label={label} required={askRequired} hint={hint} invalid={invalid}
      value={strVal} onChange={v => onChange(v)} />;
  const onCh = (v: string) => onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
  return <TextField label={label} required={askRequired} hint={hint}
    type={field.type === 'number' ? 'number' : 'text'} value={strVal}
    invalid={invalid} onChange={e => onCh(e.target.value)} fromScan={fromScan} />;
}
