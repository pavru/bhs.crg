import { createContext, useMemo, useState } from 'react';

/**
 * Что показывает сплит, когда ему тесно (issue #1355): список или карточку — по очереди, а не рядом.
 * На широком месте не значит ничего: там видно оба.
 *
 * Состояние держит СТРАНИЦА, а не сплит, когда выбор строки живёт у неё: страница зовёт
 * `showDetail()` там же, где выбирает строку. Пометка строк для сплита (атрибутом, перехватом
 * щелчка) не годится — щелчок по чипу отбора или по «Повторить» тоже щелчок внутри списка, и
 * отличить их от выбора строки сплиту нечем. Выбор у страницы, где он и сделан.
 */
export interface ListDetailView {
  view: 'list' | 'detail';
  showDetail: () => void;
  showList: () => void;
}

/**
 * `start` — с чего начинать на узком месте: со списка, если строка выбрана сама (первая по
 * умолчанию), с карточки — если её назвал адрес (переход из уведомления, ссылка).
 */
export function useListDetailView(start: 'list' | 'detail' = 'list'): ListDetailView {
  const [view, setView] = useState(start);
  return useMemo(() => ({ view, showDetail: () => setView('detail'), showList: () => setView('list') }), [view]);
}

export const ListDetailViewContext = createContext<ListDetailView | null>(null);
