import { Icon } from './Icon';
import { Close, Search } from './icons';

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
      <Icon icon={Search} size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant pointer-events-none" />
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={e => { if (e.key === 'Escape' && value) { e.preventDefault(); e.stopPropagation(); onChange(''); } }}
        placeholder={placeholder}
        aria-label={ariaLabel ?? 'Поиск'}
        autoFocus={autoFocus}
        data-search=""
        className={`w-full h-10 pl-9 pr-9 text-sm bg-surface border border-outline text-on-surface outline-none focus-visible:ring-2 focus-visible:ring-primary placeholder:text-on-surface-variant ${rounded === 'full' ? 'rounded-full' : 'rounded-sm'} ${className}`}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Очистить поиск"
          className="absolute right-2.5 top-1/2 -translate-y-1/2 rounded-full text-on-surface-variant state-layer focus-ring"
        >
          <Icon icon={Close} size={18} />
        </button>
      )}
    </div>
  );
}
