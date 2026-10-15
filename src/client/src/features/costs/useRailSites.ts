import { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { onInvoiceWritten, type InvoiceListItem, type InvoiceView } from '@/shared/api/invoices';
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
  const qc = useQueryClient();
  const hides = (item: InvoiceListItem) => cuts && hiddenBySite(item.constructionIds, favorites);
  const all = list ?? [];
  const hidden = all.filter(hides);
  const open = selected ? all.find(i => i.id === selected) : undefined;

  // Своя запись, уведшая счёт из рейла. Решение — по прежнему и новому виду счёта, а не по списку:
  // список перечитывается позже, и чужая правка, пришедшая перечитыванием, «Сохранено» не скажет.
  // Только об ОТКРЫТОМ счёте (ревью PR #1334): ответ приходит и после ухода на другой, и «счёт скрыт»
  // над его формой читалось бы как о нём.
  const now = useRef({ cuts, favorites, selected });
  useLayoutEffect(() => { now.current = { cuts, favorites, selected }; });
  const onSaved = useCallback((before: InvoiceView | undefined, after: InvoiceView) => {
    const at = now.current;
    if (!before || !at.cuts || after.id !== at.selected) return;
    if (!hiddenBySite(viewSites(after), at.favorites) || hiddenBySite(viewSites(before), at.favorites)) return;
    toast.info(SAVED_HIDDEN, { action: { label: 'Показать все', onClick: () => at.favorites.setOn(false) } });
  }, [toast]);
  useEffect(() => onInvoiceWritten(qc, onSaved), [qc, onSaved]);

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
