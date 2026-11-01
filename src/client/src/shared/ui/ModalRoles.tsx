import { useContext, useId, useLayoutEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Button, type ButtonProps, type ButtonVariant } from './Button';
import { ModalBarContext, useModalControl } from './modalControl';

/**
 * Кнопки подвала окна с ролью (sharedui-md3-13, [UI-75]). Подвал окна и шага собирается только из них и
 * `ModalExit`: на столе они рисуют подвал на месте и в объявленном порядке, во весь экран — переносят себя
 * в шапку (см. `ModalBar`). Произвольный элемент в шапку не переносится, поэтому в подвале его нет вовсе —
 * сторож `modalFooterRoles.test.ts`.
 *
 * <p>Ведущий край на столе — `lead`: пояснение и редкое действие («Отвязать») встают слева, выход и
 * главное — справа. Занятое окно (`busy`) гасит все кнопки с ролью, и в шапке тоже.</p>
 */

/** Главное действие окна, не больше одного: filled в подвале, кнопка text справа в шапке во весь экран. */
export function ModalPrimary({ variant = 'filled', disabled, ...rest }: ButtonProps) {
  const bar = useContext(ModalBarContext);
  const { busy } = useModalControl();
  if (!bar) return <Button {...rest} variant={variant} disabled={disabled || busy} />;
  // Знак в шапке не ставится: там действие — подпись (MD3 full-screen dialog).
  return bar.primary && createPortal(
    <Button {...rest} icon={undefined} variant="text" disabled={disabled || busy} />, bar.primary);
}

interface ModalActionProps {
  /** Подпись — строкой: во весь экран она же пункт меню «⋮». */
  children: string;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
  loading?: boolean;
  icon?: ReactNode;
  variant?: Exclude<ButtonVariant, 'filled'>;
  title?: string;
  /** На столе — у ведущего края подвала. */
  lead?: boolean;
}

/** Прочее действие окна: кнопка подвала на столе, пункт «⋮» во весь экран (тона ошибки при `danger`). */
export function ModalAction({ children, onClick, danger = false, disabled = false, loading = false, icon,
  variant = 'text', title, lead }: ModalActionProps) {
  const bar = useContext(ModalBarContext);
  const { busy } = useModalControl();
  const id = useId();
  const run = useRef(onClick);
  useLayoutEffect(() => { run.current = onClick; });
  const off = disabled || loading || busy;
  const setAction = bar?.setAction;
  const layer = bar?.layer;
  useLayoutEffect(() => {
    if (setAction && layer) setAction(id, { layer, label: children, danger, disabled: off, run: () => run.current() });
  }, [setAction, layer, id, children, danger, off]);
  // Снимается только с уходом кнопки: снятие на каждой правке переставило бы пункт в конец меню.
  useLayoutEffect(() => () => setAction?.(id, null), [setAction, id]);
  if (bar) return null;
  return (
    <Button variant={variant} danger={danger} disabled={disabled || busy} loading={loading} icon={icon} title={title}
      data-lead={lead ? '' : undefined} onClick={onClick}>
      {children}
    </Button>
  );
}

const NOTE = 'text-body-medium text-on-surface-variant';

/** Пояснение подвала: у ведущего края на столе, строкой под шапкой во весь экран. */
export function ModalNote({ children }: { children: ReactNode }) {
  const bar = useContext(ModalBarContext);
  if (!bar) return <span data-lead="" className={NOTE}>{children}</span>;
  return bar.note && createPortal(<p className={NOTE}>{children}</p>, bar.note);
}
