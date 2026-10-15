import { FilterHidesAll, NotInListNotice } from '@/shared/siteFilter/SiteFilterNotes';
import { SEARCH_ALL_SITES, SHOW_ALL_SITES } from '@/shared/siteFilter/siteFilterTexts';
import { NOT_IN_RAIL, SEARCH_HIDDEN, hiddenEmpty } from './invoiceSites';

/** Строки рейла счетов под фильтром «Избранные стройки» (issue #1221); решения — в `useRailSites`. */

/**
 * Пустой рейл, когда счета есть, но скрыты фильтром. `null` — сказать нечего, говорит обычное пустое
 * состояние. Под поиском — только если поиск нашёл бы скрытые: рейл знает их все.
 */
export function RailSitesEmpty({ searching, hidden, foundHidden, onShowAll }: {
  searching: boolean; hidden: number; foundHidden: number; onShowAll: () => void;
}) {
  if (searching ? foundHidden === 0 : hidden === 0) return null;
  return (
    <FilterHidesAll testId="invoice-rail-sites-empty" onShowAll={onShowAll}
      text={searching ? SEARCH_HIDDEN : hiddenEmpty(hidden)} action={searching ? SEARCH_ALL_SITES : SHOW_ALL_SITES} />
  );
}

/** Над формой счёта, скрытого в рейле. */
export function NotInRailNotice({ onShowAll }: { onShowAll: () => void }) {
  return <NotInListNotice testId="invoice-not-in-rail" text={NOT_IN_RAIL} onShowAll={onShowAll} />;
}
