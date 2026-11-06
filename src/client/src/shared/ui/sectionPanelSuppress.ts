import { createContext, useCallback, useContext, useEffect, useState } from 'react';

/**
 * Подавление подпанели раздела страницей (costs-invoice-screen-10-layout, D-10.5, ТЗ COST-6.3): пока открыт
 * счёт, подпанели нет, а выбор человека (`rail`, на сервере) НЕ переписывается — закрыл счёт, и подпанель
 * вернулась такой, какой была.
 *
 * <p>Подавление, а не настройка: `setRail('collapsed')` при открытии переписывал бы выбор человека экраном.
 * Действует, пока смонтирован тот, кто подавляет, — уход со страницы снимает его сам.</p>
 */
export const SuppressSectionPanelContext = createContext<(() => () => void) | null>(null);

/** Страница: «пока `active`, подпанели раздела нет». */
export function useSuppressSectionPanel(active: boolean) {
  const suppress = useContext(SuppressSectionPanelContext);
  useEffect(() => (active && suppress ? suppress() : undefined), [active, suppress]);
}

/**
 * Состояние подпанели в оболочке: показана ли она в раскладке, открыта ли поверх, что делает «Показать
 * панель». При подавлении «Показать» открывает её поверх формы модальным drawer (решение Э11) и
 * предпочтения не пишет: раскладка формы и место файла не меняются.
 */
export function useSectionPanel({ has, wanted, expand }: {
  /** У адреса есть подпанель. */
  has: boolean;
  /** Выбор человека (или ширина окна при `auto`). */
  wanted: boolean;
  /** Записать выбор «показана» — только без подавления. */
  expand: () => void;
}) {
  const [suppressors, setSuppressors] = useState(0);
  const [drawer, setDrawer] = useState(false);
  const suppress = useCallback(() => {
    setSuppressors(n => n + 1);
    return () => setSuppressors(n => n - 1);
  }, []);
  const suppressed = suppressors > 0;
  // Подавление снято — drawer не переживает его: подпанель уже стоит в раскладке.
  if (drawer && (!suppressed || !has)) setDrawer(false);
  return {
    suppress,
    shown: has && wanted && !suppressed,
    drawer: drawer && suppressed && has,
    show: () => (suppressed ? setDrawer(true) : expand()),
    closeDrawer: () => setDrawer(false),
  };
}
