import { createContext, useContext, useMemo, useState } from 'react';
import { useSiteFilter, type SiteEntry } from './siteFilterContext';
import { siteChoices, type SiteChoices } from './siteChoices';

/**
 * Выход «Показать все стройки» в выпадающих списках выбора стройки (issue #1219, §62.4).
 *
 * <p>Раскрытие действует на ФОРМУ, а не на поле: в разноске списков выбора несколько в ряд, и
 * раскрывать каждый отдельно — та же ловушка, размноженная. Глобальный фильтр раскрытие не трогает,
 * списки под формой не перечитывает и не запоминается: форма закрылась — раскрытие ушло вместе с ней.
 * Форма объявляет себя `SiteRevealScope`; список вне формы раскрывается сам по себе.</p>
 */
export interface SiteRevealValue {
  revealed: boolean;
  reveal: () => void;
}

export const SiteRevealContext = createContext<SiteRevealValue | null>(null);

/** Значение пункта-выхода в списке выбора стройки. Стройкой оно не бывает. */
export const REVEAL_CHOICE = '__show-all-sites__';

/**
 * Варианты выбора стройки для СПИСКА, который фильтр урезает. Регистрирует экран потребителем
 * фильтра: список урезан — сегмент в шапке горит, и выключить фильтр целиком можно там.
 */
export function useSiteChoices<T extends SiteEntry>(
  sites: readonly T[] | undefined,
  current: string | null | undefined,
): SiteChoices<T> & { reveal: () => void } {
  const filter = useSiteFilter();
  const scope = useContext(SiteRevealContext);
  const [own, setOwn] = useState(false);
  const revealed = scope ? scope.revealed : own;
  const reveal = scope ? scope.reveal : () => setOwn(true);
  const { active, favorites } = filter;
  const choices = useMemo(
    () => siteChoices(sites ?? [], current, { active, favorites }, revealed),
    [sites, current, active, favorites, revealed]);
  return { ...choices, reveal };
}

/**
 * Выбор в списке стройки: пункт-выход значения не меняет, а раскрывает форму. Список при этом
 * остаётся открытым — его держит `stayOpenOn={REVEAL_CHOICE}` у `Select` (ревью PR #1332).
 */
export function pickSite(choice: string, reveal: () => void, change: (value: string) => void) {
  if (choice === REVEAL_CHOICE) reveal(); else change(choice);
}
