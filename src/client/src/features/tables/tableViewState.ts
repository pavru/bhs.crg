import type { FilterNode } from '@/shared/api/types';
import type { FilterChange } from '@/shared/filter/chipsModel';
import { TABLE_PAGE, type TableDeclaration, type TablePreset, type TableSort } from '@/shared/api/tables';

/**
 * Состояние экрана таблицы и его запись в адресе (ТЗ CORE-33: «состояние — в адресе страницы»;
 * задача G1e, issue #1092). Чистое: адрес ↔ состояние проверяется тестом, а не глазами.
 *
 * ⚠️ Состояние живёт во ФРАГМЕНТЕ адреса (после `#`), а не в строке запроса. Строка запроса при
 * перезагрузке страницы уходит серверу, стоящему перед приложением, а у него потолок длины — 8 КБ;
 * кириллица стоит шести байт за знак, и отбор «поставщик из списка» либо два десятка выбранных
 * колонок в этот потолок упираются. Отказ пришёл бы пустой страницей с кодом 414, без причины и без
 * экрана, на котором её можно показать. Фрагмент серверу не отправляется вовсе.
 */

/** Итог по колонке: сумма, среднее, наименьшее, наибольшее, количество. */
export type Aggregate = 'sum' | 'avg' | 'min' | 'max' | 'count';

const AGGREGATE_NAMES: readonly Aggregate[] = ['sum', 'avg', 'min', 'max', 'count'];

/** Размеры страницы, которые предлагает экран. */
export const PAGE_SIZES: readonly number[] = [50, 100, TABLE_PAGE, 500];

export interface ColumnTotal {
  column: string;
  aggregate: Aggregate;
}

export interface TableView {
  /** Колонки и их порядок; null — все колонки таблицы в её порядке. */
  columns: string[] | null;
  filter: FilterNode | null;
  /**
   * Отбор из адреса, который деревом условий не разобрался, — как есть. Не выбрасывается: он уходит
   * серверу, и отказывает сервер. Выбросить его здесь значило бы показать ВСЕ строки под видом
   * отобранных.
   */
  brokenFilter: string | null;
  sort: TableSort[];
  totals: ColumnTotal[];
  /** Сколько первых колонок закреплено слева. */
  pinned: number;
  /** Страница, с единицы. */
  page: number;
  size: number;
  /** Ключ строки, открытой в боковой панели. */
  row: string | null;
}

export const DEFAULT_VIEW: TableView = {
  columns: null, filter: null, brokenFilter: null, sort: [], totals: [], pinned: 0, page: 1,
  size: TABLE_PAGE, row: null,
};

/**
 * Настройка готового представления — состоянием экрана (задача G4, issue #1097). От неё экран
 * отсчитывает адрес: пока человек ничего не менял, фрагмент пуст, а на экране — «Реестр счетов» с
 * его колонками, итогами и закреплением.
 *
 * Итог с незнакомым словом пропускается, как и в адресе: считать по нему нечего. Сервер такое
 * представление не объявит — он проверяет слова итогов при старте.
 */
export function presetView(preset: TablePreset): TableView {
  return {
    ...DEFAULT_VIEW,
    columns: [...preset.columns],
    sort: preset.sort.map(s => ({ ...s })),
    totals: preset.totals
      .filter((t): t is ColumnTotal => (AGGREGATE_NAMES as readonly string[]).includes(t.aggregate)),
    pinned: preset.pinned,
  };
}

/**
 * Что стоит под адресом с кодом представления (G4, issue #1097). Пять ответов, и у каждого своё
 * поведение экрана — поэтому они названы, а не выводятся на месте из «есть ли preset»:
 *
 * - `table` — код не назван, это таблица целиком;
 * - `pending` — описание ещё не пришло: настройка едет в нём, строки ждут;
 * - `found` — представление есть;
 * - `off` — модуль выключен: представлений у него нет ВОВСЕ, и говорит сама таблица — «модуль
 *   выключен». Строки запрашиваются: иначе экран вечно показывал бы «Строки загружаются…» под
 *   состоянием, у которого есть название (ревью PR #1177);
 * - `missing` — кода у таблицы нет: отказ экрана, строки не запрашиваются.
 */
export type PresetLookup =
  | { state: 'table' | 'pending' | 'off' | 'missing' }
  | { state: 'found'; preset: TablePreset };

export function presetLookup(
  code: string | undefined, declaration: Pick<TableDeclaration, 'state' | 'views'> | undefined,
): PresetLookup {
  if (!code) return { state: 'table' };
  if (!declaration) return { state: 'pending' };
  if (declaration.state === 'module-off') return { state: 'off' };
  const preset = declaration.views?.find(v => v.code.toLowerCase() === code.toLowerCase());
  return preset ? { state: 'found', preset } : { state: 'missing' };
}

/** Запрашивать ли строки: под `pending` рано, под `missing` нечего. */
export function rowsWanted(lookup: PresetLookup): boolean {
  return lookup.state !== 'pending' && lookup.state !== 'missing';
}

/**
 * Адрес таблицы целиком с ТЕМ ЖЕ отбором (G4, issue #1097): из представления к таблице уходят, чтобы
 * посмотреть те же счета всеми колонками, — отбор, оставленный позади, пришлось бы набирать заново.
 * Колонки, итоги и сортировка не переносятся: они — настройка представления.
 */
export function wholeTableHash(view: TableView): string {
  return viewHash({ ...DEFAULT_VIEW, filter: view.filter, brokenFilter: view.brokenFilter });
}

/** «Все колонки таблицы» — словом в адресе: у готового представления пустой список значил бы «как в нём». */
const ALL_COLUMNS = '*';

/**
 * Состояние из фрагмента адреса. Чего в адресе нет — то как в `base`: у таблицы это умолчания, у
 * готового представления — его настройка.
 *
 * ⚠️ Параметр, который в адресе ЕСТЬ, но пуст (`sort=`), — не «как в основе», а «снято»: иначе
 * сортировку готового представления нельзя было бы убрать — адрес без неё читался бы как адрес с ней.
 */
export function parseView(hash: string, base: TableView = DEFAULT_VIEW): TableView {
  const params = new URLSearchParams(hash.replace(/^#/, ''));
  const view: TableView = { ...base };

  if (params.get('columns')?.trim() === ALL_COLUMNS) view.columns = null;
  else {
    const columns = unique(list(params.get('columns')));
    if (columns.length > 0) view.columns = columns;
  }

  const filter = params.get('filter');
  if (filter?.trim()) {
    const node = parseFilter(filter);
    if (node) view.filter = node;
    else view.brokenFilter = filter;
  }

  if (params.has('sort'))
    view.sort = list(params.get('sort')).map(pair).map(([column, word]) => ({ column, descending: word === 'desc' }));

  // Итог с незнакомым словом в адрес попадает только правкой адреса руками; считать по нему нечего,
  // и колонки, которая осталась бы без итога, он не называет — пропускаем.
  if (params.has('totals'))
    view.totals = list(params.get('totals')).map(pair)
      .filter((p): p is [string, Aggregate] => (AGGREGATE_NAMES as readonly string[]).includes(p[1]))
      .map(([column, aggregate]) => ({ column, aggregate }));

  view.pinned = whole(params.get('pin'), 0, base.pinned);
  view.page = whole(params.get('page'), 1, 1);
  const size = whole(params.get('size'), 1, TABLE_PAGE);
  view.size = PAGE_SIZES.includes(size) ? size : TABLE_PAGE;
  view.row = params.get('row')?.trim() || null;
  return view;
}

/**
 * Фрагмент адреса из состояния: с `#`, либо пусто, если всё как в `base`. В адрес идёт только то,
 * чем состояние от основы ОТЛИЧАЕТСЯ: у таблицы — от умолчаний, у готового представления — от его
 * настройки. Так адрес «Реестра счетов» остаётся коротким, пока человек его не менял, и правка
 * представления модулем доезжает до всех, кто своего не настраивал.
 */
export function viewHash(view: TableView, base: TableView = DEFAULT_VIEW): string {
  const parts: [string, string][] = [];
  const columns = view.columns?.join(',') ?? '';
  if (columns !== (base.columns?.join(',') ?? '')) parts.push(['columns', columns || ALL_COLUMNS]);

  const filter = view.filter ? JSON.stringify(view.filter) : view.brokenFilter;
  if (filter) parts.push(['filter', filter]);

  const sortText = (sort: TableSort[]) => sort.map(s => `${s.column}:${s.descending ? 'desc' : 'asc'}`).join(',');
  const totalsText = (totals: ColumnTotal[]) => totals.map(t => `${t.column}:${t.aggregate}`).join(',');
  if (sortText(view.sort) !== sortText(base.sort)) parts.push(['sort', sortText(view.sort)]);
  if (totalsText(view.totals) !== totalsText(base.totals)) parts.push(['totals', totalsText(view.totals)]);
  if (view.pinned !== base.pinned) parts.push(['pin', String(view.pinned)]);
  if (view.page > 1) parts.push(['page', String(view.page)]);
  if (view.size !== TABLE_PAGE) parts.push(['size', String(view.size)]);
  if (view.row) parts.push(['row', view.row]);

  // Запятая и двоеточие — разделители списков, и в адресе они читаются; остальное кодируется.
  const text = parts
    .map(([key, value]) => `${key}=${encodeURIComponent(value).replace(/%2C/g, ',').replace(/%3A/g, ':')}`)
    .join('&');
  return text ? `#${text}` : '';
}

/**
 * Сменился ли отбор. От этого зависит, как состояние ложится в историю браузера: новый отбор —
 * новая запись, всё остальное (сортировка, колонки, страница, открытая строка) — замена текущей.
 * Так «назад» возвращает ПРЕДЫДУЩИЙ ОТБОР за один шаг, а не перебирает щелчки по шапке.
 */
export function filterChanged(from: TableView, to: TableView): boolean {
  return JSON.stringify(from.filter) !== JSON.stringify(to.filter) || from.brokenFilter !== to.brokenFilter;
}

/** Изменение состояния: из того, что стоит в адресе СЕЙЧАС, — что должно стоять. */
export type ViewChange = (current: TableView) => TableView;

/**
 * Куда и как записать изменение: новый фрагмент и «заменить ли текущую запись истории»; null —
 * менять нечего.
 *
 * ⚠️ Изменение применяется к состоянию из АДРЕСА на момент действия, а не к состоянию, с которым
 * экран был нарисован. Адрес меняется сразу, а перерисовка под него приходит позже (маршрутизатор
 * отдаёт её переходом), и в этом просвете экран ещё показывает прежнее. Действие, посчитанное от
 * нарисованного, записывало в адрес прежнее состояние с одной своей правкой — и молча стирало
 * предыдущую: щелчок по шапке сразу после добавления условия снимал это условие, причём заменой
 * записи истории, так что и «назад» его не возвращал. Ловил это живой прогон, через раз.
 */
export function addressChange(
  hash: string, base: TableView, change: ViewChange,
): { hash: string; replace: boolean } | null {
  const current = parseView(hash, base);
  const next = change(current);
  const target = viewHash(next, base);
  if (target === viewHash(current, base)) return null;
  return { hash: target, replace: !filterChanged(current, next) };
}

// ── Изменения состояния ──────────────────────────────────────────────────────────────────────────

/** Новый отбор: страница — первая, открытая строка закрывается (под новым отбором её может не быть). */
export function withFilter(view: TableView, filter: FilterNode | null): TableView {
  return { ...view, filter, brokenFilter: null, page: 1, row: null };
}

/**
 * Правка отбора чипами — изменением от отбора, который стоит сейчас (`FilterChange`). Изменение,
 * которому менять нечего, не трогает ничего: иначе чип, снятый дважды, сбрасывал бы страницу и
 * закрывал открытую строку под тем же самым отбором.
 */
export function withFilterChange(view: TableView, change: FilterChange): TableView {
  const filter = change(view.filter);
  return filter === view.filter && view.brokenFilter === null ? view : withFilter(view, filter);
}

/**
 * Щелчок по шапке: по возрастанию → по убыванию → без сортировки. Обычный щелчок оставляет одну
 * колонку; с `additive` колонка добавляется к уже стоящим — следующим по важности ключом.
 */
export function withSort(view: TableView, column: string, additive: boolean): TableView {
  const current = view.sort.find(s => s.column === column);
  const next: TableSort | null = !current ? { column, descending: false }
    : current.descending ? null : { column, descending: true };

  const sort = !additive ? (next ? [next] : [])
    : current ? view.sort.flatMap(s => (s.column !== column ? [s] : next ? [next] : []))
      : [...view.sort, next!];
  return { ...view, sort, page: 1 };
}

/**
 * Показать или убрать колонку. `all` — колонки таблицы в её порядке; `at` — на какое место среди
 * показанных колонка встаёт (нет — в конец).
 */
export function withColumnShown(
  view: TableView, all: string[], column: string, shown: boolean, at?: number,
): TableView {
  const current = view.columns ?? all;
  if (shown && current.includes(column)) return view;
  const columns = shown
    ? [...current.slice(0, at ?? current.length), column, ...current.slice(at ?? current.length)]
    : current.filter(c => c !== column);
  return settled({ ...view, columns });
}

/**
 * Вернуть колонку из окошка выбора: она встаёт туда, где стоит в списке, — после показанных колонок,
 * что в списке выше неё (`above`). Сколько из них показано, считается по состоянию, к которому
 * изменение применяется: число, посчитанное при отрисовке списка, после только что снятой галочки
 * уже на единицу больше, и колонка вставала бы правее своего места.
 */
export function withColumnReturned(view: TableView, all: string[], column: string, above: string[]): TableView {
  const current = view.columns ?? all;
  return withColumnShown(view, all, column, true, above.filter(key => current.includes(key)).length);
}

/**
 * Порядок строк в окошке выбора колонок. `snapshot` — порядок на момент, когда окошко открыли:
 * показанные колонки, за ними остальные.
 *
 * Строка не прыгает из-под руки: снятая галочка оставляет колонку на её месте в списке, а не
 * уносит в конец. Места показанных колонок при этом заполняются ими же в ТЕКУЩЕМ порядке — так
 * перестановка «левее / правее» в списке видна сразу.
 */
export function chooserOrder(snapshot: string[], shown: string[]): string[] {
  const visible = new Set(shown);
  let next = 0;
  return snapshot.map(key => (visible.has(key) ? shown[next++] : key));
}

/** Сдвинуть колонку на место левее (`-1`) или правее (`+1`). */
export function withColumnMoved(view: TableView, all: string[], column: string, delta: -1 | 1): TableView {
  const columns = [...(view.columns ?? all)];
  const from = columns.indexOf(column);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= columns.length) return view;
  [columns[from], columns[to]] = [columns[to], columns[from]];
  return { ...view, columns };
}

/** Итог по колонке; null — итога нет. */
export function withTotal(view: TableView, column: string, aggregate: Aggregate | null): TableView {
  const rest = view.totals.filter(t => t.column !== column);
  return { ...view, totals: aggregate ? [...rest, { column, aggregate }] : rest };
}

export function withPinned(view: TableView, pinned: number): TableView {
  return settled({ ...view, pinned: Math.max(0, pinned) });
}

export function withPage(view: TableView, page: number): TableView {
  return { ...view, page: Math.max(1, page) };
}

/**
 * Шаг на соседнюю страницу — действие над ПОКАЗАННОЙ страницей: «следующая» значит «следующая за
 * той, что на экране». `shown` — состояние, с которым экран нарисован. Отбор, размер страницы или
 * сама страница в адресе уже другие — шаг не делается вовсе: номер, посчитанный от прежней выдачи,
 * под новым отбором указывал бы за её конец, на пустую страницу.
 */
export function withPageStep(view: TableView, shown: TableView, delta: -1 | 1): TableView {
  if (view.page !== shown.page || view.size !== shown.size || filterChanged(view, shown)) return view;
  return withPage(view, shown.page + delta);
}

export function withSize(view: TableView, size: number): TableView {
  return { ...view, size, page: 1 };
}

export function withRow(view: TableView, row: string | null): TableView {
  return { ...view, row };
}

/**
 * Колонки, итоги и закрепление — как в основе: у таблицы — её умолчания, у готового представления —
 * его настройка. Отбор и сортировка остаются.
 */
export function withColumnsReset(view: TableView, base: TableView = DEFAULT_VIEW): TableView {
  return { ...view, columns: base.columns && [...base.columns], totals: [...base.totals], pinned: base.pinned };
}

/** Отличаются ли колонки, итоги или закрепление от основы — есть ли что возвращать. */
export function columnsCustomised(view: TableView, base: TableView = DEFAULT_VIEW): boolean {
  return JSON.stringify([view.columns, view.totals, view.pinned])
    !== JSON.stringify([base.columns, base.totals, base.pinned]);
}

/**
 * Убранная колонка уносит свой итог — показать его было бы негде, а считать сервер продолжал бы; и
 * закрепить можно не больше колонок, чем показано.
 */
function settled(view: TableView): TableView {
  const columns = view.columns;
  if (!columns) return view;
  return {
    ...view,
    totals: view.totals.filter(t => columns.includes(t.column)),
    pinned: Math.min(view.pinned, columns.length),
  };
}

// ── Разбор ───────────────────────────────────────────────────────────────────────────────────────

function list(value: string | null): string[] {
  return (value ?? '').split(',').map(s => s.trim()).filter(Boolean);
}

function unique(items: string[]): string[] {
  return [...new Set(items)];
}

/** «Итого:desc» → [«Итого», «desc»]; без двоеточия — слово пустое. Делим по ПОСЛЕДНЕМУ, как сервер. */
function pair(item: string): [string, string] {
  const colon = item.lastIndexOf(':');
  return colon > 0 ? [item.slice(0, colon), item.slice(colon + 1)] : [item, ''];
}

function whole(value: string | null, min: number, fallback: number): number {
  const n = Number(value);
  return value !== null && Number.isInteger(n) && n >= min ? n : fallback;
}

function parseFilter(text: string): FilterNode | null {
  try {
    const node: unknown = JSON.parse(text);
    return isNode(node) ? node : null;
  } catch {
    return null;
  }
}

function isNode(node: unknown): node is FilterNode {
  if (typeof node !== 'object' || node === null) return false;
  const n = node as Record<string, unknown>;
  if (n.type === 'condition') return typeof n.column === 'string';
  return n.type === 'group' && Array.isArray(n.children) && n.children.every(isNode);
}
