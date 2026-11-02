import { useContext } from 'react';
import { Icon } from './Icon';
import { Close, Search } from './icons';
import { IconButton } from './Button';
import { ICON_BUTTON_XS } from './buttonClasses';
import { InLayerContext } from './layerPlace';

/**
 * Единое поле поиска списка (issue #249): иконка-лупа слева, кнопка очистки (✕) справа при непустом
 * значении, Escape очищает. Один источник правды для всех текстовых поисков — чтобы «очистка» была
 * везде одинаковой. Для комбобокс-пикеров с автофокусом использовать НЕ обязательно.
 *
 * Вид — MD3 search bar (sharedui-md3-15): форма full, `surface-container-high`, без рамки. На странице —
 * 56 везде, в том числе в ряду с чипами (выбор владельца); в окне и листе — плотное 40: место говорит
 * `InLayerContext`, а не вызывающий.
 */
export function SearchInput({
  value, onChange, placeholder = 'Поиск…', ariaLabel, className = '', autoFocus,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  ariaLabel?: string;
  className?: string;
  autoFocus?: boolean;
}) {
  const dense = useContext(InLayerContext);
  return (
    <div className="relative">
      <Icon icon={Search} size={dense ? 20 : 24}
        className={`absolute top-1/2 -translate-y-1/2 text-on-surface-variant pointer-events-none ${dense ? 'left-3' : 'left-4'}`} />
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={e => { if (e.key === 'Escape' && value) { e.preventDefault(); e.stopPropagation(); onChange(''); } }}
        placeholder={placeholder}
        aria-label={ariaLabel ?? 'Поиск'}
        autoFocus={autoFocus}
        data-search=""
        className={`w-full rounded-full bg-surface-container-high text-on-surface outline-none focus-visible:ring-2 focus-visible:ring-primary placeholder:text-on-surface-variant ${
          dense ? 'h-10 pl-10 pr-10 text-body-medium' : 'h-14 pl-14 pr-12 text-body-large'} ${className}`}
      />
      {value && (
        // Кнопка внутри поля — место XS ([UI-68]): 32 при мыши, 40 при касании.
        <span className={`absolute top-1/2 -translate-y-1/2 ${dense ? 'right-1' : 'right-3'} ${ICON_BUTTON_XS}`}>
          <IconButton label="Очистить поиск" icon={Close} onClick={() => onChange('')} />
        </span>
      )}
    </div>
  );
}
