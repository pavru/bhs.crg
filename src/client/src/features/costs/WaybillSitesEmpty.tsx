import { SEARCH_HIDDEN, hiddenEmpty } from './useWaybillSites';

const link = 'underline hover:text-fg1';

/**
 * Пустой список накладных, когда накладные есть, но скрыты фильтром «Избранные стройки» (issue #1222):
 * «Накладных пока нет» или «Ничего не найдено» здесь были бы неправдой.
 *
 * <p>Под поиском числа нет: поиск идёт по загруженному, а скрытые сервер не прислал — сколько из них
 * нашлось бы, неизвестно. Известно одно: среди избранных не нашлось, а скрытые есть.</p>
 */
export function WaybillSitesEmpty({ searching, hidden, onShowAll }: {
  searching: boolean; hidden: number; onShowAll: () => void;
}) {
  return (
    <div className="px-3 py-2 text-xs text-fg3 space-y-1" data-testid="waybill-list-sites-empty">
      <p>{searching ? SEARCH_HIDDEN : hiddenEmpty(hidden)}</p>
      <button type="button" className={link} onClick={onShowAll}>
        {searching ? 'Искать по всем стройкам' : 'Показать все стройки'}
      </button>
    </div>
  );
}
