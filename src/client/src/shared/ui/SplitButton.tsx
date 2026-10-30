import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { useEffect, useRef, type ReactNode } from 'react';
import { BASE, HEIGHT, SIZE, variantClass } from './buttonClasses';
import { CircularProgress } from './CircularProgress';
import { Icon } from './Icon';
import { KeyboardArrowDown } from './icons';
import { MENU_ITEM, MENU_ITEM_OFF, MENU_SURFACE } from './menuClasses';
import { Tooltip } from './Tooltip';
import { focusFromLayer } from './layerFocus';

export interface SplitButtonItem {
  key: string;
  label: string;
  /** Вторая строка пункта — что выбор сделает. */
  description?: string;
  icon?: ReactNode;
  onSelect: () => void;
  disabled?: boolean;
}

export interface SplitButtonProps {
  /** Подпись основной части. */
  children: ReactNode;
  /** Ведущий знак основной части. При `loading` его место занимает индикатор хода. */
  icon?: ReactNode;
  onClick: () => void;
  /** Подсказка основной части — как у `Button`: у недоступной она и объясняет, почему нельзя. */
  title?: string;
  /** Вида `text` у split-кнопки нет и в MD3: без заливки половины не читались бы парой. */
  variant?: 'filled' | 'tonal' | 'outlined';
  /** На обе части. */
  disabled?: boolean;
  /** Ход — в основной части; обе части недоступны. */
  loading?: boolean;
  /** Имя стрелки для диктора и её подсказка. */
  menuLabel: string;
  /** Варианты того же действия. Нет вариантов — нет split: ставьте `Button`. */
  items: SplitButtonItem[];
}

// Переход формы — добавкой к переходу слоя состояния (`state-layer` в BASE): свой класс transition-*
// отнял бы transition-property у слоя или слой у него, смотря по порядку утилит (src/index.css).
const MORPH = '[--_also-transition:border-radius]';
const itemCls = `flex items-start gap-2 px-3 py-2 ${MENU_ITEM}`;

/**
 * Split-кнопка M3 Expressive ([UI-83]): основная часть делает действие одним нажатием, стрелка
 * открывает меню вариантов того же действия. Две кнопки с зазором 2 px; внешние углы полные, внутренние
 * малые (нажатая — чуть больше), стрелка у открытого меню круглая и смотрит вверх.
 *
 * <p>Вид половин — из общих с `Button` констант (`buttonClasses`), а не перекрытием классов `Button` или
 * `IconButton`: у половины нет двух классов одного свойства, и правка кнопки переходит сюда сама. Тени
 * нет — на каждой половине она легла бы швом в зазор.</p>
 *
 * <p>Стрелка — триггер Radix `DropdownMenu`: имя, «меню», «развёрнуто», клавиатура, Esc и возврат фокуса
 * — его. Недоступная стрелка остаётся настоящим `disabled` (меню не открыть ни мышью, ни клавиатурой) и
 * подсказки не показывает; причину называет основная часть.</p>
 */
export function SplitButton({
  children, icon, onClick, title, variant = 'filled', disabled = false, loading = false, menuLabel, items,
}: SplitButtonProps) {
  const off = disabled || loading;
  const look = `${BASE} ${variantClass(variant)} ${MORPH}`;
  const arrow = useRef<HTMLButtonElement>(null);
  // Выбранный пункт ждёт, пока меню закроется и фокус вернётся на стрелку: окно, открытое пунктом,
  // запоминает фокус при открытии и возвращает его туда же — иначе это снятый пункт, и фокус уходит в body.
  const chosen = useRef<SplitButtonItem | null>(null);
  useEffect(() => {
    if (items.length === 0 && import.meta.env.DEV) console.error('SplitButton без вариантов — это обычная Button.');
  }, [items.length]);

  return (
    <div className="inline-flex shrink-0 gap-0.5">
      <Tooltip content={title}>
        <button type="button" onClick={onClick} disabled={off}
          className={`${look} ${SIZE.md} whitespace-nowrap rounded-l-full rounded-r-xs active:rounded-r-sm`}>
          {loading ? <CircularProgress size={18} className="shrink-0" /> : icon}
          {children}
        </button>
      </Tooltip>
      {items.length > 0 && (
        <DropdownMenu.Root>
          <Tooltip name content={menuLabel}>
            <DropdownMenu.Trigger asChild disabled={off}>
              <button ref={arrow} type="button"
                className={`group ${look} ${HEIGHT.md} w-11 rounded-r-full rounded-l-xs active:rounded-l-sm data-[state=open]:rounded-l-full`}>
                <Icon icon={KeyboardArrowDown} size={20}
                  className="transition-transform duration-(--md-sys-motion-duration-short3) ease-standard group-data-[state=open]:rotate-180" />
              </button>
            </DropdownMenu.Trigger>
          </Tooltip>
          <DropdownMenu.Portal>
            <DropdownMenu.Content align="end" sideOffset={4} className={`z-50 min-w-44 max-w-80 ${MENU_SURFACE}`}
              onCloseAutoFocus={e => {
                const item = chosen.current;
                if (!item) return;
                chosen.current = null;
                e.preventDefault();
                if (arrow.current) focusFromLayer(arrow.current);
                item.onSelect();
              }}>
              {items.map(item => {
                const dim = item.disabled ? MENU_ITEM_OFF : '';
                return (
                  <DropdownMenu.Item key={item.key} className={itemCls} disabled={item.disabled}
                    onSelect={() => { chosen.current = item; }}>
                    {item.icon && <span className={`shrink-0 mt-0.5 ${dim || 'text-on-surface-variant'}`}>{item.icon}</span>}
                    <span>
                      <span className={`block text-body-medium ${dim || 'text-on-surface'}`}>{item.label}</span>
                      {item.description && (
                        <span className={`block text-body-small ${dim || 'text-on-surface-variant'}`}>{item.description}</span>
                      )}
                    </span>
                  </DropdownMenu.Item>
                );
              })}
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      )}
    </div>
  );
}
