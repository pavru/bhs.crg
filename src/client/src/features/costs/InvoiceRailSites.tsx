import { Star } from 'lucide-react';
import { NOT_IN_RAIL, SEARCH_HIDDEN, hiddenEmpty } from './invoiceSites';

/** Строки рейла счетов под фильтром «Избранные стройки» (issue #1221); решения — в `useRailSites`. */

const link = 'underline hover:text-fg1';

/**
 * Пустой рейл, когда счета есть, но скрыты фильтром: «Счетов пока нет» или «Ничего не найдено» здесь
 * были бы неправдой. `null` — сказать нечего, говорит обычное пустое состояние.
 */
export function RailSitesEmpty({ searching, hidden, foundHidden, onShowAll }: {
  searching: boolean; hidden: number; foundHidden: number; onShowAll: () => void;
}) {
  if (searching ? foundHidden === 0 : hidden === 0) return null;
  return (
    <div className="px-3 py-2 text-xs text-fg3 space-y-1" data-testid="invoice-rail-sites-empty">
      <p>{searching ? SEARCH_HIDDEN : hiddenEmpty(hidden)}</p>
      <button type="button" className={link} onClick={onShowAll}>
        {searching ? 'Искать по всем стройкам' : 'Показать все стройки'}
      </button>
    </div>
  );
}

/** Над формой счёта, скрытого в рейле: без неё форма справа выглядела бы счётом, которого нет. */
export function NotInRailNotice({ onShowAll }: { onShowAll: () => void }) {
  return (
    <p className="flex items-center gap-1.5 px-4 py-1.5 text-xs text-fg3 border-b border-stroke"
      data-testid="invoice-not-in-rail">
      <Star size={12} aria-hidden />
      <span>{NOT_IN_RAIL}.</span>
      <button type="button" className={link} onClick={onShowAll}>Показать все</button>
    </p>
  );
}
