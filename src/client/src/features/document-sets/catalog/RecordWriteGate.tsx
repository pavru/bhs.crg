import type { ReactNode } from 'react';
import { recordWritePermission } from '@/shared/api/commonData';
import { useCan } from '@/shared/api/access';
import { ModalExit } from '@/shared/ui/Modal';

/**
 * Подвал формы записи: «Отмена» (выход окна — спросит о набранном) и — если у человека есть право
 * писать записи этой семьи — кнопка сохранения (WORK-59). Без права кнопки нет, а на её месте сказано почему: вид работы правит тот,
 * кто ведёт классификатор, и правка общих данных этого права не даёт. Нажатая, кнопка привела бы
 * к отказу 403 после заполненной формы.
 */
export function RecordWriteGate({ doorCode, children }: {
  /** Код семьи со своей дверью (`ownDoorCode`); не задан — запись общих данных. */
  doorCode: string | undefined;
  /** Кнопка сохранения. */
  children: ReactNode;
}) {
  const allowed = useCan().permission(recordWritePermission(doorCode));
  return (
    <>
      {!allowed && (
        <p className="mr-auto self-center text-body-medium text-on-surface-variant">
          Только просмотр: записи этого справочника ведёт тот, у кого есть право на него.
        </p>
      )}
      <ModalExit />
      {allowed && children}
    </>
  );
}
