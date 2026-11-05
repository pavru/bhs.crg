import type { ReactNode } from 'react';
import { Icon } from './Icon';
import type { IconDef } from './icons/types';
import { fullTextIfCut } from './fullTextIfCut';
import { listDepthPad } from './listDepth';

/** Ведущий слот — знак или узел, не оба: второй молча пропал бы. */
export type ListLead =
  | {
      /** Ведущий знак — 24 (Л5). */ icon?: IconDef; leading?: never;
      /** Цвет знака по смыслу строки (`text-primary`, `text-error`) — и на выбранной. Без него знак тихий: тон
       *  второй строки, на выбранной — `on-secondary-container`. */
      iconTone?: string;
    }
  | { /** Ведущий узел вместо знака (миниатюра листа) — внутри цели, неинтерактивный. */ leading?: ReactNode; icon?: never; iconTone?: never };

/** Содержимое строки — общее у строки списка (`ListItem`) и варианта выбора (`ListOption`). */
export type ListRowContent = ListLead & {
  /** Подпись. В форме `list` — `body-large` в одну строку с многоточием, в форме `menu` — `label-large`,
   *  длинная переносится. */
  headline: ReactNode;
  /** Полный текст подписи для подсказки `title` при обрезке (Р6). По умолчанию — сама подпись, если она строка. */
  title?: string;
  /** Пояснение. В форме `list` — второй строкой `body-medium` роли `on-surface-variant`: строкой — в одну
   *  строчку с многоточием, узлом — как есть (многострочное содержимое растит строку). В форме `menu` —
   *  замыкающим текстом `body-small` в той же строке. */
  supporting?: ReactNode;
  /** Замыкающий текст, число, пометка, шеврон. `false` и пустая строка — слота нет (`cond && …`): пустой слот
   *  с отступом отнимал бы 16 у подписи. */
  trailing?: ReactNode;
};

/** Вид строки по контейнеру ([UI-87], П1 = В): окно и список — `list`, слой под полем — `menu`. */
export type ListRowForm = 'list' | 'menu';

/** Слот есть: 0 — содержимое, `false`, пустая строка и null — нет (`cond && …` у вызывающих). */
export const present = (node: ReactNode) => node != null && node !== false && node !== '';

/**
 * Отступы и высота строки ([UI-84], [UI-87]). `list` — list item MD3: отступы 16, не ниже 48, с пояснением —
 * не ниже 64. `menu` — пункт плотного меню (D7): отступы 12, не ниже 40, на касании 48. `lead` и `tail` —
 * у строки есть сосед у края (флажок, действие), и отступ к нему — 8.
 */
export function listRowBox({ form, twoLine, lead, tail, depth }: {
  form: ListRowForm; twoLine: boolean; lead: boolean; tail: boolean; depth: number;
}): string {
  if (form === 'menu') {
    const pl = lead ? 'pl-2' : depth > 0 ? listDepthPad(depth) : 'pl-3';
    return `flex-1 min-w-0 flex items-center gap-3 ${pl} ${tail ? 'pr-1' : 'pr-3'} py-2 min-h-10 pointer-coarse:min-h-12`;
  }
  const pad = `${lead ? 'pl-2' : listDepthPad(depth)} ${tail ? 'pr-2' : 'pr-4'}`;
  return `flex-1 min-w-0 flex items-center gap-4 ${pad} py-2 ${twoLine ? 'min-h-16' : 'min-h-12'}`;
}

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
