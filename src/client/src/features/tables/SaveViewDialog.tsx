import { useState, type FormEvent } from 'react';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { Checkbox } from '@/shared/ui/Checkbox';
import { apiError } from '@/shared/utils/apiError';

/**
 * Название представления и признак «общее» (задача G3a, issue #1095): диалог сохранения нового и
 * переименования лежащего.
 *
 * Занятое название — отказ ПОД ПОЛЕМ, словами сервера, без предложения заменить: заменить чужое
 * общее одним нажатием значило бы стереть его настройку у всех, кто им пользуется.
 */
export function SaveViewDialog({ heading, action, initial, share, onSave, onClose }: {
  heading: string;
  /** Подпись кнопки: «Сохранить» либо «Переименовать». */
  action: string;
  initial: string;
  /** Показать флажок «Общее». Не назван — признак диалог не трогает (переименование, нет права). */
  share?: boolean;
  /** Отклонённый промис — отказ сервера: диалог остаётся и показывает причину под полем. */
  onSave: (title: string, shared: boolean) => Promise<unknown>;
  onClose: () => void;
}) {
  const [title, setTitle] = useState(initial);
  const [shared, setShared] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!title.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      await onSave(title.trim(), shared);
      onClose();
    } catch (refusal) {
      setError(apiError(refusal, 'Представление не сохранено'));
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title={heading} busy={busy}
      isDirty={title.trim() !== initial.trim() || shared}>
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Название" value={title} maxLength={120} autoFocus required error={error ?? undefined}
          onChange={e => { setTitle(e.target.value); setError(null); }} />
        {share && (
          <Checkbox checked={shared} onChange={setShared}
            label="Общее" description="Его увидят все, кому открыта таблица." />
        )}
        <div className="flex justify-end gap-2 pt-1">
          <ModalExit />
          <Button type="submit" variant="filled" loading={busy} disabled={!title.trim()}>{action}</Button>
        </div>
      </form>
    </Modal>
  );
}
