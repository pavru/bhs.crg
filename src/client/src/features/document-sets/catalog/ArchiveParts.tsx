import { Archive, ArchiveRestore } from 'lucide-react';
import { Button } from '@/shared/ui/Button';

/**
 * Плашка над формой архивной записи. Править её можно — реквизиты архивной организации по-прежнему
 * уходят в печать старых документов, — поэтому плашка не запирает форму, а объясняет состояние.
 */
export function ArchivedBanner({ employee, onReturn, busy }: {
  employee: boolean;
  /** Не задано — у смотрящего нет права вести общие данные (или запись ведёт модуль): кнопки нет. */
  onReturn?: () => void;
  busy: boolean;
}) {
  return (
    <div className="mx-6 mt-3 shrink-0 flex items-start gap-2.5 rounded-md border border-stroke bg-surface2 px-3 py-2.5 text-xs text-fg2">
      <Archive size={15} className="shrink-0 mt-0.5 text-fg3" />
      <p className="flex-1 min-w-0">
        Запись в архиве: в списках выбора её нет, в уже сохранённых документах и счетах она остаётся.
        {employee && ' Архив — не увольнение: дата «Уволен с» задаётся отдельным полем.'}
      </p>
      {onReturn && (
        <Button variant="tonal" size="sm" icon={<ArchiveRestore size={14} />} onClick={onReturn} disabled={busy}>
          Вернуть из архива
        </Button>
      )}
    </div>
  );
}
