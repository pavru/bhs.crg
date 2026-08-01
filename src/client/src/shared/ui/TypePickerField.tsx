import { useId, useState } from 'react';
import { Boxes, Search, X } from 'lucide-react';
import { TypePicker, typeIcon, type PickType } from './TypePicker';
import { OutlinedField } from './OutlinedField';

/**
 * Триггер-поле над `TypePicker` (issue #266): закрытый вид читается как поле формы (значок
 * семейства + имя выбранного типа + код по showCode + лупа), клик/Enter/Space открывают богатую
 * модалку выбора. Единый контрол для ЛЮБОГО выбора типа (документа/поля/родителя), чтобы не плодить
 * свои триггеры на каждом сайте. Триггер — настоящий `<button aria-haspopup>`, фокус возвращается
 * на него по Esc (Radix Dialog). Не-типовые короткие списки (роль/scope) — обычный Select.
 *
 * Замыкающий значок — ЛУПА, а не шеврон (issue #565). Обещание о том, что произойдёт по клику,
 * несёт именно он: шеврон в MD3 означает приклеенное меню, а у нас открывается модалка с поиском,
 * группами и «недавними». Оболочку поля при этом можно носить честно — врал только значок.
 *
 * Два слоя (issue #565), и выбирает между ними наличие `label`:
 *
 * - **слой формы** (`label` задан) — MD3 outlined-оболочка `OutlinedField` высотой `h-14`, подпись
 *   всегда в вырезе рамки. Свой `<label>` в вызывающем коде НЕ пишем: он разъехался на три разные
 *   типографики, пока жил снаружи. Подпись связана через `aria-labelledby`, а не `<label for>` —
 *   у кнопки щелчок по подписи открывал бы модалку;
 * - **слой обвязки** (`label` нет) — компактный триггер `h-8`/`h-9` для шапок и фильтров, там и
 *   живёт плотность.
 */
export function TypePickerField({
  types, value, onChange, recentKey, title = 'Выберите тип', placeholder = 'Выберите тип',
  size = 'md', label, required, clearable, disabled, className = '', 'aria-label': ariaLabel,
}: {
  types: PickType[];
  value: string | undefined;
  /** id выбранного типа, либо null при выборе «нет значения» (только при `clearable`). */
  onChange: (id: string | null) => void;
  recentKey?: string;
  title?: string;
  placeholder?: string;
  /** `sm` — компактный триггер для шапок (без кода); `md` — обычное поле формы. */
  size?: 'sm' | 'md';
  /** Подпись поля. Задана — контрол переходит в слой формы (outlined-рамка с вырезом). */
  label?: string;
  required?: boolean;
  /** Показать строку «нет значения» в пикере + крестик-сброс на триггере. */
  clearable?: { label: string };
  disabled?: boolean;
  className?: string;
  'aria-label'?: string;
}) {
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const labelId = useId();
  const selected = value ? types.find(t => t.id === value) : undefined;
  const Icon = selected ? typeIcon(selected) : Boxes;
  const code = selected?.code.trim();
  const showCode = size !== 'sm' && !!code && code.toLowerCase() !== selected!.name.trim().toLowerCase();
  const framed = label !== undefined;
  const showClear = !!clearable && !!selected && !disabled;

  // В слое формы рамку и фон рисует оболочка — триггер внутри неё прозрачный и без своей границы,
  // иначе получилась бы рамка в рамке. Кольцо фокуса там тоже не нужно: фокус показывает сама
  // оболочка (border-2 + подпись цветом бренда), как у `DateField`.
  const triggerClass = framed
    ? `w-full h-14 px-4 rounded-md bg-transparent text-sm text-left outline-none ` +
      `disabled:opacity-50 disabled:pointer-events-none ${showClear ? 'pr-12' : ''}`
    : `w-full ${size === 'sm' ? 'h-8' : 'h-9'} px-3 rounded-md border border-stroke-strong bg-surface ` +
      `text-sm text-left transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-brand ` +
      `data-[state=open]:ring-2 disabled:opacity-50 disabled:pointer-events-none ${showClear ? 'pr-11' : ''}`;

  const trigger = (
    <button
      type="button" disabled={disabled} onClick={() => setOpen(true)}
      onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
      aria-haspopup="dialog" aria-expanded={open}
      aria-label={framed ? undefined : ariaLabel} aria-labelledby={framed ? labelId : undefined}
      className={`inline-flex items-center gap-2 ${triggerClass}`}
      data-state={open ? 'open' : 'closed'}
    >
      <Icon size={16} className={`shrink-0 ${selected ? 'text-fg3' : 'text-fg4'}`} />
      {/* Подсказка — на самом обрезаемом тексте, а не на кнопке (issue #548): подсказку кнопки
          наследуют и лупа, и код типа, и всплывало бы имя типа поверх них. */}
      <span className={`flex-1 truncate ${selected ? 'text-fg1' : 'text-fg4'}`}
        title={selected ? (showCode ? `${selected.name} (${code})` : selected.name) : undefined}>
        {selected ? selected.name : placeholder}
      </span>
      {showCode && <span className="text-[11px] font-mono text-fg4 shrink-0">{code}</span>}
      <Search size={15} className="shrink-0 text-fg4" />
    </button>
  );

  // «Очистить» — отдельная кнопка РЯДОМ с триггером, а не `<span role="button">` внутри него
  // (issue #565): вложенный интерактив невалиден, и с клавиатуры до него было не добраться.
  // Лишняя остановка Tab здесь правильная — действие полноценное.
  const clear = showClear && (
    <button type="button" aria-label="Очистить" title="Очистить"
      onClick={() => onChange(null)}
      className={`absolute ${framed ? 'right-8' : 'right-7'} top-1/2 -translate-y-1/2 z-10 text-fg4 ` +
        `opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 hover:text-fg2 transition-opacity ` +
        `focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand rounded-sm`}>
      <X size={14} />
    </button>
  );

  return (
    <>
      {framed ? (
        <OutlinedField label={label} required={required} raise="always" focused={focused}
          labelId={labelId} containerClassName={`group relative ${className}`}>
          {trigger}
          {clear}
        </OutlinedField>
      ) : (
        <div className={`group relative inline-flex ${className}`}>
          {trigger}
          {clear}
        </div>
      )}

      <TypePicker
        open={open} onOpenChange={setOpen} title={title} recentKey={recentKey}
        types={types} onSelect={id => onChange(id)}
        noneOption={clearable} onSelectNone={clearable ? () => onChange(null) : undefined}
      />
    </>
  );
}
