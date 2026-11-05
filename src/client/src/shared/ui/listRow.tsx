import type { ReactNode } from 'react';
import { Icon } from './Icon';
import { fullTextIfCut } from './fullTextIfCut';
import { present, type ListRowContent, type ListRowForm } from './listRowBox';

/**
 * Тело строки: ведущий знак, подпись с пояснением, замыкающий слот. `filled` — строка залита
 * `secondary-container` (открытая карточка, отмеченная, активный вариант): второстепенный текст и тихий знак
 * берут `on-secondary-container`.
 */
export function ListRowBody({ form, filled, icon, iconTone, leading, headline, title, supporting, trailing, overline }:
  ListRowContent & { form: ListRowForm; filled: boolean; /** Надпись над подписью (`ListOption`, форма `list`). */ overline?: ReactNode }) {
  const tone = filled ? 'text-on-secondary-container' : 'text-on-surface-variant';
  const full = title ?? (typeof headline === 'string' ? headline : undefined);
  const lead = icon ? <Icon icon={icon} size={24} className={`shrink-0 ${iconTone ?? tone}`} /> : leading;
  if (form === 'menu') {
    return (
      <>
        {lead}
        <span className="flex-1 min-w-0 break-words text-label-large">{headline}</span>
        {present(supporting) && <span className={`shrink-0 text-body-small ${tone}`}>{supporting}</span>}
        {present(trailing) && <span className={`shrink-0 flex items-center gap-2 text-label-small ${tone}`}>{trailing}</span>}
      </>
    );
  }
  return (
    <>
      {lead}
      <span className="flex-1 min-w-0 flex flex-col">
        {present(overline) && <span className="text-label-small text-primary">{overline}</span>}
        <span className="truncate text-body-large" onPointerEnter={full ? fullTextIfCut(full) : undefined}>
          {headline}
        </span>
        {present(supporting) && (
          <span className={`text-body-medium ${tone} ${typeof supporting === 'string' ? 'truncate' : ''}`}>{supporting}</span>
        )}
      </span>
      {present(trailing) && <span className={`shrink-0 flex items-center gap-2 text-label-small ${tone}`}>{trailing}</span>}
    </>
  );
}
