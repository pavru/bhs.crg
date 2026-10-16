import { useContext, useId, useLayoutEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Modal } from './Modal';
import { WindowStepContext, type WindowStepHost } from './modalControl';

interface WindowStepProps {
  title: string;
  /** Подвал шага. «Отмена» — `ModalExit`: внутри окна она возвращает к форме, а не закрывает окно. */
  footer?: ReactNode;
  children: ReactNode;
  /** Уйти с шага — к форме окна (или закрыть окно, если шаг сам стал окном). */
  onBack: () => void;
  busy?: boolean;
  /** На шаге набрано то, что выход назад потеряет, — спросить перед ним. */
  isDirty?: boolean;
  /** Размер, если шаг стал окном сам (снаружи любого окна). Внутри окна размер — его. */
  wide?: boolean;
}

/**
 * Шаг окна вместо окна поверх окна (issue #1384, docs/UI.md «Окна»). Внутри окна шаг подменяет в нём
 * заголовок, тело и подвал; форма окна остаётся смонтированной под шагом, и выход с шага возвращает к
 * ней с набранным. Снаружи любого окна шаг — обычное окно: компоненту, который его рисует, незачем
 * знать, где он стоит.
 *
 * <p>Компонент показывается, пока смонтирован: открыть — отрисовать, уйти — перестать рисовать в
 * `onBack`.</p>
 */
export function WindowStep(props: WindowStepProps) {
  const host = useContext(WindowStepContext);
  if (!host) {
    const { title, footer, children, onBack, busy, isDirty, wide } = props;
    return (
      <Modal open onOpenChange={o => { if (!o) onBack(); }} title={title} wide={wide}
        footer={footer} busy={busy} isDirty={isDirty}>
        {children}
      </Modal>
    );
  }
  return <HostedStep host={host} {...props} />;
}

function HostedStep({ host, title, footer, children, onBack, busy = false, isDirty = false }: WindowStepProps & {
  host: WindowStepHost;
}) {
  const id = useId();
  const back = useRef(onBack);
  useLayoutEffect(() => { back.current = onBack; });
  const { push, update, pop } = host;
  useLayoutEffect(() => {
    push(id, () => back.current());
    return () => pop(id);
  }, [push, pop, id]);
  useLayoutEffect(() => { update(id, { title, busy, isDirty }); }, [update, id, title, busy, isDirty]);

  const slot = host.slots[id];
  return (
    <>
      {slot?.body && createPortal(children, slot.body)}
      {slot?.footer && footer && createPortal(footer, slot.footer)}
    </>
  );
}
