import { createContext, useContext, useId, useLayoutEffect, useRef, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { Button, type ButtonProps, type ButtonVariant } from './Button';
import { ModalBarContext, useModalControl } from './modalControl';

/**
 * Кнопки окна с ролью (sharedui-md3-13, [UI-75]). Подвал окна и шага, а форма в теле окна — ряд
 * `ModalButtons`, собираются только из них и `ModalExit`: на столе они рисуют себя на месте и в объявленном
 * порядке, во весь экран — переносят себя в шапку (см. `ModalBar`). Произвольный элемент в шапку не
 * переносится, поэтому рядом с ними его нет вовсе — сторож `modalFooterRoles.test.ts`.
 *
 * <p>Ведущий край на столе — `lead`: пояснение и редкое действие («Отвязать») встают слева, выход и
 * главное — справа. Занятое окно (`busy`) гасит все кнопки с ролью, и в шапке тоже.</p>
 */

/**
 * Ряд кнопок на столе: в объявленном порядке у замыкающего края, а помеченные `data-lead` (пояснение,
 * редкое действие) — у ведущего. Последний из ведущих отодвигает остальное вправо.
 */
export const FOOTER_ROW = 'flex flex-wrap items-center justify-end gap-2 [&_[data-lead]:not(:has(~[data-lead]))]:mr-auto';

/** Ряд `ModalButtons`, в котором стоит кнопка: через него главное действие находит свою форму из шапки. */
const RowContext = createContext<RefObject<HTMLDivElement | null> | null>(null);

/**
 * Ряд кнопок с ролью в теле окна — у формы, которая рисует свои «Отмену» и «Создать» сама (содержимое,
 * которому подвал окна не достать). Во весь экран кнопки уходят в шапку, а опустевший ряд скрывается.
 */
export function ModalButtons({ className = '', children }: { className?: string; children: ReactNode }) {
  const row = useRef<HTMLDivElement>(null);
  return (
    <div ref={row} className={`${FOOTER_ROW} empty:hidden ${className}`}>
      <RowContext.Provider value={row}>{children}</RowContext.Provider>
    </div>
  );
}

/** Главное действие окна, не больше одного: filled в подвале, кнопка text справа в шапке во весь экран. */
export function ModalPrimary({ variant = 'filled', disabled, ...rest }: ButtonProps) {
  const bar = useContext(ModalBarContext);
  const row = useContext(RowContext);
  const { busy } = useModalControl();
  if (!bar) return <Button {...rest} variant={variant} disabled={disabled || busy} />;
  // Кнопка отправки формы из ряда в теле: в шапке она уже вне `<form>` — отправляет ту, где стоит ряд.
  const submits = rest.type === 'submit' && !rest.form && !!row;
  const onClick: ButtonProps['onClick'] = submits
    ? e => { rest.onClick?.(e); row?.current?.closest('form')?.requestSubmit(); }
    : rest.onClick;
  // Знак в шапке не ставится: там действие — подпись (MD3 full-screen dialog).
  return bar.primary && createPortal(
    <Button {...rest} type={submits ? 'button' : rest.type} onClick={onClick} icon={undefined} variant="text"
      disabled={disabled || busy} />,
    bar.primary);
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
  /** Подсказка кнопки; у погашенной во весь экран — строкой под пунктом «⋮», почему нельзя. */
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
  const hint = disabled ? title : undefined;
  const setAction = bar?.setAction;
  const layer = bar?.layer;
  useLayoutEffect(() => {
    if (setAction && layer) setAction(id, {
      layer, label: children, danger, disabled: off, loading, icon, hint, run: () => run.current(),
    });
    // Знак — разметка, новая на каждой отрисовке: пункт перерисуется с ним при любой другой перемене.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setAction, layer, id, children, danger, off, loading, hint]);
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

/** Пояснение подвала: у ведущего края на столе, строкой под шапкой во весь экран. */
export function ModalNote({ children }: { children: ReactNode }) {
  const bar = useContext(ModalBarContext);
  // На столе пояснение стоит рядом с кнопками и мельче их подписи; под шапкой — строка текста окна.
  if (!bar) return <span data-lead="" className="text-body-small text-on-surface-variant">{children}</span>;
  return bar.note && createPortal(<p className="text-body-medium text-on-surface-variant">{children}</p>, bar.note);
}
