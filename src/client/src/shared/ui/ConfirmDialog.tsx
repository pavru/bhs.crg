import * as Dialog from '@radix-ui/react-dialog';
import { useEffect, useState, type ReactNode } from 'react';
import { Button } from './Button';

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
  onConfirm: () => void;
}

/**
 * Стилизованное подтверждение удаления — замена голому window.confirm(). Визуальный паттерн
 * вынесен из confirmClose-оверлея Modal.tsx (не сам isDirty-guard, он про другой сценарий —
 * закрытие несохранённой формы). См. память проекта feedback_delete_ui_safety —
 * найдена системная проблема (12 мест с window.confirm(), включая каскадное удаление
 * стройки без предупреждения о масштабе).
 */
export function ConfirmDialog({
  open, onOpenChange, title, description, confirmLabel, cancelLabel = 'Отмена', requireCheckbox, onConfirm,
}: ConfirmDialogProps) {
  const [checked, setChecked] = useState(false);

  useEffect(() => {
    if (!open) setChecked(false);
  }, [open]);

  const canConfirm = !requireCheckbox || checked;

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
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
            {title}
          </Dialog.Title>

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

          {/* items-start + shrink-0 у «Отмена» и multiline+min-w-0 у подтверждения: длинная подпись
              (в неё вшито имя объекта) переносится внутри кнопки, а не распирает узкий диалог. */}
          <div className={`flex gap-2 justify-end items-start ${!requireCheckbox ? 'mt-4' : ''}`}>
            <Dialog.Close asChild>
              <Button variant="text" size="sm" className="shrink-0">{cancelLabel}</Button>
            </Dialog.Close>
            <Button
              variant="filled" danger size="sm" multiline className="min-w-0"
              disabled={!canConfirm}
              onClick={() => { onConfirm(); onOpenChange(false); }}
            >
              {confirmLabel}
            </Button>
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
