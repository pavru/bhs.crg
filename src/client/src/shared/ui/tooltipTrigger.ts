import { useComposedRefs } from '@radix-ui/react-compose-refs';
import {
  useEffect, useRef, useState,
  type ElementType, type FocusEvent, type KeyboardEvent, type MouseEvent, type PointerEvent, type ReactElement, type Ref,
} from 'react';
import { isLayerFocus } from './layerFocus';

/**
 * Триггер подсказки — общий у `Tooltip` и `RichTooltip` ([UI-72]): наведение с задержкой, фокус с
 * клавиатуры, неактивная кнопка, которая объясняет почему. Хук не рисует элемент сам — он отдаёт тип и
 * пропсы: разметка одна и та же с подсказкой и без, открытой и закрытой, элемент всегда первый, слой —
 * рядом. Условная подсказка (`cond ? 'почему' : undefined`) иначе пересоздавала бы элемент — и он терял
 * бы фокус.
 *
 * <p><b>Закрытая подсказка не стоит ничего, кроме обработчиков на самом элементе.</b> Слой (позиция,
 * портал, Esc) монтируется только открытым, рядом с элементом, а не вокруг него: подсказки стоят в
 * каждой строке длинных списков, и корень Radix Tooltip на каждой делал выбор счёта в списке из
 * тысячи строк на три секунды медленнее (PR 2/2). Нативный `title` такой цены не имел.</p>
 */

/** Вид простой подсказки MD3 ([UI-72]): `inverse-surface`, `body-small`, форма 4, без рамки и тени. */
export const PLAIN_TOOLTIP = 'max-w-60 min-h-6 rounded-xs bg-inverse-surface px-2 py-1 text-body-small text-inverse-on-surface whitespace-pre-line';

const DELAY = 400;
/** Ушли с одной подсказки на соседнюю быстрее этого — она открывается сразу, без задержки. */
const SKIP = 300;
let lastClosed = 0;
const noteClosed = () => { lastClosed = Date.now(); };
const justClosed = () => Date.now() - lastClosed < SKIP;

/** Виден ли фокус. Движок без `:focus-visible` (старый браузер, тестовый DOM) — считаем видимым. */
function focusVisible(el: Element): boolean {
  try { return el.matches(':focus-visible'); }
  catch { return true; }
}

/**
 * Неактивная кнопка, которая говорит почему: `aria-disabled`, а не `disabled`. Настоящий `disabled` не
 * получает ни наведения, ни фокуса — причину не увидеть ни мышью, ни с клавиатуры, а обёртка вокруг неё
 * сбивала раскладку и пересоздавала кнопку на каждом переключении (ревью #1396). Такая кнопка остаётся
 * в Tab, скринридер называет её недоступной, нажатие ничего не делает — и форму не отправляет: щелчок
 * отменён.
 *
 * <p>Вид — тот, что кнопке задан для неактивной. Отключённое MD3 (`disabled-container`,
 * `disabled-content`) и слой состояния ловят `aria-disabled` сами ([UI-64]). Классы с `disabled:`
 * становятся безусловными, а `disabled:pointer-events-none` и подсветка наведения и нажатия уходят:
 * они звали бы нажать то, что не нажимается, или прятали бы от мыши саму подсказку.</p>
 */
const looksDisabled = (cls: string) => cls.split(/\s+/)
  .filter(c => c && c !== 'disabled:pointer-events-none' && !/^(hover|active):/.test(c))
  .map(c => c.replace(/^disabled:/, ''))
  .join(' ');

/** То, что подсказка читает у элемента и дополняет своим. */
interface ChildProps {
  ref?: Ref<HTMLElement>;
  disabled?: boolean;
  className?: string;
  'aria-label'?: unknown;
  onClick?: (e: MouseEvent<HTMLElement>) => void;
  onPointerEnter?: (e: PointerEvent<HTMLElement>) => void;
  onPointerLeave?: (e: PointerEvent<HTMLElement>) => void;
  onPointerDown?: (e: PointerEvent<HTMLElement>) => void;
  onFocus?: (e: FocusEvent<HTMLElement>) => void;
  onBlur?: (e: FocusEvent<HTMLElement>) => void;
  onKeyDown?: (e: KeyboardEvent<HTMLElement>) => void;
}

export interface TooltipTriggerOptions {
  /** Подсказки нет — элемент как есть, только без пересоздания. */
  empty: boolean;
  /** Имя или описание, которое подсказка даёт элементу. */
  aria: Record<string, string>;
  /** Подсказка открывается — богатая снимает тут отложенное закрытие. */
  onShow?: () => void;
  /**
   * Указатель ушёл с элемента, фокус ушёл с него. Без своих — подсказка закрывается сразу; богатая с
   * действием даёт перейти в неё саму.
   */
  onLeave?: (e: PointerEvent<HTMLElement>) => void;
  onBlur?: (e: FocusEvent<HTMLElement>) => void;
  onKeyDown?: (e: KeyboardEvent<HTMLElement>) => void;
}

export function useTooltipTrigger(children: ReactElement, opt: TooltipTriggerOptions) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLElement | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const own = children.props as ChildProps;
  const ref = useComposedRefs(anchor, own.ref);
  useEffect(() => () => clearTimeout(timer.current), []);

  // Только своя кнопка: `disabled` у триггера меню (`DropdownMenu.Trigger disabled`) — его решение,
  // снятый он открыл бы меню, которое закрыто нарочно.
  const reason = !opt.empty && !!own.disabled && typeof children.type === 'string';

  const show = (now: boolean) => {
    if (opt.empty) return;
    clearTimeout(timer.current);
    opt.onShow?.();
    if (now || justClosed()) setOpen(true);
    else timer.current = setTimeout(() => setOpen(true), DELAY);
  };
  const hide = () => {
    clearTimeout(timer.current);
    if (open) noteClosed();
    setOpen(false);
  };

  const props: ChildProps & Record<string, unknown> = {
    ...own,
    ref,
    ...opt.aria,
    ...(reason && {
      disabled: false,
      'aria-disabled': true,
      className: looksDisabled(own.className ?? ''),
    } as ChildProps),
    // Свой обработчик элемента — первым, подсказкин — следом.
    onClick: (e: MouseEvent<HTMLElement>) => {
      if (reason) e.preventDefault(); else own.onClick?.(e);
      hide();
    },
    onPointerEnter: (e: PointerEvent<HTMLElement>) => { own.onPointerEnter?.(e); if (e.pointerType !== 'touch') show(false); },
    onPointerLeave: (e: PointerEvent<HTMLElement>) => { own.onPointerLeave?.(e); if (opt.onLeave) opt.onLeave(e); else hide(); },
    onPointerDown: (e: PointerEvent<HTMLElement>) => { own.onPointerDown?.(e); hide(); },
    // Фокус открывает подсказку, только когда он виден — пришёл с клавиатуры. Фокус, который вернуло
    // окно (выход с шага к открывшей его кнопке), иначе открыл бы её сам — и первый Esc достался бы
    // подсказке, а не окну. Фокус, поставленный открывшимся слоем, — тоже не зов (см. `layerFocus.ts`).
    onFocus: (e: FocusEvent<HTMLElement>) => {
      own.onFocus?.(e);
      if (!isLayerFocus() && focusVisible(e.currentTarget)) show(true);
    },
    onBlur: (e: FocusEvent<HTMLElement>) => { own.onBlur?.(e); if (opt.onBlur) opt.onBlur(e); else hide(); },
    ...((own.onKeyDown || opt.onKeyDown) && {
      onKeyDown: (e: KeyboardEvent<HTMLElement>) => { own.onKeyDown?.(e); opt.onKeyDown?.(e); },
    }),
  };

  return { open, hide, anchor, Element: children.type as ElementType, props };
}
