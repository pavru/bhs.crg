import { useNavigate } from 'react-router';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { useAccess, NO_ACCESS, hasPermission } from '@/shared/api/access';

/**
 * «Распознавание не запущено» (issue #801) — отказ ДО постановки задачи: распознавать некому.
 *
 * Не ConfirmDialog: выбора здесь нет, это не «продолжить?» — задача не поставлена и поставлена не
 * будет, пока не сменят модель или движок. Диалог, а не строка в углу, потому что нажатие «Распознать»
 * на альбоме в двести листов человек считает запуском часовой работы: не заметить отказ он не должен.
 */
export function RecognitionBlockedDialog(
  { message, configurable = true, onClose }: { message: string; configurable?: boolean; onClose: () => void },
) {
  const navigate = useNavigate();
  // Совет даём по ПРАВУ: носителю роли с core.system.manage незачем советовать обратиться к
  // администратору — он и есть тот, кто чинит. По имени роли этот текст врал бы именно ему.
  const { data: access = NO_ACCESS } = useAccess();
  const canConfigure = hasPermission(access, 'core.system.manage');

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Распознавание не запущено"
      footer={<>
        {/* Кнопка ведёт в раздел под AdminRoute: обычному пользователю она дала бы 403 — то есть
            совет, за которым следует отказ. Ему адресован другой текст, ниже. */}
        {canConfigure && configurable && (
          <ModalPrimary variant="text" onClick={() => { onClose(); navigate('/settings'); }}>
            Открыть настройки
          </ModalPrimary>
        )}
        <ModalExit>Понятно</ModalExit>
      </>}>
      <div className="space-y-3">
        <p className="text-body-medium text-on-surface">{message}</p>
        {/* Совет даём только там, где он лечит: отказ движка на середине или сбой сети в настройках
            не чинится, и отправлять туда человека значит тратить его время. */}
        {configurable && (
        <p className="text-body-medium text-on-surface-variant">
          {canConfigure
            ? 'Что сделать: в «Система → Параметры системы → Поиск и распознавание» выберите модель, которая принимает изображения, или поставьте выше другой движок распознавания.'
            : 'Что сделать: обратитесь к администратору — нужно сменить модель распознавания в настройках системы.'}
        </p>
        )}
      </div>
    </Modal>
  );
}
