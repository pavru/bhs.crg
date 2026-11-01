import type { ReactNode } from 'react';
import { recordWritePermission } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { ModalButtons, ModalExit, ModalNote } from '@/shared/ui/Modal';

/**
 * Ряд кнопок формы записи (`ModalButtons`): «Отмена» (выход окна — спросит о набранном) и — если у
 * человека есть право писать записи этой семьи — кнопка сохранения (WORK-59). Без права кнопки нет, а на её месте сказано почему: вид работы правит тот,
 * кто ведёт классификатор, и правка общих данных этого права не даёт. Нажатая, кнопка привела бы
 * к отказу 403 после заполненной формы.
 */
export function RecordWriteGate({ doorCode, className, children }: {
  /** Код семьи со своей дверью (`ownDoorCode`); не задан — запись общих данных. */
  doorCode: string | undefined;
  /** Кнопка сохранения — `ModalPrimary`. */
  children: ReactNode;
  /** Отступы и черта ряда. */
  className?: string;
}) {
  const allowed = useCan().permission(recordWritePermission(doorCode));
  return (
    <ModalButtons className={className}>
      {!allowed && (
        <ModalNote>Только просмотр: записи этого справочника ведёт тот, у кого есть право на него.</ModalNote>
      )}
      <ModalExit />
      {allowed && children}
    </ModalButtons>
  );
}
