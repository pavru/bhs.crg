import type { ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LinkIcon } from '@/shared/ui/icons';
import { useCommonDataEntry } from '@/shared/api/commonData';
import { TextField } from '@/shared/ui/TextField';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Значение связанного поля записи — поле только для чтения (Р3, sharedui-md3-17): одна строка, целиком —
 * в подсказке, знак происхождения — в хвосте. Ссылку на запись каталога (@@ref, issue #99) показывает
 * именем записи, а не сырым JSON, и знаком ссылки в хвосте; запись в архиве (issue #1185) — словом
 * после имени: связка цела, пометка — о выборе.
 */
export function BoundValueField({ label, refId, display, origin }: {
  label: string;
  /** Связанная запись каталога; тогда `display` не нужен. */
  refId?: string;
  display: string | null;
  /** Знак происхождения значения (`SourceOriginIcon`). */
  origin?: ReactNode;
}) {
  const { data: entry } = useCommonDataEntry(refId);
  const shown = refId
    ? entry ? `${entry.displayName}${entry.archived ? ' · в архиве' : ''}` : 'запись каталога…'
    : display;
  return (
    <Tooltip content={shown ?? undefined}>
      <TextField readOnly label={label} value={shown ?? 'нет данных'}
        trailing={(refId || origin) ? (
          <span className="flex items-center gap-1 pr-1">
            {refId && <Icon icon={LinkIcon} size={18} className="text-on-surface-variant" />}
            {origin}
          </span>
        ) : undefined} />
    </Tooltip>
  );
}
