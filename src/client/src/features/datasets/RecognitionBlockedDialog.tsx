import { useNavigate } from 'react-router';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useAuth } from '@/shared/hooks/useAuth';

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
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Распознавание не запущено"
      footer={
        <div className="flex justify-end gap-2">
          {/* Кнопка ведёт в раздел под AdminRoute: обычному пользователю она дала бы 403 — то есть
              совет, за которым следует отказ. Ему адресован другой текст, ниже. */}
          {isAdmin && configurable && (
            <Button type="button" variant="text"
              onClick={() => { onClose(); navigate('/settings'); }}>
              Открыть настройки
            </Button>
          )}
          <Button type="button" variant="filled" onClick={onClose}>Понятно</Button>
        </div>
      }>
      <div className="space-y-3 max-w-[520px]">
        <p className="text-sm text-fg1">{message}</p>
        {/* Совет даём только там, где он лечит: отказ движка на середине или сбой сети в настройках
            не чинится, и отправлять туда человека значит тратить его время. */}
        {configurable && (
        <p className="text-sm text-fg2">
          {isAdmin
            ? 'Что сделать: в «Настройка системы → Поиск и распознавание» выберите модель, которая принимает изображения, или поставьте выше другой движок распознавания.'
            : 'Что сделать: обратитесь к администратору — нужно сменить модель распознавания в настройках системы.'}
        </p>
        )}
      </div>
    </Modal>
  );
}
