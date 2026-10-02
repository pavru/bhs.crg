import { useCallback, useMemo } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { filterChanged, parseView, viewHash, type TableView } from './tableViewState';

/**
 * Состояние экрана таблицы — в адресе страницы (ТЗ CORE-33; задача G1e, issue #1092). Своего
 * состояния у экрана нет: что написано в адресе, то и показано, поэтому перезагрузка и ссылка
 * открывают ту же таблицу.
 *
 * ⚠️ В историю браузера новой записью ложится только СМЕНА ОТБОРА. Сортировка, колонки, страница и
 * открытая строка заменяют текущую запись. Иначе «назад» перебирал бы щелчки по шапке, и до
 * предыдущего отбора пришлось бы отступать вслепую, считая шаги.
 */
export function useTableView(): [TableView, (next: TableView) => void] {
  const { hash } = useLocation();
  const navigate = useNavigate();
  const view = useMemo(() => parseView(hash), [hash]);

  const setView = useCallback((next: TableView) => {
    const target = viewHash(next);
    if (target === viewHash(view)) return;
    void navigate({ hash: target }, { replace: !filterChanged(view, next) });
  }, [navigate, view]);

  return [view, setView];
}
