import * as P from '@radix-ui/react-popover';
import { useComposedRefs } from '@radix-ui/react-compose-refs';
import { createContext, useContext, useEffect, useRef, useState, type ReactNode, type Ref, type RefObject } from 'react';
import { WIDE } from '@/shared/hooks/useMedia';
import { useHistoryLayer } from './historyLayers';

/**
 * Поповер ([UI-96]): один на всё приложение — условие отбора, колонки, раскладка, пояснения, календарь
 * поля даты, список «+ Вставить поле». Части те же, что у Radix, и экран меняет только импорт:
 * `import * as Popover from '@/shared/ui/Popover'`.
 *
 * <p>Своё здесь одно — поповер слой истории (правило #1418, docs/UI.md «Назад на телефоне»): на телефоне
 * системная «Назад» закрывает его, а окно или страница под ним остаются. Закрытый своими средствами
 * (выбор, Esc, щелчок мимо, `Close`), он снимает запись сам; на рабочем столе записей нет. Слой — у
 * каждого поповера, управляемого и нет, а не у тех, кто вспомнил о нём; своего `useHistoryLayer` у
 * поповера на обёртке нет — запись была бы двойной, и первая «Назад» «ничего бы не делала».</p>
 *
 * <p>Перечень частей назван поимённо, без `export *`: звёздный реэкспорт выпустил бы наружу псевдонимы
 * Radix (`Popover`, `PopoverContent`…) мимо слоя. Пакет `@radix-ui/react-popover` берёт только этот файл и
 * список подсказки — сторож `windowsAreHistoryLayers.test.ts`.</p>
 */

interface PopoverState {
  open: boolean;
  setOpen: (open: boolean) => void;
  triggerRef: RefObject<HTMLElement | null>;
  anchorRef: RefObject<HTMLElement | null>;
  /** Слой закрыт уходом якоря — сюда фокус вернётся без прокрутки, вместо возврата Radix. */
  detachedRef: RefObject<HTMLElement | null>;
}

const PopoverContext = createContext<PopoverState | null>(null);

function usePopover(part: string) {
  const state = useContext(PopoverContext);
  if (!state) throw new Error(`Popover.${part} — только внутри Popover.Root`);
  return state;
}

const phone = () => typeof window.matchMedia === 'function' && !window.matchMedia(WIDE).matches;

export interface RootProps {
  open?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  modal?: boolean;
  /**
   * Свой шаг назад слоя (список месяцев календаря): `false` — слой остаётся открытым, `true` — закрыть.
   * Не задан — «Назад» закрывает поповер, как Esc.
   */
  onBack?: () => boolean;
  children?: ReactNode;
}

/** Поповер — управляемый (`open` / `onOpenChange`) или нет (`defaultOpen`); слой истории в обоих случаях. */
export function Root({ open: openProp, defaultOpen = false, onOpenChange, modal, onBack, children }: RootProps) {
  const [own, setOwn] = useState(defaultOpen);
  const open = openProp ?? own;
  const setOpen = (next: boolean) => {
    if (openProp === undefined) setOwn(next);
    onOpenChange?.(next);
  };
  useHistoryLayer(open, () => {
    if (onBack && !onBack()) return false;
    setOpen(false);
    return true;
  });
  const triggerRef = useRef<HTMLElement | null>(null);
  const anchorRef = useRef<HTMLElement | null>(null);
  const detachedRef = useRef<HTMLElement | null>(null);
  return (
    <PopoverContext.Provider value={{ open, setOpen, triggerRef, anchorRef, detachedRef }}>
      <P.Root open={open} onOpenChange={setOpen} modal={modal}>{children}</P.Root>
    </PopoverContext.Provider>
  );
}

/** Кнопка поповера. Её и смотрят, ушла ли она из видимой части (`Content`, `hideWhenDetached`). */
export function Trigger({ ref, ...props }: P.PopoverTriggerProps & { ref?: Ref<HTMLButtonElement> }) {
  const { triggerRef } = usePopover('Trigger');
  return <P.Trigger {...props} ref={useComposedRefs(ref, (el: HTMLButtonElement | null) => { triggerRef.current = el; })} />;
}

/**
 * Якорь места. Видимость смотрят у него, только когда `Trigger` нет: порядок ref-колбэков (дочерний
 * раньше родительского) выбор не решает — `Trigger` внутри якоря у поля даты смотрится он.
 */
export function Anchor({ ref, ...props }: P.PopoverAnchorProps & { ref?: Ref<HTMLDivElement> }) {
  const { anchorRef } = usePopover('Anchor');
  return <P.Anchor {...props} ref={useComposedRefs(ref, (el: HTMLDivElement | null) => { anchorRef.current = el; })} />;
}

export interface ContentProps extends P.PopoverContentProps {
  ref?: Ref<HTMLDivElement>;
  /**
   * Якорь ушёл из видимой части — закрыть на любой ширине, а не спрятать. Слою, где фокус стоит в поле
   * ввода («+ Вставить поле»): спрятанный, он оставил бы фокус и набор в невидимом фильтре.
   */
  closeWhenDetached?: boolean;
}

/**
 * Слой поповера. `hideWhenDetached`: якорь ушёл из видимой части — на рабочем столе слой прячется, как у
 * Radix, на телефоне закрывается: спрятанный, он держал бы запись истории, и первая «Назад» ушла бы на
 * невидимый слой. Касание, начавшее прокрутку, щелчком не стало — Radix слой не закрыл.
 *
 * <p>Закрыв слой уходом якоря, обёртка гасит возврат фокуса Radix — после обработчика экрана — и ставит
 * фокус на якорь `focus({ preventScroll: true })`: обычный `focus()` Radix прокрутил бы тело окна обратно и
 * отменил прокрутку человека. Ставит при закрытии, а не раньше: ловушка фокуса модального поповера вернула
 * бы его внутрь слоя, и с уходом слоя он упал бы на body.</p>
 */
export function Content({ closeWhenDetached = false, hideWhenDetached = false, onCloseAutoFocus, ...props }: ContentProps) {
  const { open, setOpen, triggerRef, anchorRef, detachedRef } = usePopover('Content');
  const close = closeWhenDetached || hideWhenDetached;
  // Последний `setOpen` — в ref: наблюдатель живёт, пока слой открыт, и не пересоздаётся на каждой отрисовке.
  const shut = useRef(setOpen);
  useEffect(() => { shut.current = setOpen; });
  useEffect(() => {
    const watched = triggerRef.current ?? anchorRef.current;
    if (!open || !close || !watched || typeof IntersectionObserver !== 'function') return;
    if (!closeWhenDetached && !phone()) return;
    detachedRef.current = null;
    const seen = new IntersectionObserver(([e]) => {
      if (!e || e.isIntersecting) return;
      detachedRef.current = watched;
      shut.current(false);
    });
    seen.observe(watched);
    return () => seen.disconnect();
  }, [open, close, closeWhenDetached, triggerRef, anchorRef, detachedRef]);
  return (
    <P.Content {...props} hideWhenDetached={close}
      onCloseAutoFocus={e => {
        onCloseAutoFocus?.(e);
        const back = detachedRef.current;
        if (!back) return;
        detachedRef.current = null;
        e.preventDefault();
        back.focus({ preventScroll: true });
      }} />
  );
}

/** Портал в body — слой над окном, а не часть его (docs/UI.md, «Слои над окном»). */
export function Portal(props: P.PopoverPortalProps) {
  return <P.Portal {...props} />;
}

/** Кнопка, закрывающая поповер: закрытый ею, он снимает свою запись истории сам. */
export function Close(props: P.PopoverCloseProps & { ref?: Ref<HTMLButtonElement> }) {
  return <P.Close {...props} />;
}

export function Arrow(props: P.PopoverArrowProps & { ref?: Ref<SVGSVGElement> }) {
  return <P.Arrow {...props} />;
}
