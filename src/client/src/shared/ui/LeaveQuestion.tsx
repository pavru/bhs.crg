import * as Dialog from '@radix-ui/react-dialog';
import { Button } from './Button';
import { COMPACT_DIALOG_PLACE, DIALOG_SCRIM, DIALOG_SURFACE, DIALOG_TITLE } from './dialogSurface';

interface LeaveQuestionProps {
  open: boolean;
  /** «Продолжить редактирование»: Esc, щелчок по затемнению вопроса и сама кнопка. */
  onStay: () => void;
  /** «Закрыть» — уйти, потеряв набранное. */
  onLeave: () => void;
}

/**
 * «Закрыть без сохранения?» — отдельное окно над окном формы ([UI-75]), а не блок внутри него: своё
 * затемнение и своё возвышение отличают его от формы в обеих темах. Окно Radix вложенное — ловушку фокуса,
 * Esc и щелчок мимо даёт оно: всё это значит «остаться».
 *
 * <p>Своего слоя истории у вопроса нет: «Назад» при вопросе решает окно под ним.</p>
 */
export function LeaveQuestion({ open, onStay, onLeave }: LeaveQuestionProps) {
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o) onStay(); }}>
      <Dialog.Portal>
        {/* Выше окна: вопрос открывается, когда окно уже лежит над страницей. */}
        <Dialog.Overlay className={`${DIALOG_SCRIM} z-50`} />
        <Dialog.Content
          role="alertdialog"
          // Вопрос держит человека, как и окно под ним, — и он же верхний: кольцо выхода ложится на его
          // «Продолжить редактирование», а не на кнопку под затемнением (см. `refusedByDialog`).
          data-modal-state="dirty"
          className={`${COMPACT_DIALOG_PLACE} z-50 flex flex-col p-6 ${DIALOG_SURFACE} focus:outline-none`}
          // Фокус вернёт окно — туда, откуда спросили. Сам Radix вернул бы его на кнопку вопроса, которой
          // уже нет.
          onCloseAutoFocus={e => e.preventDefault()}
        >
          <Dialog.Title className={`${DIALOG_TITLE} mb-4`}>Закрыть без сохранения?</Dialog.Title>
          <Dialog.Description className="text-body-medium text-on-surface-variant">
            Несохранённые изменения будут потеряны.
          </Dialog.Description>
          <div className="flex flex-wrap justify-end gap-2 pt-6">
            {/* Фокус — на безопасном ответе: Enter, которым человек пришёл сюда с «Отмены», повторным
                нажатием ничего не теряет. До «Закрыть» — один Tab. */}
            <Button variant="text" autoFocus data-modal-exit="" onClick={onStay}>Продолжить редактирование</Button>
            <Button variant="filled" danger onClick={onLeave}>Закрыть</Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
