import * as RS from '@radix-ui/react-select';
import { Check, ChevronDown } from 'lucide-react';
import { forwardRef, useId, useState, type ReactNode } from 'react';
import { OutlinedField } from './OutlinedField';

/**
 * MD3-Select (issue #110, фаза 2) поверх Radix Select — замена нативному `<select>`
 * (дефект «смесь нативных и кастомных контролов» из хендоффа). Radix даёт полный
 * APG-listbox с клавиатуры (стрелки/Home/End/typeahead/Esc) — закрывает #107 F5.
 * Тема light/dark через токены.
 *
 * Два слоя (issue #565, шаг 4 — #574), и выбирает между ними наличие `label`, как у
 * `TypePickerField`:
 *
 * - **слой формы** (`label` задан) — общая MD3-оболочка `OutlinedField` высотой `h-14`, подпись
 *   всегда в вырезе рамки. Свой `<label>` в вызывающем коде НЕ пишем: пока подпись жила снаружи,
 *   у семи площадок набралось четыре разные типографики (`text-xs/fg2`, `text-sm/fg1`,
 *   `text-sm/fg2`, `text-xs/fg3`) — при том, что соседние `TextField` в тех же формах уже носили
 *   подпись в вырезе. Связываем через `aria-labelledby`, а не `<label for>`: у кнопки-триггера
 *   щелчок по подписи открывал бы список;
 * - **слой обвязки** (`label` нет) — компактный триггер `h-9` со своей рамкой для шапок, фильтров
 *   и ячеек таблиц, там и живёт плотность.
 *
 * Метка в слое формы поднята ПОСТОЯННО (`raise="always"`): у кнопки-триггера нет состояния «пусто»
 * в смысле CSS — `:placeholder-shown` есть только у `input`, поэтому всплывание по peer-селекторам,
 * как в `TextField`, здесь неосуществимо. Фокус по той же причине приходит пропом.
 *
 * ВНИМАНИЕ: Radix запрещает пустую строку как value у Item. Для «пусто/все» используйте
 * placeholder (не передавайте value) либо отдельный Item с непустым sentinel-значением.
 */
export interface SelectProps {
  value: string | undefined;
  onValueChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  /** Для участия в форме (валидация required через скрытый нативный select у Radix). */
  required?: boolean;
  name?: string;
  /** Класс триггера (например ширина). */
  className?: string;
  /** Доступная подпись, если рядом нет <label>. */
  'aria-label'?: string;
  /** Подпись поля. Задана — контрол переходит в слой формы (outlined-рамка с вырезом). */
  label?: string;
  /** Красная рамка без сообщения (сообщение рисует вызывающий код). Только в слое формы. */
  invalid?: boolean;
  /** Сообщение об ошибке под полем; заодно красит рамку. Только в слое формы. */
  error?: string;
  /** Вспомогательный текст под полем. Только в слое формы. */
  hint?: string;
  /** Классы внешней обёртки поля (ширина, `col-span`). Только в слое формы. */
  containerClassName?: string;
  children: ReactNode;
}

const TRIGGER =
  'inline-flex items-center justify-between gap-2 w-full h-9 px-3 rounded-md border border-stroke-strong ' +
  'bg-surface text-sm text-fg1 transition-colors data-[placeholder]:text-fg4 ' +
  'focus:outline-none focus-visible:ring-2 focus-visible:ring-brand data-[state=open]:ring-2 ' +
  'data-[state=open]:ring-brand disabled:opacity-50 disabled:pointer-events-none';

// В слое формы рамку рисует оболочка — триггер внутри неё прозрачный и без своей границы, иначе
// вышла бы рамка в рамке, а непрозрачный фон затёр бы вырез на не-surface подложке (та же ловушка,
// что у пикера типа). Кольцо фокуса там тоже лишнее: фокус показывает сама оболочка.
const FRAMED_TRIGGER =
  'inline-flex items-center justify-between gap-2 w-full h-14 px-4 rounded-md bg-transparent ' +
  'text-sm text-fg1 data-[placeholder]:text-fg4 outline-none ' +
  'disabled:opacity-50 disabled:pointer-events-none';

export function Select({
  value, onValueChange, placeholder, disabled, required, name, className = '',
  label, invalid, error, hint, containerClassName = '', children, ...aria
}: SelectProps) {
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const labelId = useId();
  const framed = label !== undefined;

  const control = (
    <RS.Root value={value} onValueChange={onValueChange} disabled={disabled} required={required}
      name={name} onOpenChange={setOpen}>
      <RS.Trigger className={`${framed ? FRAMED_TRIGGER : TRIGGER} ${className}`}
        aria-label={framed ? undefined : aria['aria-label']}
        aria-labelledby={framed ? labelId : undefined}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        {/* min-w-0 + truncate: длинное выбранное значение обрезается многоточием, а не переносится
            на вторую строку, распирая триггер фиксированной высоты. */}
        <span className="min-w-0 flex-1 truncate text-left"><RS.Value placeholder={placeholder} /></span>
        <RS.Icon className="text-fg4 shrink-0"><ChevronDown size={15} /></RS.Icon>
      </RS.Trigger>
      <RS.Portal>
        <RS.Content position="popper" sideOffset={4}
          className="z-50 min-w-[var(--radix-select-trigger-width)] max-h-[var(--radix-select-content-available-height)] overflow-hidden rounded-md border border-stroke bg-surface shadow-[var(--f-shadow16)]">
          <RS.Viewport className="p-1">
            {children}
          </RS.Viewport>
        </RS.Content>
      </RS.Portal>
    </RS.Root>
  );

  if (!framed) return control;

  // Пока список открыт, фокус живёт в нём, а не на триггере — без `open` рамка гасла бы ровно на то
  // время, что человек выбирает значение.
  return (
    <OutlinedField label={label} required={required} invalid={invalid} error={error} hint={hint}
      raise="always" focused={focused || open} labelId={labelId} containerClassName={containerClassName}>
      {control}
    </OutlinedField>
  );
}

const ITEM =
  'relative flex items-center gap-2 pl-3 pr-8 py-1.5 rounded text-sm text-fg1 select-none cursor-pointer ' +
  'outline-none data-[highlighted]:bg-brand-subtle data-[highlighted]:text-brand-hover ' +
  'data-[state=checked]:font-medium data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

export const SelectItem = forwardRef<HTMLDivElement, { value: string; disabled?: boolean; children: ReactNode; className?: string }>(
  function SelectItem({ value, disabled, children, className = '' }, ref) {
    return (
      <RS.Item ref={ref} value={value} disabled={disabled} className={`${ITEM} ${className}`}>
        <RS.ItemText>{children}</RS.ItemText>
        <RS.ItemIndicator className="absolute right-2 inline-flex text-brand">
          <Check size={14} />
        </RS.ItemIndicator>
      </RS.Item>
    );
  },
);

/** Группа опций (аналог <optgroup>). */
export function SelectGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <RS.Group>
      <RS.Label className="px-3 pt-2 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4">{label}</RS.Label>
      {children}
    </RS.Group>
  );
}
