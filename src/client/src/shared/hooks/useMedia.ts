import { useEffect, useState } from 'react';

/**
 * Рабочий стол — от `md` (768 px). Ниже — телефон (issue #1355, #1434): рейла нет, разделы — в нижней
 * навигации, страницы раздела — в меню заголовка. Порог записан и здесь, и классами `md:` — то, что
 * решается в коде (меню заголовка, пункт «Администрирование»), обязано переключаться там же, где
 * разметка.
 */
export const WIDE = '(min-width: 768px)';

/** Совпадает ли медиазапрос — и следит за ним, пока окно меняет размер. */
export function useMedia(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = () => setMatches(list.matches);
    list.addEventListener('change', onChange);
    return () => list.removeEventListener('change', onChange);
  }, [query]);
  return matches;
}
