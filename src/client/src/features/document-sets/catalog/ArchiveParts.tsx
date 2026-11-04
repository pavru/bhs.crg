import { Archive, Unarchive } from '@/shared/ui/icons';
import { Banner } from '@/shared/ui/Banner';

/**
 * Banner над формой архивной записи ([UI-73]). Править её можно — реквизиты архивной организации
 * по-прежнему уходят в печать старых документов, — поэтому сообщение не запирает форму, а объясняет
 * состояние.
 */
export function ArchivedBanner({ employee, onReturn, busy }: {
  employee: boolean;
  /** Не задано — у смотрящего нет права вести общие данные (или запись ведёт модуль): кнопки нет. */
  onReturn?: () => void;
  busy: boolean;
}) {
  return (
    <Banner tone="info" icon={Archive}
      actions={onReturn ? [{ label: 'Вернуть из архива', icon: Unarchive, loading: busy, onClick: onReturn }] : []}>
      Запись в архиве: в списках выбора её нет, в уже сохранённых документах и счетах она остаётся.
      {employee && ' Архив — не увольнение: дата «Уволен с» задаётся отдельным полем.'}
    </Banner>
  );
}
