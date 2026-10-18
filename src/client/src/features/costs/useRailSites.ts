import { onInvoiceWritten, type InvoiceListItem, type InvoiceView } from '@/shared/api/invoices';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { useSavedHiddenToast } from '@/shared/siteFilter/useSavedHiddenToast';
import { SAVED_HIDDEN, hiddenBySite, viewSites } from './invoiceSites';

/**
 * Рейл счетов под фильтром «Избранные стройки» (issue #1221): что скрыто, что сказать над пустым
 * списком и над формой, и тост после своей записи, уводящей счёт из рейла.
 *
 * <p>Урезается только ОБЩИЙ список (`cuts`). Рабочие очереди — «Разобрать», «наведите порядок», «Не
 * распознано» — и отбор загрузки не урезаются: из них счета выходят работой, а не фильтром, и счёт,
 * который надо разобрать, не может прятаться за звездой (#1217).</p>
 */
export function useRailSites({ cuts, list, selected, matches }: {
  cuts: boolean;
  list: InvoiceListItem[] | undefined;
  selected: string | null;
  matches: (item: InvoiceListItem) => boolean;
}) {
  const favorites = useSiteFilter();
  const hides = (item: InvoiceListItem) => cuts && hiddenBySite(item.constructionIds, favorites);
  const all = list ?? [];
  const hidden = all.filter(hides);
  const open = selected ? all.find(i => i.id === selected) : undefined;

  // Своя запись, уведшая открытый счёт из рейла: решает общий хук, по стройкам вида счёта.
  const onSaved = useSavedHiddenToast<InvoiceView>(onInvoiceWritten, {
    selected,
    hides: view => cuts && hiddenBySite(viewSites(view), favorites),
    text: SAVED_HIDDEN,
    showAll: () => favorites.setOn(false),
  });

  return {
    hides,
    /**
     * Фильтр ещё не известен (настройки или справочник в пути): строк общего списка нет вовсе. Полный
     * список, через миг урезанный под курсором, человек прочёл бы как отобранный (ревью PR #1334).
     */
    pending: cuts && favorites.status === 'loading',
    /** Сколько счетов общего списка скрыто фильтром. */
    hidden: hidden.length,
    /** Сколько скрытых нашёл бы поиск; без запроса — все скрытые. Это число и пишет строка у рейла. */
    foundHidden: hidden.filter(matches).length,
    /** Открытый счёт есть в списке, но скрыт фильтром — форма говорит, почему его нет слева. */
    openHidden: !!open && hides(open),
    onSaved,
    showAll: () => favorites.setOn(false),
  };
}
