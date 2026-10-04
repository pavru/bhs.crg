import { useCallback, useMemo } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { DEFAULT_VIEW, addressChange, parseView, type TableView, type ViewChange } from './tableViewState';

/**
 * Состояние экрана таблицы — в адресе страницы (ТЗ CORE-33; задача G1e, issue #1092). Своего
 * состояния у экрана нет: что написано в адресе, то и показано, поэтому перезагрузка и ссылка
 * открывают ту же таблицу.
 *
 * ⚠️ В историю браузера новой записью ложится только СМЕНА ОТБОРА. Сортировка, колонки, страница и
 * открытая строка заменяют текущую запись. Иначе «назад» перебирал бы щелчки по шапке, и до
 * предыдущего отбора пришлось бы отступать вслепую, считая шаги.
 *
 * ⚠️ Изменение — ФУНКЦИЯ от текущего состояния, а не готовое состояние: считается оно от адреса на
 * момент действия (`addressChange`). Состояние `view`, с которым экран нарисован, отстаёт от адреса
 * на время перерисовки, и посчитанное от него стирало бы правку, сделанную мгновением раньше.
 *
 * ⚠️ Хук читает адрес у БРАУЗЕРА (`window.location`), поэтому работает только под маршрутизатором,
 * который адрес браузера и ведёт (`BrowserRouter`, без `basename`). Под `MemoryRouter` запись ушла бы
 * в его память, а чтение — мимо неё.
 *
 * `base` — от чего адрес отсчитан: умолчания таблицы либо настройка готового представления (G4,
 * issue #1097). Вызывающий обязан держать её одним и тем же объектом между отрисовками.
 */
export function useTableView(base: TableView = DEFAULT_VIEW): [TableView, (change: ViewChange) => void] {
  const { hash, pathname } = useLocation();
  const navigate = useNavigate();
  const view = useMemo(() => parseView(hash, base), [hash, base]);

  const setView = useCallback((change: ViewChange) => {
    // Адрес уже чужой — экран уходит (щёлкнули по ссылке, перерисовка ещё не пришла): действие
    // опоздало. Его фрагмент читался бы от настройки ЭТОГО экрана, а запись вернула бы человека сюда.
    if (window.location.pathname !== pathname) return;
    // Адрес — у браузера, а не из `useLocation`: тот отдаёт адрес последней ОТРИСОВКИ.
    const target = addressChange(window.location.hash, base, change);
    if (target) void navigate({ hash: target.hash }, { replace: target.replace });
  }, [navigate, base, pathname]);

  return [view, setView];
}
