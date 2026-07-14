import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { useState, useEffect, type ReactNode } from 'react';
import { Button } from './Button';

interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  children: ReactNode;
  wide?: boolean;
  extraWide?: boolean;
  isDirty?: boolean;
  /** Не добавлять собственный скролл и паддинг тела — содержимое само управляет
   *  раскладкой (фиксированный футер, прокрутка только области полей). */
  flushBody?: boolean;
  /** Зафиксированная нижняя область (кнопки действий). Заголовок сверху и футер снизу
   *  не скроллятся; прокручивается только тело между ними. */
  footer?: ReactNode;
}

export function Modal({ open, onOpenChange, title, children, wide, extraWide, isDirty, flushBody, footer }: ModalProps) {
  const [confirmClose, setConfirmClose] = useState(false);

  useEffect(() => {
    if (!open) setConfirmClose(false);
  }, [open]);

  function attemptClose() {
    if (isDirty) setConfirmClose(true);
    else onOpenChange(false);
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-black/40"
          style={{ backdropFilter: 'blur(2px)' }}
        />
        <Dialog.Content
          className={`fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 rounded-[28px] max-h-[90vh] flex flex-col overflow-hidden focus:outline-none bg-surface border border-stroke ${
            extraWide ? 'w-full max-w-5xl' : wide ? 'w-full max-w-2xl' : 'w-full max-w-lg'
          }`}
          style={{ boxShadow: 'var(--f-shadow28)' }}
          onEscapeKeyDown={e => {
            if (isDirty) {
              e.preventDefault();
              setConfirmClose(true);
            }
          }}
          onPointerDownOutside={e => {
            if (isDirty) {
              e.preventDefault();
              setConfirmClose(true);
            }
          }}
        >
          <div className="flex items-center justify-between shrink-0 px-6 pt-6 pb-5">
            <Dialog.Title className="text-base font-semibold text-fg1">
              {title}
            </Dialog.Title>
            <button
              type="button"
              onClick={attemptClose}
              aria-label="Закрыть"
              className="flex items-center justify-center w-9 h-9 rounded-full transition-colors text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand"
            >
              <X size={18} />
            </button>
          </div>
          {flushBody ? (
            <div className="flex-1 min-h-0 flex flex-col">
              {children}
            </div>
          ) : (
            <div className={`overflow-y-auto flex-1 px-6 ${footer ? 'pb-4' : 'pb-6'}`}>
              {children}
            </div>
          )}

          {footer && (
            <div className="shrink-0 px-6 py-3 border-t border-stroke bg-surface">
              {footer}
            </div>
          )}

          {confirmClose && (
            <div className="absolute inset-0 z-10 flex items-center justify-center rounded-[28px] bg-black/40">
              <div className="rounded-3xl p-5 w-80 bg-surface border border-stroke" style={{ boxShadow: 'var(--f-shadow28)' }}>
                <p className="text-sm font-semibold mb-1 text-fg1">
                  Закрыть без сохранения?
                </p>
                <p className="text-xs mb-4 text-fg3">
                  Несохранённые изменения будут потеряны.
                </p>
                <div className="flex gap-2 justify-end">
                  <Button variant="text" size="sm" onClick={() => setConfirmClose(false)}>
                    Продолжить редактирование
                  </Button>
                  <Button variant="filled" danger size="sm"
                    onClick={() => { setConfirmClose(false); onOpenChange(false); }}>
                    Закрыть
                  </Button>
                </div>
              </div>
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
