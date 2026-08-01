import { useState } from 'react';
import { Boxes, Search, X } from 'lucide-react';
import { TypePicker, typeIcon, type PickType } from './TypePicker';

/**
 * Триггер-поле над `TypePicker` (issue #266): закрытый вид читается как form-поле в тон нашего
 * `Select` (значок семейства + имя выбранного типа + код по showCode + лупа), клик/Enter/Space
 * открывают богатую модалку выбора.
 *
 * Замыкающий значок — ЛУПА, а не шеврон (issue #565). Обещание о том, что произойдёт по клику,
 * несёт именно он: шеврон в MD3 означает приклеенное меню, а у нас открывается модалка с поиском,
 * группами и «недавними». Оболочку поля при этом можно носить честно — врал только значок. Единый контрол для ЛЮБОГО выбора типа (документа/поля/родителя),
 * чтобы не плодить свои триггеры на каждом сайте. Триггер — настоящий `<button aria-haspopup>`, фокус
 * возвращается на него по Esc (Radix Dialog). Не-типовые короткие списки (роль/scope) — обычный Select.
 */
export function TypePickerField({
  types, value, onChange, recentKey, title = 'Выберите тип', placeholder = 'Выберите тип',
  size = 'md', clearable, disabled, className = '', 'aria-label': ariaLabel,
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
  /** Показать строку «нет значения» в пикере + крестик-сброс на триггере. */
  clearable?: { label: string };
  disabled?: boolean;
  className?: string;
  'aria-label'?: string;
}) {
  const [open, setOpen] = useState(false);
  const selected = value ? types.find(t => t.id === value) : undefined;
  const Icon = selected ? typeIcon(selected) : Boxes;
  const code = selected?.code.trim();
  const showCode = size !== 'sm' && !!code && code.toLowerCase() !== selected!.name.trim().toLowerCase();
  const h = size === 'sm' ? 'h-8' : 'h-9';

  return (
    <>
      <button
        type="button" disabled={disabled} onClick={() => setOpen(true)}
        aria-haspopup="dialog" aria-label={ariaLabel} aria-expanded={open}
        className={`group inline-flex items-center gap-2 ${h} px-3 rounded-md border border-stroke-strong ` +
          `bg-surface text-sm text-left transition-colors focus:outline-none focus-visible:ring-2 ` +
          `focus-visible:ring-brand data-[state=open]:ring-2 disabled:opacity-50 disabled:pointer-events-none ${className}`}
        data-state={open ? 'open' : 'closed'}
      >
        <Icon size={16} className={`shrink-0 ${selected ? 'text-fg3' : 'text-fg4'}`} />
        {/* Подсказка — на самом обрезаемом тексте, а не на кнопке (issue #548): подсказку кнопки
            наследуют и крестик, и шеврон, и на «Очистить» всплывало бы имя типа. */}
        <span className={`flex-1 truncate ${selected ? 'text-fg1' : 'text-fg4'}`}
          title={selected ? (showCode ? `${selected.name} (${code})` : selected.name) : undefined}>
          {selected ? selected.name : placeholder}
        </span>
        {showCode && <span className="text-[11px] font-mono text-fg4 shrink-0">{code}</span>}
        {clearable && selected && !disabled && (
          <span role="button" tabIndex={-1} aria-label="Очистить" title="Очистить"
            onClick={e => { e.stopPropagation(); onChange(null); }}
            className="shrink-0 text-fg4 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 hover:text-fg2 transition-opacity">
            <X size={14} />
          </span>
        )}
        <Search size={15} className="shrink-0 text-fg4" />
      </button>

      <TypePicker
        open={open} onOpenChange={setOpen} title={title} recentKey={recentKey}
        types={types} onSelect={id => onChange(id)}
        noneOption={clearable} onSelectNone={clearable ? () => onChange(null) : undefined}
      />
    </>
  );
}
