import * as Dialog from '@radix-ui/react-dialog';
import { useEffect, useState, type ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button } from './Button';
import { apiError } from '@/shared/utils/apiError';

interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Список последствий (например маркированный список каскада) — необязательный. */
  description?: ReactNode;
  /** Текст кнопки подтверждения — должен называть объект, не быть голым "Удалить". */
  confirmLabel: string;
  cancelLabel?: string;
  /** Текст чекбокса-барьера ("Понимаю, что это необратимо") — если задан, кнопка
   *  подтверждения неактивна, пока чекбокс не отмечен. Использовать для операций с
   *  многоуровневым каскадом или системными последствиями (стройка/раздел/тип документа). */
  requireCheckbox?: string;
  /** Заголовок в состоянии отказа (после ошибки сервера, напр. 409-guard). */
  errorTitle?: string;
  /**
   * Проактивная блокировка (issue #275): если задано — диалог сразу открывается в состоянии
   * «нельзя» (тот же вид, что и реактивная ошибка): заголовок `errorTitle`, этот контент вместо
   * подтверждения, кнопка только «Понятно». Реактивный 409 остаётся страховкой от гонок.
   */
  blocked?: ReactNode;
  /**
   * Действие подтверждения. Может быть async: если промис отклоняется (напр. 409 «нельзя удалить —
   * используется …»), диалог НЕ закрывается, а показывает причину (apiError) в теле — пользователь
   * видит, почему кнопка «не сработала». Успех (или синхронный void без throw) — закрывает диалог.
   * Возвращаемое значение промиса игнорируется (mutateAsync-ответ и т.п.) — важен лишь resolve/reject.
   */
  onConfirm: () => void | Promise<unknown>;
}

/**
 * Стилизованное подтверждение удаления — замена голому window.confirm(). Визуальный паттерн
 * вынесен из confirmClose-оверлея Modal.tsx (не сам isDirty-guard, он про другой сценарий —
 * закрытие несохранённой формы). См. память проекта feedback_delete_ui_safety.
 *
 * Показ причины отказа (issue #273): при reject `onConfirm` диалог остаётся открыт, меняет
 * заголовок на `errorTitle` и показывает блок причины (сообщение backend как есть) вместо
 * кнопки подтверждения — единый канал для guard-отказов удаления (409), вместо alert()/тишины.
 */
export function ConfirmDialog({
  open, onOpenChange, title, description, confirmLabel, cancelLabel = 'Отмена',
  requireCheckbox, errorTitle = 'Удаление невозможно', blocked, onConfirm,
}: ConfirmDialogProps) {
  const [checked, setChecked] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) { setChecked(false); setBusy(false); setError(null); }
  }, [open]);

  const canConfirm = !requireCheckbox || checked;
  // Состояние «нельзя»: проактивная блокировка (blocked) ИЛИ реактивная ошибка после попытки (error).
  const blockedView = error != null || blocked != null;

  async function handleConfirm() {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
      onOpenChange(false);
    } catch (e) {
      // Отказ (обычно 409-guard): не закрываемся, показываем причину.
      setError(apiError(e, 'Не удалось выполнить действие.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!busy) onOpenChange(o); }}>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-black/40"
          style={{ backdropFilter: 'blur(2px)' }}
        />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 rounded-[28px] p-6 w-full max-w-sm bg-surface border border-stroke focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}
        >
          <Dialog.Title className="text-sm font-semibold mb-2 text-fg1">
            {blockedView ? errorTitle : title}
          </Dialog.Title>

          {blockedView ? (
            <div className="mt-2 flex items-start gap-2.5 rounded-md bg-danger-subtle px-3 py-2.5 text-xs text-fg1">
              <AlertTriangle size={16} className="shrink-0 mt-0.5 text-danger" />
              <div className="max-h-40 overflow-y-auto whitespace-pre-line min-w-0">{error ?? blocked}</div>
            </div>
          ) : (
            <>
              {description && (
                <div className="text-xs mb-3 text-fg3 space-y-1">
                  {description}
                </div>
              )}

              {requireCheckbox && (
                <label className="flex items-start gap-2 mb-4 text-xs text-fg2 cursor-pointer select-none">
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={e => setChecked(e.target.checked)}
                    className="mt-0.5"
                  />
                  {requireCheckbox}
                </label>
              )}
            </>
          )}

          <div className={`flex gap-2 justify-end items-start ${blockedView || !requireCheckbox ? 'mt-4' : ''}`}>
            {blockedView ? (
              <Button variant="tonal" size="sm" onClick={() => onOpenChange(false)}>Понятно</Button>
            ) : (
              <>
                <Dialog.Close asChild>
                  <Button variant="text" size="sm" className="shrink-0" disabled={busy}>{cancelLabel}</Button>
                </Dialog.Close>
                <Button
                  variant="filled" danger size="sm" multiline className="min-w-0"
                  disabled={!canConfirm} loading={busy}
                  onClick={handleConfirm}
                >
                  {confirmLabel}
                </Button>
              </>
            )}
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/** Маркированный список каскада — общий рендер для description. */
export function CascadeList({ items }: { items: string[] }) {
  return (
    <ul className="list-disc pl-4 space-y-0.5">
      {items.map((item, i) => <li key={i}>{item}</li>)}
    </ul>
  );
}
