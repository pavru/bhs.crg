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
  // Подпись и замыкающий слот — в одной обёртке, чтобы на узком списке слот уходил под пояснение, а не отнимал
  // ширину у подписи: справа от слота ещё действие строки, и на 375 подписи оставалась одна буква (снимки 3.1
  // sharedui-md3-21). «Узкий» меряет список (`LIST_CONTAINER`), а не экран и не строка: на планшете рядом стоит
  // колонка списка, и тесно уже при широком экране, а решение по строке разнило бы строки одного списка.
  return (
    <>
      {lead}
      <span className="flex-1 min-w-0 flex items-center gap-x-4 @max-md/list:flex-wrap">
        <span className="flex-1 min-w-0 flex flex-col @max-md/list:basis-full">
          {present(overline) && <span className="text-label-small text-primary">{overline}</span>}
          <span className="truncate text-body-large" onPointerEnter={full ? fullTextIfCut(full) : undefined}>
            {headline}
          </span>
          {present(supporting) && (
            <span className={`text-body-medium ${tone} ${typeof supporting === 'string' ? 'truncate' : ''}`}>{supporting}</span>
          )}
        </span>
        {present(trailing) && <span className={`shrink-0 flex items-center gap-2 text-label-small ${tone}`}>{trailing}</span>}
      </span>
    </>
  );
}
