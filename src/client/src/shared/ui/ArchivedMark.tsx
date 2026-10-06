import { Archive } from 'lucide-react';
import { ARCHIVED_HINT, ARCHIVED_WORD } from './archive';

/**
 * Пометка «в архиве» после названия записи (issue #1185). Ставит её и ядро, и модули — поэтому она
 * общая: слово и значок, разошедшиеся по экранам, читались бы как разные состояния.
 *
 * `words={false}` — только значок, для тесных мест (ячейка таблицы); слово тогда живёт в подсказке
 * и в `aria-label`.
 */
export function ArchivedMark({ words = true, className = '' }: { words?: boolean; className?: string }) {
  return (
    <span title={ARCHIVED_HINT} aria-label={words ? undefined : ARCHIVED_WORD}
      className={`inline-flex items-center gap-1 text-[11px] text-fg3 shrink-0 ${className}`}>
      <Archive size={11} className="shrink-0" />
      {words && ARCHIVED_WORD}
    </span>
  );
}
