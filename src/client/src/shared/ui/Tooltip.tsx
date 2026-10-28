import * as Popper from '@radix-ui/react-popper';
import { Portal } from '@radix-ui/react-portal';
import { DismissableLayer } from '@radix-ui/react-dismissable-layer';
import { useComposedRefs } from '@radix-ui/react-compose-refs';
import {
  useEffect, useRef, useState,
  type ElementType, type FocusEvent, type MouseEvent, type PointerEvent, type ReactElement, type ReactNode, type Ref, type RefObject,
} from 'react';

/**
 * Всплывающая подсказка в нашем стиле вместо нативного `title` (issue #1394, docs/UI.md).
 *
 * <p>Показывается при наведении и при фокусе с клавиатуры — нативный `title` фокуса не замечает;
 * Esc закрывает её, а не окно под ней. На телефоне наведения нет: в подсказке не должно быть ничего,
 * без чего нельзя работать.</p>
 *
 * <p>Вид — на токенах всплывающего слоя: поверхность, текст `caption`, радиус 4 px, тень `shadow16`.
 * Сквозь подсказку щелчок проходит (`pointer-events-none`), а наведение на неё не держит её открытой:
 * иначе щелчок по ней, лежащей вне окна, окно прочло бы как промах мимо него.</p>
 *
 * <p><b>Закрытая подсказка не стоит ничего, кроме обработчиков на самом элементе.</b> Слой (позиция,
 * портал, Esc) монтируется только открытым, рядом с элементом, а не вокруг него: подсказки стоят в
 * каждой строке длинных списков, и корень Radix Tooltip на каждой делал выбор счёта в списке из
 * тысячи строк на три секунды медленнее (PR 2/2). Нативный `title` такой цены не имел.</p>
 */

const DELAY = 400;
/** Ушли с одной подсказки на соседнюю быстрее этого — она открывается сразу, без задержки. */
const SKIP = 300;
let lastClosed = 0;
const noteClosed = () => { lastClosed = Date.now(); };
const justClosed = () => Date.now() - lastClosed < SKIP;

/**
 * Фокус, который слой Radix ставит сам, открываясь (поповер, окно, меню — `FocusScope`): первый
 * фокусируемый элемент внутри. Открыли слой с клавиатуры — такой фокус ещё и «виден», и подсказка
 * первой кнопки открывалась сама: первый Esc доставался ей, слой закрывался только со второго (ревью
 * PR #1450). Человек туда фокус не вёл — подсказку он не звал.
 *
 * Слой объявляет свой автофокус событием `focusScope.autoFocusOnMount` и сразу, в том же заходе,
 * ставит фокус, синхронно. Событие не всплывает, но перехват на документе его видит — отметка живёт
 * до ближайшей микрозадачи, то есть ровно этот синхронный шаг.
 */
let layerAutoFocus = false;
if (typeof document !== 'undefined') {
  document.addEventListener('focusScope.autoFocusOnMount', () => {
    layerAutoFocus = true;
    queueMicrotask(() => { layerAutoFocus = false; });
  }, true);
}

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
 * <p>Вид — тот, что кнопке задан для неактивной: `disabled:opacity-40` становится `opacity-40`, а
 * `disabled:pointer-events-none` и подсветка наведения и нажатия уходят — они звали бы нажать то, что
 * не нажимается, или прятали бы от мыши саму подсказку.</p>
 */
const looksDisabled = (cls: string) => cls.split(/\s+/)
  .filter(c => c && c !== 'disabled:pointer-events-none' && !/^(dark:)?(hover|active):/.test(c))
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
}

interface TooltipProps {
  /** Текст подсказки; пустой — подсказки нет. */
  content: ReactNode;
  /**
   * Один элемент, умеющий ref и обработчики (кнопка, ссылка, span, триггер меню). Неактивная своя
   * кнопка (`<button disabled>`) с подсказкой становится `aria-disabled` и подсказку показывает — она
   * объясняет, почему нельзя. Триггер меню или всплывающего окна оборачивают целиком:
   * `<Tooltip><Popover.Trigger asChild>`, а не кнопку внутри него.
   */
  children: ReactElement;
  /**
   * Подсказка — ещё и ИМЯ элемента: у кнопки без подписи (одна иконка) браузерный `title` давал его
   * сам. Без этого строковая подсказка ложится постоянным описанием (`aria-description`).
   */
  name?: boolean;
  side?: 'top' | 'right' | 'bottom' | 'left';
}

export function Tooltip({ content, children, name = false, side = 'top' }: TooltipProps) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLElement | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const own = children.props as ChildProps;
  const ref = useComposedRefs(anchor, own.ref);
  useEffect(() => () => clearTimeout(timer.current), []);

  const empty = content == null || content === '' || content === false;
  const text = typeof content === 'string' && content !== '' ? content : undefined;
  const aria = !text ? {} : name ? { 'aria-label': text } : own['aria-label'] === text ? {} : { 'aria-description': text };
  // Только своя кнопка: `disabled` у триггера меню (`DropdownMenu.Trigger disabled`) — его решение,
  // снятый он открыл бы меню, которое закрыто нарочно.
  const reason = !empty && !!own.disabled && typeof children.type === 'string';

  const show = (now: boolean) => {
    if (empty) return;
    clearTimeout(timer.current);
    if (now || justClosed()) setOpen(true);
    else timer.current = setTimeout(() => setOpen(true), DELAY);
  };
  const hide = () => {
    clearTimeout(timer.current);
    if (open) noteClosed();
    setOpen(false);
  };

  // Разметка одна и та же с подсказкой и без, открытой и закрытой: элемент всегда первый, слой — рядом.
  // Условная подсказка (`cond ? 'почему' : undefined`) иначе пересоздавала бы элемент — и он терял бы фокус.
  const Element = children.type as ElementType;
  const extra: ChildProps & Record<string, unknown> = {
    ref,
    ...aria,
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
    onPointerLeave: (e: PointerEvent<HTMLElement>) => { own.onPointerLeave?.(e); hide(); },
    onPointerDown: (e: PointerEvent<HTMLElement>) => { own.onPointerDown?.(e); hide(); },
    // Фокус открывает подсказку, только когда он виден — пришёл с клавиатуры. Фокус, который вернуло
    // окно (выход с шага к открывшей его кнопке), иначе открыл бы её сам — и первый Esc достался бы
    // подсказке, а не окну. Фокус, поставленный открывшимся слоем, — тоже не зов (см. `layerAutoFocus`).
    onFocus: (e: FocusEvent<HTMLElement>) => {
      own.onFocus?.(e);
      if (!layerAutoFocus && focusVisible(e.currentTarget)) show(true);
    },
    onBlur: (e: FocusEvent<HTMLElement>) => { own.onBlur?.(e); hide(); },
  };

  return (
    <>
      <Element {...own} {...extra} />
      {open && !empty && <Layer anchor={anchor} side={side} onClose={hide}>{content}</Layer>}
    </>
  );
}

/** Открытая подсказка: у элемента, поверх всего, верхний слой для Esc. */
function Layer({ anchor, side, onClose, children }: {
  anchor: RefObject<HTMLElement | null>;
  side: NonNullable<TooltipProps['side']>;
  onClose: () => void;
  children: ReactNode;
}) {
  // Прокрутка уводит элемент из-под подсказки — она закрывается, как у Radix Tooltip.
  useEffect(() => {
    const onScroll = (e: Event) => {
      if (e.target instanceof Node && anchor.current && e.target.contains(anchor.current)) onClose();
    };
    window.addEventListener('scroll', onScroll, { capture: true });
    return () => window.removeEventListener('scroll', onScroll, { capture: true });
  }, [anchor, onClose]);

  return (
    <Popper.Root>
      <Popper.Anchor virtualRef={anchor as RefObject<HTMLElement>} />
      <Portal>
        {/* Верхний слой: Esc достаётся подсказке, а не окну под ней. Щелчок и фокус мимо её не
            закрывают — это делают уход указателя и потеря фокуса элементом. */}
        <DismissableLayer asChild onEscapeKeyDown={onClose} onDismiss={onClose}
          onPointerDownOutside={e => e.preventDefault()} onFocusOutside={e => e.preventDefault()}>
          <Popper.Content role="tooltip" side={side} sideOffset={6} collisionPadding={8}
            className="z-[60] max-w-60 rounded bg-surface border border-stroke px-2 py-1 text-caption text-fg1 whitespace-pre-line pointer-events-none select-none"
            style={{ boxShadow: 'var(--md-sys-elevation-level2)' }}>
            {children}
          </Popper.Content>
        </DismissableLayer>
      </Portal>
    </Popper.Root>
  );
}
