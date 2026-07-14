import * as RS from '@radix-ui/react-select';
import { Check, ChevronDown } from 'lucide-react';
import { forwardRef, type ReactNode } from 'react';

/**
 * MD3-Select (issue #110, фаза 2) поверх Radix Select — замена нативному `<select>`
 * (дефект «смесь нативных и кастомных контролов» из хендоффа). Radix даёт полный
 * APG-listbox с клавиатуры (стрелки/Home/End/typeahead/Esc) — закрывает #107 F5.
 * Outlined-поле в стиле MD3, кольцо фокуса, тема light/dark через токены.
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
  children: ReactNode;
}

const TRIGGER =
  'inline-flex items-center justify-between gap-2 w-full h-9 px-3 rounded-md border border-stroke-strong ' +
  'bg-surface text-sm text-fg1 transition-colors data-[placeholder]:text-fg4 ' +
  'focus:outline-none focus-visible:ring-2 focus-visible:ring-brand data-[state=open]:ring-2 ' +
  'data-[state=open]:ring-brand disabled:opacity-50 disabled:pointer-events-none';

export function Select({
  value, onValueChange, placeholder, disabled, required, name, className = '', children, ...aria
}: SelectProps) {
  return (
    <RS.Root value={value} onValueChange={onValueChange} disabled={disabled} required={required} name={name}>
      <RS.Trigger className={`${TRIGGER} ${className}`} aria-label={aria['aria-label']}>
        <RS.Value placeholder={placeholder} />
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
