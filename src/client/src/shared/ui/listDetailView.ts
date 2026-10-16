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
 * `addressed` — запись, которую назвал АДРЕС (счёт, накладная), если выбор живёт в нём. Тогда с
 * карточки начинается страница, открытая ссылкой, и на карточку же переходит страница, когда адрес
 * сменился без щелчка по строке: уведомление, палитра, «Вперёд» браузера. Без этого на телефоне
 * ссылка на счёт меняла бы адрес и оставляла на экране список — выглядит как «ссылка не сработала».
 *
 * Щелчок по строке всё равно зовёт `showDetail()`: после «К списку» щелчок по ТОЙ ЖЕ строке адреса
 * не меняет, а карточку показать обязан.
 */
export function useListDetailView(addressed?: string | null): ListDetailView {
  const [view, setView] = useState<'list' | 'detail'>(addressed ? 'detail' : 'list');
  // Смена адреса — во время отрисовки, а не эффектом: так React советует выводить состояние из
  // пропа, и кадра со списком вместо карточки не бывает.
  const [seen, setSeen] = useState(addressed);
  if (addressed !== seen) {
    setSeen(addressed);
    if (addressed) setView('detail');
  }
  return useMemo(() => ({ view, showDetail: () => setView('detail'), showList: () => setView('list') }), [view]);
}

export const ListDetailViewContext = createContext<ListDetailView | null>(null);
