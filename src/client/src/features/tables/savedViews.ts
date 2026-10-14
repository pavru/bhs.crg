import { TABLE_PAGE, type TableDeclaration, type TablePreset } from '@/shared/api/tables';
import type { SaveTableViewBody, SavedTableView, SavedViewState } from '@/shared/api/tableViews';
import {
  DEFAULT_VIEW, filterText, knownTotals, parseFilter, parseView, presetLookup, presetView, viewHash,
  type PresetLookup, type TableView,
} from './tableViewState';

/**
 * Сохранённые представления на экране таблицы (ТЗ CORE-33; задача G3a, issue #1095) — чистые правила:
 * что стоит под адресом, от чего экран отсчитывает изменения и какие действия человеку показаны.
 *
 * Представление названо в адресе тем же местом, что и представление модуля: код — у модуля,
 * идентификатор — у сохранённого. Модулю код, похожий на идентификатор, запрещён сервером.
 */

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Назван ли в адресе идентификатор сохранённого представления, а не код представления модуля. */
export function isSavedId(code: string | undefined): code is string {
  return !!code && ID.test(code);
}

/**
 * Что стоит под адресом. К ответам `presetLookup` добавлены два:
 *
 * - `saved` — сохранённое представление, своё или общее;
 * - `gone` — идентификатор назван, а в списке его нет: представление удалено либо автор вернул его
 *   в свои. Сервер эти случаи не различает нарочно (один ответ на «нет» и «не ваше»), и экран тоже.
 */
export type ViewLookup = PresetLookup | { state: 'saved'; saved: SavedTableView } | { state: 'gone' };

export function viewLookup(
  code: string | undefined, declaration: Pick<TableDeclaration, 'state' | 'views'> | undefined,
  saved: SavedTableView[] | undefined,
): ViewLookup {
  if (!isSavedId(code)) return presetLookup(code, declaration);
  if (!declaration) return { state: 'pending' };
  // У выключенного модуля сохранённых представлений в списке нет — говорит сама таблица.
  if (declaration.state === 'module-off') return { state: 'off' };
  if (!saved) return { state: 'pending' };
  const found = saved.find(v => v.id.toLowerCase() === code.toLowerCase());
  return found ? { state: 'saved', saved: found } : { state: 'gone' };
}

/** Запрашивать ли строки: под `pending` рано, под `missing` и `gone` нечего. */
export function rowsWantedFor(lookup: ViewLookup): boolean {
  return lookup.state !== 'pending' && lookup.state !== 'missing' && lookup.state !== 'gone';
}

/**
 * Настройка сохранённого представления — состоянием экрана: от неё отсчитан адрес. В отличие от
 * представления модуля, отбор здесь — часть настройки.
 *
 * Отбор, который деревом условий не разобрался, не выбрасывается — он уезжает серверу как есть, и
 * отказывает сервер: иначе под названием представления показались бы ВСЕ строки.
 */
export function savedView(state: SavedViewState): TableView {
  const text = state.filter?.trim() ? state.filter : null;
  const filter = text ? parseFilter(text) : null;
  return {
    ...DEFAULT_VIEW,
    columns: state.columns?.length ? [...state.columns] : null,
    filter,
    brokenFilter: text && !filter ? text : null,
    sort: (state.sort ?? []).map(s => ({ column: s.column, descending: s.descending })),
    totals: knownTotals(state.totals ?? []),
    pinned: state.pinned,
  };
}

/** От чего экран отсчитывает адрес: умолчания таблицы, настройка модуля либо сохранённая настройка. */
export function lookupBase(lookup: ViewLookup): TableView {
  if (lookup.state === 'found') return presetView(lookup.preset);
  if (lookup.state === 'saved') return savedView(lookup.saved.state);
  return DEFAULT_VIEW;
}

/** Что уходит на сохранение: колонки, отбор, сортировка, итоги и закрепление — без страницы и строки. */
export function stateOf(view: TableView): SavedViewState {
  return {
    columns: view.columns && [...view.columns],
    filter: filterText(view) || null,
    sort: view.sort.map(s => ({ ...s })),
    totals: view.totals.map(t => ({ ...t })),
    pinned: view.pinned,
  };
}

/**
 * Отошла ли настройка от основы. Страница, размер страницы и открытая строка — не настройка: ими
 * человек ходит по таблице, и «изменено» от перелистывания значило бы, что помечено всегда.
 */
export function viewModified(view: TableView, base: TableView): boolean {
  return viewHash({ ...view, page: 1, size: TABLE_PAGE, row: null }, base) !== '';
}

/** Настройка — как в основе; размер страницы остаётся, страница — первая, открытая строка закрыта. */
export function withViewReset(view: TableView, base: TableView): TableView {
  return { ...base, size: view.size, page: 1, row: null };
}

/** Фрагмент адреса, в котором от настройки ничего не осталось, — только где человек стоит. */
export function placeHash(view: TableView): string {
  return viewHash({ ...DEFAULT_VIEW, page: view.page, size: view.size, row: view.row });
}

/**
 * Адрес после «Сохранить»: тот же экран, отсчитанный от НОВОЙ основы. Основа стала тем, что
 * сохранено, и прежние отличия от старой читались бы уже от неё.
 *
 * ⚠️ Считается от адреса на момент ОТВЕТА (`hash`), а не от состояния на момент нажатия: правка,
 * сделанная, пока запрос шёл, в сохранённое не попала — и обязана остаться на экране помеченной, а
 * не исчезнуть вместе с отличиями (ревью PR #1324).
 */
export function rebasedHash(hash: string, before: TableView, stored: SavedViewState): string {
  return viewHash(parseView(hash, before), savedView(stored));
}

/**
 * Название, которое экран предлагает копии. Не длиннее предела сервера — иначе отказ пришёл бы на
 * значение, предложенное самим экраном; и без второго хвоста у копии своей же копии.
 */
export function copyTitle(title: string): string {
  const tail = ' — моё';
  return title.endsWith(tail) ? title : title.slice(0, TITLE_MAX - tail.length).trimEnd() + tail;
}

/** Предел длины названия — тот же, что у сервера (`SavedTableView.TitleMax`). */
export const TITLE_MAX = 120;

/**
 * Какие действия сохранения показаны под изменённой настройкой. Невыполнимое не показывается вовсе
 * (решение владельца, 11.10.2026): у представления модуля и чужого общего «Сохранить» нет — только
 * копия.
 *
 * - `save` — заменить лежащее: только своё либо общее с правом на общие. ⚠️ И не под скрытым
 *   отбором автора (`withheld`): экран его не знает, а правка заменяет настройку целиком.
 * - `saveAs` — завести своё. Отбор, который не разобрался, сохранить нельзя: сервер его не примет.
 */
export function saveActions(view: TableView, lookup: ViewLookup): { save: boolean; saveAs: boolean } {
  const sound = view.brokenFilter === null;
  const saved = lookup.state === 'saved' ? lookup.saved : null;
  return { save: sound && !!saved?.canEdit && !saved.withheld, saveAs: sound };
}

/**
 * Действия над представлением в списке. «Вернуть в свои» — только автору: держатель права чужое
 * общее правит и удаляет, но себе не забирает. Под скрытым отбором остаётся одно удаление — всякая
 * правка названия или признака «общее» заменила бы настройку той, что отдана читателю, без отбора.
 */
export function listActions(saved: SavedTableView, canShare: boolean) {
  const whole = saved.canEdit && !saved.withheld;
  return {
    rename: whole,
    share: whole && !saved.shared && saved.mine && canShare,
    unshare: whole && saved.shared && saved.mine,
    remove: saved.canEdit,
  };
}

/** Тело правки, которая настройку НЕ трогает: переименование и смена «общее». */
export function keeping(saved: SavedTableView, change: Partial<Pick<SaveTableViewBody, 'title' | 'shared'>>): SaveTableViewBody {
  return { title: saved.title, shared: saved.shared, state: saved.state, basedOn: saved.basedOn, ...change };
}

/** Из чего сделано новое представление: код представления модуля либо то, из чего сделано открытое. */
export function basedOnOf(lookup: ViewLookup): string | null {
  if (lookup.state === 'found') return lookup.preset.code;
  return lookup.state === 'saved' ? lookup.saved.basedOn : null;
}

/** Группы списка представлений: готовые (модуля), свои и общие. Своё общее стоит в общих. */
export function viewGroups(presets: TablePreset[] | undefined, saved: SavedTableView[] | undefined) {
  const list = saved ?? [];
  return { ready: presets ?? [], own: list.filter(v => !v.shared), shared: list.filter(v => v.shared) };
}

/** Название того, что открыто: представление модуля, сохранённое либо сама таблица. */
export function lookupTitle(lookup: ViewLookup, tableTitle: string): string {
  if (lookup.state === 'found') return lookup.preset.title;
  return lookup.state === 'saved' ? lookup.saved.title : tableTitle;
}
