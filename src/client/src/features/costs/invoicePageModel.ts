import { useCallback, useState } from 'react';
import { placeBand } from './invoicePlace';
import { CalendarClock, LinkIcon, NoteAdd, Sell } from '@/shared/ui/icons';
import type { RowAction } from '@/shared/ui/RowActionsMenu';

/** Ширина рабочей области и действия страницы счетов при открытом счёте (costs-invoice-screen-10-layout). */

/**
 * Ширина рабочей области — меряет `ResizeObserver` контейнера страницы, а не окно (D-10.3): подпанель,
 * масштаб браузера и полосы оболочки окна не знают. До первого замера — окно без рейла 80: первый кадр
 * не рисует «узко» на широком экране.
 *
 * <p>⚠️ Ширина меняется только на смене места (`placeBand`), а не на каждом тике (ревью PR #1787): форма
 * с сорока строками иначе перерисовывалась бы десятки раз в секунду, пока тянут край окна. Внутри полосы
 * число устаревает нарочно — ширину панели файла задаёт CSS (`FILE_WIDTH_CSS`), а не оно.</p>
 */
export function useWorkWidth(): [(el: HTMLElement | null) => void, number] {
  const [width, setWidth] = useState(() => Math.max(0, window.innerWidth - 80));
  const ref = useCallback((el: HTMLElement | null) => {
    if (!el || typeof ResizeObserver === 'undefined') return;
    const take = (next: number) => setWidth(prev => (placeBand(prev) === placeBand(next) ? prev : next));
    take(el.getBoundingClientRect().width);
    const observer = new ResizeObserver(([entry]) => take(entry.contentRect.width));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width];
}

/**
 * Действия страницы, которые при открытом счёте уходят из её шапки (D-10.8): в «⋮» списка, а где списка
 * нет — в «⋮» сводки. Каждое — тому же праву, что и кнопка шапки.
 */
export function pageActions({ articles, edit, creating, onArticles, onMatches, onTerms, onNew }: {
  /** Ведёт справочник статей вне строек. */
  articles: boolean;
  /** Вводит счета. */
  edit: boolean;
  creating: boolean;
  onArticles: () => void; onMatches: () => void; onTerms: () => void; onNew: () => void;
}): RowAction[] {
  const actions: RowAction[] = [];
  if (edit) actions.push({ key: 'new', label: 'Новый счёт', icon: NoteAdd, loading: creating, onSelect: onNew });
  if (articles) actions.push({ key: 'articles', label: 'Статьи вне строек', icon: Sell, onSelect: onArticles });
  if (edit) {
    actions.push({ key: 'matches', label: 'Соответствия', icon: LinkIcon, onSelect: onMatches });
    actions.push({ key: 'terms', label: 'Условия поставщиков', icon: CalendarClock, onSelect: onTerms });
  }
  return actions;
}
