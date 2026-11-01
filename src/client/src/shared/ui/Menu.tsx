import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { createContext, useContext, useState, type CSSProperties, type ReactElement, type ReactNode } from 'react';
import { Link } from 'react-router';
import { CircularProgress } from './CircularProgress';
import { Icon } from './Icon';
import { Check, ChevronRight } from './icons';
import type { IconDef } from './icons/types';
import { useHistoryLayer } from './historyLayers';
import { markLayerFocus } from './layerFocus';
import { useGuardedNavClick } from './NavigationGuard';
import { LAYER_SURFACE } from './menuClasses';
import { Tooltip } from './Tooltip';

/** Слой меню и подменю: радиус 4 и отступ 8 сверху и снизу — пункты от края. */
const MENU_SURFACE = `rounded-xs py-2 ${LAYER_SURFACE}`;

// Подсветка пункта — слоем on-surface ([UI-64]), мышью и стрелкой одинаково (data-highlighted Radix).
const MENU_ITEM = 'cursor-pointer select-none outline-none state-layer data-[disabled]:pointer-events-none';

/** Плотное меню — пункт 40 (D7): в таблице, тулбаре, просмотрщике. Подменю наследует плотность. */
const DenseContext = createContext(false);

type ContentProps = Omit<DropdownMenu.DropdownMenuContentProps, 'children' | 'asChild'>;

export interface MenuProps extends ContentProps {
  /** Кнопка, открывающая меню: один элемент, умеющий ref и обработчики. */
  trigger: ReactElement;
  children: ReactNode;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  /** `false` — страница под меню остаётся живой (просмотрщик: прокрутка листов при открытом масштабе). */
  modal?: boolean;
  dense?: boolean;
  /** Меню не открыть ни мышью, ни клавиатурой. */
  disabled?: boolean;
  /** Подсказка кнопки — оборачивает её вместе с триггером, а не кнопку внутри него (см. `Tooltip`). */
  tooltip?: ReactNode;
  /** Подсказка — ещё и имя кнопки (кнопка из одного знака). */
  tooltipName?: boolean;
  /** Потолок высоты слоя (`20rem`): длинный список прокручивается внутри, а не до края экрана. */
  maxHeight?: string;
  /** Ширина слоя классом (`w-80`, `min-w-64`); по умолчанию — не уже 208. Потолок 320 — у меню всегда. */
  width?: string;
}

/**
 * Меню ([UI-76], sharedui-md3-13): одно на всё приложение — действия строки, меню страницы, раздела и
 * профиля, масштаба, split-кнопки, подменю. Слой — `surface-container`, радиус 4, тень уровня 2, без
 * рамки; экран слой меню не стилизует (сторож — `scripts/menu-layer.test.mjs`). Ширину (`width`), место
 * и прочие свойства слоя (`align`, `data-testid`…) передают сюда же.
 *
 * Клавиатура, портал в body, Esc и щелчок мимо — у Radix. «Назад» на телефоне закрывает меню, а не
 * страницу (issue #1418): слой истории — здесь, у каждого меню, а не у тех, кто вспомнил о нём.
 */
export function Menu({
  trigger, children, open: openProp, onOpenChange, modal, dense = false, disabled, tooltip, tooltipName, maxHeight,
  width = 'min-w-52', className = '', sideOffset = 4, align = 'start', style, onCloseAutoFocus, ...content
}: MenuProps) {
  const [own, setOwn] = useState(false);
  const open = openProp ?? own;
  const setOpen = (next: boolean) => {
    if (openProp === undefined) setOwn(next);
    onOpenChange?.(next);
  };
  useHistoryLayer(open, () => { setOpen(false); return true; });

  const button = <DropdownMenu.Trigger asChild disabled={disabled}>{trigger}</DropdownMenu.Trigger>;
  return (
    <DropdownMenu.Root open={open} onOpenChange={setOpen} modal={modal}>
      {tooltip ? <Tooltip content={tooltip} name={tooltipName}>{button}</Tooltip> : button}
      <DropdownMenu.Portal>
        <DenseContext.Provider value={dense}>
          {/* Фокус, который Radix вернёт кнопке меню, — фокус слоя (`layerFocus.ts`): иначе подсказка
              кнопки открывалась сама, и первый Esc доставался ей, а не странице. */}
          <DropdownMenu.Content sideOffset={sideOffset} align={align} {...content}
            onCloseAutoFocus={e => { markLayerFocus(); onCloseAutoFocus?.(e); }}
            style={{ maxHeight: `min(${maxHeight ?? '100dvh'}, var(--radix-dropdown-menu-content-available-height))`, ...style }}
            className={`z-50 ${width} max-w-80 overflow-y-auto focus:outline-none ${MENU_SURFACE} ${className}`}>
            {children}
          </DropdownMenu.Content>
        </DenseContext.Provider>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

interface ItemLook {
  /** Ведущий знак — символ, а не элемент: размер 24 и цвет задаёт меню. */
  icon?: IconDef;
  /** Идёт запрос пункта: вместо знака — кольцо хода. Пункт гасит вызывающий (`disabled`). */
  loading?: boolean;
  /** Опасное действие — тона ошибки. */
  danger?: boolean;
  /** Строка под подписью: что выбор сделает или почему пункт погашен (подсказки на телефоне нет). */
  description?: ReactNode;
  /** У замыкающего края: клавиша, значение, число. */
  trailing?: ReactNode;
}

function itemClass(dense: boolean, look: ItemLook, extra = '') {
  const height = dense ? 'min-h-10' : 'min-h-12';
  const tone = look.danger ? 'text-error' : 'text-on-surface';
  return `group/mi relative flex items-center gap-3 px-3 ${look.description ? 'py-2' : ''} ${height} text-label-large ${tone} disabled-content ${MENU_ITEM} ${extra}`;
}

// Знак и строка под подписью у погашенного пункта — 38 % on-surface вместе с подписью ([UI-64]).
const SIDE = 'group-data-[disabled]/mi:text-on-surface/38';

function Lead({ look, check }: { look: ItemLook; check?: ReactNode }) {
  if (check !== undefined) return <span className="flex size-6 shrink-0 items-center justify-center text-on-surface">{check}</span>;
  if (look.loading) return <CircularProgress size={18} className="shrink-0 mx-0.5" />;
  if (!look.icon) return null;
  return <Icon icon={look.icon} className={`shrink-0 ${look.danger ? 'text-error' : 'text-on-surface-variant'} ${SIDE}`} />;
}

function Body({ look, label }: { look: ItemLook; label: ReactNode }) {
  return (
    <>
      {look.description ? (
        <span className="flex flex-col min-w-0 flex-1">
          <span className="truncate">{label}</span>
          <span className={`text-body-small text-on-surface-variant ${SIDE}`}>{look.description}</span>
        </span>
      ) : <span className="min-w-0 flex-1 truncate">{label}</span>}
      {look.trailing != null && (
        <span className={`shrink-0 ml-3 text-body-medium text-on-surface-variant ${SIDE}`}>{look.trailing}</span>
      )}
    </>
  );
}

const CHECK = <Icon icon={Check} />;

type ItemProps = Omit<DropdownMenu.DropdownMenuItemProps, 'children' | 'asChild'> & ItemLook & {
  children: ReactNode;
  /** Пункт-ссылка: переход — щелчком ссылки, через охрану несохранённых правок (issue #1238). */
  to?: string;
  /**
   * Отмечен галочкой (`true`) или оставляет ей место (`false`): текущая страница меню раздела. Выбор
   * из вариантов — `MenuRadioItem`, флажок — `MenuCheckboxItem`.
   */
  checked?: boolean;
  /** Пункт ведёт туда, где человек уже есть: `aria-current="page"` у ссылки (галочку ставит `checked`). */
  current?: boolean;
};

/** Пункт меню: высота 48 (плотно — 40), подпись `label-large`, знак 24, подсветка — слоем состояния. */
export function MenuItem({ children, icon, loading, danger, description, trailing, to, checked, current, className = '', ...rest }: ItemProps) {
  const dense = useContext(DenseContext);
  const look = { icon, loading, danger, description, trailing };
  const check = checked === undefined ? undefined : checked ? CHECK : null;
  const inner = <><Lead look={look} check={check} /><Body look={look} label={children} /></>;
  const cls = itemClass(dense, look, className);
  if (to) return <LinkItem to={to} current={current} className={cls} rest={rest}>{inner}</LinkItem>;
  return <DropdownMenu.Item className={cls} {...rest}>{inner}</DropdownMenu.Item>;
}

/** Пункт-ссылка — своим компонентом: охране нужен маршрутизатор, а меню без ссылок живёт и без него. */
function LinkItem({ to, current, className, rest, children }: {
  to: string; current?: boolean; className: string; rest: DropdownMenu.DropdownMenuItemProps; children: ReactNode;
}) {
  const guardedClick = useGuardedNavClick();
  return (
    <DropdownMenu.Item asChild className={className} {...rest}>
      <Link to={to} onClick={guardedClick(to)} aria-current={current ? 'page' : undefined}>{children}</Link>
    </DropdownMenu.Item>
  );
}

type CheckboxProps = Omit<DropdownMenu.DropdownMenuCheckboxItemProps, 'children' | 'asChild'> & Omit<ItemLook, 'icon' | 'loading'> & {
  children: ReactNode;
};

/** Флажок в меню: галочка у ведущего края, место под неё есть и у снятого. */
export function MenuCheckboxItem({ children, danger, description, trailing, className = '', ...rest }: CheckboxProps) {
  const dense = useContext(DenseContext);
  const look = { danger, description, trailing };
  return (
    <DropdownMenu.CheckboxItem className={itemClass(dense, look, className)} {...rest}>
      <Lead look={look} check={<DropdownMenu.ItemIndicator className="contents">{CHECK}</DropdownMenu.ItemIndicator>} />
      <Body look={look} label={children} />
    </DropdownMenu.CheckboxItem>
  );
}

/** Варианты одного выбора: у выбранного `MenuRadioItem` — галочка. */
export function MenuRadioGroup(props: DropdownMenu.DropdownMenuRadioGroupProps) {
  return <DropdownMenu.RadioGroup {...props} />;
}

type RadioProps = Omit<DropdownMenu.DropdownMenuRadioItemProps, 'children' | 'asChild'> & Omit<ItemLook, 'loading'> & {
  children: ReactNode;
};

/** Один из вариантов (`MenuRadioGroup`): галочка у выбранного; знак варианта — у замыкающего края подписи. */
export function MenuRadioItem({ children, icon, danger, description, trailing, className = '', ...rest }: RadioProps) {
  const dense = useContext(DenseContext);
  const look = { danger, description, trailing };
  return (
    <DropdownMenu.RadioItem className={itemClass(dense, look, className)} {...rest}>
      <Lead look={look} check={<DropdownMenu.ItemIndicator className="contents">{CHECK}</DropdownMenu.ItemIndicator>} />
      {icon && <Icon icon={icon} className={`shrink-0 text-on-surface-variant ${SIDE}`} />}
      <Body look={look} label={children} />
    </DropdownMenu.RadioItem>
  );
}

/** Подменю: пункт со стрелкой у замыкающего края, слой — тот же, что у меню. */
export function MenuSub({ label, icon, description, disabled, children }: {
  label: ReactNode;
  icon?: IconDef;
  description?: ReactNode;
  disabled?: boolean;
  children: ReactNode;
}) {
  const dense = useContext(DenseContext);
  const look = { icon, description, trailing: <Icon icon={ChevronRight} size={20} /> };
  return (
    <DropdownMenu.Sub>
      <DropdownMenu.SubTrigger className={itemClass(dense, look)} disabled={disabled}>
        <Lead look={look} /><Body look={look} label={label} />
      </DropdownMenu.SubTrigger>
      <DropdownMenu.Portal>
        <DropdownMenu.SubContent sideOffset={2}
          className={`z-50 min-w-52 max-w-80 max-h-72 overflow-y-auto focus:outline-none ${MENU_SURFACE}`}>
          {children}
        </DropdownMenu.SubContent>
      </DropdownMenu.Portal>
    </DropdownMenu.Sub>
  );
}

/** Группа пунктов под общим `MenuLabel`. */
export function MenuGroup(props: DropdownMenu.DropdownMenuGroupProps) {
  return <DropdownMenu.Group {...props} />;
}

/** Черта между группами: 1 px `outline-variant`, отступ 8 сверху и снизу. */
export function MenuSeparator() {
  return <DropdownMenu.Separator className="my-2 h-px bg-outline-variant" />;
}

/** Заголовок группы: `label-medium`, его стрелки перешагивают. */
export function MenuLabel({ children, className = '', style }: { children: ReactNode; className?: string; style?: CSSProperties }) {
  return <DropdownMenu.Label className={`px-3 pt-2 pb-1 text-label-medium text-on-surface-variant ${className}`} style={style}>{children}</DropdownMenu.Label>;
}

/** Строка без действия: «Нет вариантов», «Роли загружаются…» — пустое меню объясняет, почему пусто. */
export function MenuNote({ children }: { children: ReactNode }) {
  return <p className="px-3 py-2 text-body-small text-on-surface-variant">{children}</p>;
}
