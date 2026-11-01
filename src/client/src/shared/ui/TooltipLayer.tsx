import * as Popper from '@radix-ui/react-popper';
import { Portal } from '@radix-ui/react-portal';
import { DismissableLayer } from '@radix-ui/react-dismissable-layer';
import { FocusScope } from '@radix-ui/react-focus-scope';
import { useEffect, type HTMLAttributes, type ReactNode, type Ref, type RefObject } from 'react';

export type TooltipSide = 'top' | 'right' | 'bottom' | 'left';

/**
 * Открытая подсказка — общий слой `Tooltip` и `RichTooltip` ([UI-72]): у элемента (Popper), в `body`
 * (портал — правило `portal-container`), верхним слоем для Esc. Прокрутка, уводящая элемент из-под
 * подсказки, её закрывает, как у Radix Tooltip.
 *
 * <p>Щелчок и фокус мимо слой не закрывают — это делают уход указателя и потеря фокуса элементом. Слой
 * с действиями (`interactive`) ещё и держит своё `FocusScope`: в окне фокус, ушедший в подсказку в
 * `body`, иначе вернула бы ловушка фокуса окна — Tab до действия не доходил бы. Своего автофокуса у
 * области нет: фокус в подсказку ведёт человек.</p>
 */
export function TooltipLayer({ anchor, side, onClose, onEscape, interactive = false, layerRef, className, children, ...rest }: {
  anchor: RefObject<HTMLElement | null>;
  side: TooltipSide;
  onClose: () => void;
  /** Esc; без своего — `onClose`. */
  onEscape?: () => void;
  interactive?: boolean;
  layerRef?: Ref<HTMLDivElement>;
  className: string;
  children: ReactNode;
} & Omit<HTMLAttributes<HTMLDivElement>, 'children' | 'className'>) {
  useEffect(() => {
    const onScroll = (e: Event) => {
      if (e.target instanceof Node && anchor.current && e.target.contains(anchor.current)) onClose();
    };
    window.addEventListener('scroll', onScroll, { capture: true });
    return () => window.removeEventListener('scroll', onScroll, { capture: true });
  }, [anchor, onClose]);

  const content = (
    <Popper.Content ref={layerRef} side={side} sideOffset={6} collisionPadding={8}
      className={`z-[60] ${interactive ? '' : 'pointer-events-none select-none '}${className}`} {...rest}>
      {children}
    </Popper.Content>
  );
  return (
    <Popper.Root>
      <Popper.Anchor virtualRef={anchor as RefObject<HTMLElement>} />
      <Portal>
        <DismissableLayer asChild onEscapeKeyDown={onEscape ?? onClose} onDismiss={onClose}
          onPointerDownOutside={e => e.preventDefault()} onFocusOutside={e => e.preventDefault()}>
          {interactive
            ? (
              <FocusScope asChild onMountAutoFocus={e => e.preventDefault()} onUnmountAutoFocus={e => e.preventDefault()}>
                {content}
              </FocusScope>
            )
            : content}
        </DismissableLayer>
      </Portal>
    </Popper.Root>
  );
}
