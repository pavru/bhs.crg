import { useCallback, useMemo } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { DEFAULT_VIEW, filterChanged, parseView, viewHash, type TableView } from './tableViewState';

/**
 * Состояние экрана таблицы — в адресе страницы (ТЗ CORE-33; задача G1e, issue #1092). Своего
 * состояния у экрана нет: что написано в адресе, то и показано, поэтому перезагрузка и ссылка
 * открывают ту же таблицу.
 *
 * ⚠️ В историю браузера новой записью ложится только СМЕНА ОТБОРА. Сортировка, колонки, страница и
 * открытая строка заменяют текущую запись. Иначе «назад» перебирал бы щелчки по шапке, и до
 * предыдущего отбора пришлось бы отступать вслепую, считая шаги.
 *
 * `base` — от чего адрес отсчитан: умолчания таблицы либо настройка готового представления (G4,
 * issue #1097). Вызывающий обязан держать её одним и тем же объектом между отрисовками.
 */
export function useTableView(base: TableView = DEFAULT_VIEW): [TableView, (next: TableView) => void] {
  const { hash } = useLocation();
  const navigate = useNavigate();
  const view = useMemo(() => parseView(hash, base), [hash, base]);

  const setView = useCallback((next: TableView) => {
    const target = viewHash(next, base);
    if (target === viewHash(view, base)) return;
    void navigate({ hash: target }, { replace: !filterChanged(view, next) });
  }, [navigate, view, base]);

  return [view, setView];
}
