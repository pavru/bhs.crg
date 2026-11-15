import * as P from '@radix-ui/react-popover';
import { useComposedRefs } from '@radix-ui/react-compose-refs';
import {
  createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState,
  type ReactNode, type Ref, type RefObject,
} from 'react';
import { isPhone, useHistoryLayer } from './historyLayers';

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
  /**
   * Узлы кнопки и якоря — состоянием, а не ref: сменился узел при открытом слое (чип сменил тип, поле даты
   * — ветку), и наблюдатель видимости переезжает на новый, а не смотрит на снятый.
   */
  trigger: HTMLElement | null;
  anchor: HTMLElement | null;
  setTrigger: (el: HTMLElement | null) => void;
  setAnchor: (el: HTMLElement | null) => void;
  /** Слой закрыт уходом якоря — сюда фокус вернётся без прокрутки, вместо возврата Radix. */
  detachedRef: RefObject<HTMLElement | null>;
}

const PopoverContext = createContext<PopoverState | null>(null);

function usePopover(part: string) {
  const state = useContext(PopoverContext);
  if (!state) throw new Error(`Popover.${part} — только внутри Popover.Root`);
  return state;
}

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
  // `setOpen` один на всю жизнь поповера, последние пропсы — в ref: иначе новое значение контекста на каждой
  // отрисовке экрана перерисовывало бы все части каждого поповера, и при закрытом слое тоже.
  const latestRef = useRef({ controlled: openProp !== undefined, onOpenChange });
  useLayoutEffect(() => { latestRef.current = { controlled: openProp !== undefined, onOpenChange }; });
  const setOpen = useCallback((next: boolean) => {
    if (!latestRef.current.controlled) setOwn(next);
    latestRef.current.onOpenChange?.(next);
  }, []);
  useHistoryLayer(open, () => {
    if (onBack && !onBack()) return false;
    setOpen(false);
    return true;
  });
  const [trigger, setTrigger] = useState<HTMLElement | null>(null);
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const detachedRef = useRef<HTMLElement | null>(null);
  const state = useMemo(
    () => ({ open, setOpen, trigger, anchor, setTrigger, setAnchor, detachedRef }),
    [open, setOpen, trigger, anchor],
  );
  return (
    <PopoverContext.Provider value={state}>
      <P.Root open={open} onOpenChange={setOpen} modal={modal}>{children}</P.Root>
    </PopoverContext.Provider>
  );
}

/** Кнопка поповера. Её и смотрят, ушла ли она из видимой части (`Content`, `hideWhenDetached`). */
export function Trigger({ ref, ...props }: P.PopoverTriggerProps & { ref?: Ref<HTMLButtonElement> }) {
  const { setTrigger } = usePopover('Trigger');
  // Сеттер состояния стабилен — составной ref не меняется от отрисовки к отрисовке и не переподключается.
  return <P.Trigger {...props} ref={useComposedRefs(ref, setTrigger)} />;
}

/**
 * Якорь места. Видимость смотрят у него, только когда `Trigger` нет: порядок ref-колбэков (дочерний
 * раньше родительского) выбор не решает — `Trigger` внутри якоря у поля даты смотрится он.
 */
export function Anchor({ ref, ...props }: P.PopoverAnchorProps & { ref?: Ref<HTMLDivElement> }) {
  const { setAnchor } = usePopover('Anchor');
  return <P.Anchor {...props} ref={useComposedRefs(ref, setAnchor)} />;
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
 * бы его внутрь слоя, и с уходом слоя он упал бы на body. Экран, сам решивший, куда фокус (`preventDefault`
 * в своём `onCloseAutoFocus`: Tab к следующему полю, «Очистить»), решает и здесь — обёртка его не отменяет.</p>
 */
export function Content({ closeWhenDetached = false, hideWhenDetached = false, onCloseAutoFocus, ...props }: ContentProps) {
  const { open, setOpen, trigger, anchor, detachedRef } = usePopover('Content');
  const close = closeWhenDetached || hideWhenDetached;
  const watched = trigger ?? anchor;
  useEffect(() => {
    if (!open || !close || !watched || typeof IntersectionObserver !== 'function') return;
    if (!closeWhenDetached && !isPhone()) return;
    detachedRef.current = null;
    let live = true;
    const seen = new IntersectionObserver(([e]) => {
      // Снятый узел «не виден» не потому, что его прокрутили: на смену ему смотрит уже новый наблюдатель.
      if (!live || !e || e.isIntersecting || !watched.isConnected) return;
      detachedRef.current = watched;
      setOpen(false);
    });
    seen.observe(watched);
    return () => { live = false; seen.disconnect(); };
  }, [open, close, closeWhenDetached, watched, setOpen, detachedRef]);
  return (
    <P.Content {...props} hideWhenDetached={close}
      onCloseAutoFocus={e => {
        onCloseAutoFocus?.(e);
        const back = detachedRef.current;
        detachedRef.current = null;
        if (!back || e.defaultPrevented) return;
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
