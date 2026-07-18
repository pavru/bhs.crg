import { Search, X } from 'lucide-react';

/**
 * Единое поле поиска списка (issue #249): иконка-лупа слева, кнопка очистки (✕) справа при непустом
 * значении, Escape очищает. Один источник правды для всех текстовых поисков — чтобы «очистка» была
 * везде одинаковой. Форма настраивается через `rounded`/`className` (прямоугольная в тулбарах,
 * пилюля в шапке рейла). Для комбобокс-пикеров с автофокусом использовать НЕ обязательно.
 */
export function SearchInput({
  value, onChange, placeholder = 'Поиск…', ariaLabel, className = '',
  rounded = 'md', autoFocus,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  ariaLabel?: string;
  className?: string;
  rounded?: 'md' | 'full';
  autoFocus?: boolean;
}) {
  return (
    <div className="relative">
      <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={e => { if (e.key === 'Escape' && value) { e.preventDefault(); onChange(''); } }}
        placeholder={placeholder}
        aria-label={ariaLabel ?? 'Поиск'}
        autoFocus={autoFocus}
        className={`w-full h-10 pl-9 pr-9 text-sm bg-surface border border-stroke-strong text-fg1 outline-none focus-visible:ring-2 focus-visible:ring-brand placeholder:text-fg4 ${rounded === 'full' ? 'rounded-full' : 'rounded-md'} ${className}`}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Очистить поиск"
          className="absolute right-2.5 top-1/2 -translate-y-1/2 text-fg4 hover:text-fg2 transition-colors"
        >
          <X size={15} />
        </button>
      )}
    </div>
  );
}
