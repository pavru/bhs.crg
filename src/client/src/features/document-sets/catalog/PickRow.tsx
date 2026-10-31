import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { Checkbox } from '@/shared/ui/Checkbox';
import type { CommonDataEntry } from '@/shared/api/types';

/**
 * Строка записи в режиме выбора: галочка и название. Действий строки (правка, архив, удаление) в
 * режиме нет — щелчок по строке означает одно: отметить.
 */
export function PickRow({ entry, checked, onToggle, className = '' }: {
  entry: CommonDataEntry; checked: boolean; onToggle: () => void; className?: string;
}) {
  return (
    <label className={`flex items-center gap-3 px-4 py-3 cursor-pointer state-layer ${className}`}>
      <Checkbox checked={checked} onChange={onToggle} />
      <span className={`flex-1 min-w-0 flex items-center gap-2 text-body-medium font-medium ${entry.archived ? 'text-on-surface-variant' : 'text-on-surface'}`}>
        <span className="truncate">{entry.displayName}</span>
        {entry.archived && <ArchivedMark className="font-normal" />}
      </span>
    </label>
  );
}
