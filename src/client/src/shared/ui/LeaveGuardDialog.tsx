import { Modal, ModalAction, ModalExit, ModalPrimary } from './Modal';

/**
 * MD3-диалог-гард при уходе с несохранёнными правками: с выбранного элемента (`useDirtyGuard`) либо
 * со страницы по маршруту (`useLeaveGuard`).
 *
 * Общий компонент, а не часть редактора типов (где он появился, issue #197 / #210): тот же вопрос
 * задаёт форма счёта (G4, issue #1097), и разделу счетов незачем зависеть от редактора типов.
 */
export function LeaveGuardDialog({ open, saving, saveBlocked, onSave, onDiscard, onCancel }: {
  open: boolean; saving: boolean;
  /** Сохранить сейчас нельзя — почему (картинка ещё едет в хранилище, issue #532). Уйти без
   *  сохранения и остаться можно: недоступна только кнопка сохранения, с причиной в подсказке. */
  saveBlocked?: string;
  onSave: () => void; onDiscard: () => void; onCancel: () => void;
}) {
  return (
    // Вопрос ведёт себя как `ConfirmDialog`: промах и Esc равны «Отмене» — безопасному ответу.
    // Идущее сохранение сильнее: `busy` гасит и их, и кнопку (issue #1236).
    <Modal open={open} onOpenChange={o => { if (!o) onCancel(); }} title="Несохранённые изменения"
      nothingToLose busy={saving}
      footer={<>
        <ModalExit />
        <ModalAction variant="tonal" onClick={onDiscard}>Не сохранять</ModalAction>
        <ModalPrimary onClick={onSave} loading={saving} disabled={!!saveBlocked} title={saveBlocked}>
          Сохранить и перейти
        </ModalPrimary>
      </>
      }>
      <p className="text-body-medium text-on-surface-variant">
        Есть несохранённые изменения. Сохранить их перед переходом к другому элементу?
      </p>
    </Modal>
  );
}
