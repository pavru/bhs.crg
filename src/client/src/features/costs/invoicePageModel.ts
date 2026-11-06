import { useCallback, useState } from 'react';
import { CalendarClock, LinkIcon, NoteAdd, Sell } from '@/shared/ui/icons';
import type { RowAction } from '@/shared/ui/RowActionsMenu';

/** Ширина рабочей области и действия страницы счетов при открытом счёте (costs-invoice-screen-10-layout). */

/**
 * Ширина рабочей области — меряет `ResizeObserver` контейнера страницы, а не окно (D-10.3): подпанель,
 * масштаб браузера и полосы оболочки окна не знают. До первого замера — окно без рейла 80: первый кадр
 * не рисует «узко» на широком экране.
 */
export function useWorkWidth(): [(el: HTMLElement | null) => void, number] {
  const [width, setWidth] = useState(() => Math.max(0, window.innerWidth - 80));
  const ref = useCallback((el: HTMLElement | null) => {
    if (!el || typeof ResizeObserver === 'undefined') return;
    setWidth(el.getBoundingClientRect().width);
    const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
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
