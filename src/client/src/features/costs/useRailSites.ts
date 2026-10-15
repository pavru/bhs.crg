import { useCallback, useLayoutEffect, useRef } from 'react';
import type { InvoiceListItem, InvoiceView } from '@/shared/api/invoices';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { useToast } from '@/shared/ui/Toast';
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
  const favorites = useSiteFilter(cuts);
  const toast = useToast();
  const hides = (item: InvoiceListItem) => cuts && hiddenBySite(item.constructionIds, favorites);
  const all = list ?? [];
  const hidden = all.filter(hides);
  const open = selected ? all.find(i => i.id === selected) : undefined;

  // Своя запись, уведшая счёт из рейла. Решение — по прежнему и новому виду счёта, а не по списку:
  // список перечитывается позже, и чужая правка, пришедшая перечитыванием, «Сохранено» не скажет.
  const now = useRef({ cuts, favorites });
  useLayoutEffect(() => { now.current = { cuts, favorites }; });
  const onSaved = useCallback((before: InvoiceView | undefined, after: InvoiceView) => {
    const at = now.current;
    if (!before || !at.cuts) return;
    if (!hiddenBySite(viewSites(after), at.favorites) || hiddenBySite(viewSites(before), at.favorites)) return;
    toast.info(SAVED_HIDDEN, { action: { label: 'Показать все', onClick: () => at.favorites.setOn(false) } });
  }, [toast]);

  return {
    hides,
    /** Сколько счетов общего списка скрыто фильтром. */
    hidden: hidden.length,
    /** Сколько скрытых нашёл бы поиск. */
    foundHidden: hidden.filter(matches).length,
    /** Открытый счёт есть в списке, но скрыт фильтром — форма говорит, почему его нет слева. */
    openHidden: !!open && hides(open),
    onSaved,
    showAll: () => favorites.setOn(false),
  };
}
