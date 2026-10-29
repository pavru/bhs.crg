import { Icon } from './Icon';
import { Archive } from './icons';
import { ARCHIVED_HINT, ARCHIVED_WORD } from './archive';
import { Tooltip } from './Tooltip';

/**
 * Пометка «в архиве» после названия записи (issue #1185). Ставит её и ядро, и модули — поэтому она
 * общая: слово и значок, разошедшиеся по экранам, читались бы как разные состояния.
 *
 * `words={false}` — только значок, для тесных мест (ячейка таблицы); слово тогда живёт в подсказке
 * и в `aria-label`.
 */
export function ArchivedMark({ words = true, className = '' }: { words?: boolean; className?: string }) {
  return (
    <Tooltip content={ARCHIVED_HINT}>
      <span aria-label={words ? undefined : ARCHIVED_WORD}
        className={`inline-flex items-center gap-1 text-label-small text-on-surface-variant shrink-0 ${className}`}>
        <Icon icon={Archive} size={18} className="shrink-0" />
        {words && ARCHIVED_WORD}
      </span>
    </Tooltip>
  );
}
