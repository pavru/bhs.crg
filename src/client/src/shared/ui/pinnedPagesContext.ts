import { createContext, useContext } from 'react';

/** Закреплённые страницы (ТЗ AUTH-16.4, work-04) — значение `PinnedPagesProvider`. */
export interface PinnedPages {
  list: string[];
  /** Закреплена ли страница. */
  has: (to: string) => boolean;
  pin: (to: string) => void;
  unpin: (to: string) => void;
  move: (from: number, to: number) => void;
  /** Свёрнута ли группа «Закреплённые» в подпанели — тоже серверная настройка (`pinnedGroup`). */
  collapsed: boolean;
  setCollapsed: (collapsed: boolean) => void;
}

const EMPTY: PinnedPages = {
  list: [], has: () => false, pin: () => {}, unpin: () => {}, move: () => {}, collapsed: false, setCollapsed: () => {},
};

export const PinnedPagesContext = createContext<PinnedPages>(EMPTY);

export function usePinnedPages(): PinnedPages {
  return useContext(PinnedPagesContext);
}

