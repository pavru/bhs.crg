import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { isNotFound, seenBy } from './invoices';
import { onWritten, writeMeta, type WriteContext } from './writeWatch';

/**
 * Расходные накладные модуля «Счета и накладные» (задача D1 этапа 2, issue #1083; ТЗ COST-5, COST-17).
 *
 * ⚠️ Денег здесь нет — ни в строке, ни в перечне отпущенного: право на накладные счетов не открывает
 * (COST-29). Поле с суммой в этих типах означало бы, что сервер начал её отдавать.
 */
export type WaybillState = 'Draft' | 'Posted';

export interface WaybillLineView {
  id: string;
  ordinal: number;
  nomenclatureId: string | null;
  nomenclatureName: string | null;
  /** Ссылка есть, а позиции в справочнике нет. Считает сервер. */
  nomenclatureLost: boolean;
  /** Позиция в архиве (issue #1185): строка сопоставлена, в поиске этой позиции больше нет. */
  nomenclatureArchived?: boolean;
  sourceText: string | null;
  unit: string | null;
  quantity: number | null;
  note: string | null;
}

export interface WaybillView {
  id: string;
  /**
   * Версия накладной. Её называет КАЖДАЯ правка — заголовком `If-Match`, как у счёта: форма, открытая
   * давно, иначе записалась бы поверх чужой правки, а «Провести» провело бы строки, которых на экране
   * не было.
   */
  version: string;
  number: string | null;
  issuedOn: string | null;
  warehouse: string | null;
  constructionId: string | null;
  constructionName: string | null;
  /** Стройка названа, а её больше нет. */
  constructionLost: boolean;
  receivedBy: string | null;
  note: string | null;
  state: WaybillState;
  postedAt: string | null;
  lines: WaybillLineView[];
  /** `unmatched` — строки, которые в «материалы на объекте» не попадают. */
  totals: { count: number; unmatched: number };
  /**
   * Учётная дата — есть только у проведённой. Отличается от даты отпуска, когда накладную провели
   * после закрытия периода её даты: тогда она учтена в первом открытом дне.
   */
  accountingOn?: string | null;
  /** Чем накладная заперта, словами сервера; пусто — не заперта. Запертую не возвращают в черновик и не сопоставляют. */
  locked?: string | null;
}

export interface WaybillListItem {
  id: string;
  number: string | null;
  issuedOn: string | null;
  warehouse: string | null;
  constructionId: string | null;
  constructionName: string | null;
  state: WaybillState;
  lines: number;
  unmatched: number;
}

/** Список и честное «есть ещё»: поиск на экране идёт по загруженному. */
export interface WaybillList {
  items: WaybillListItem[];
  more: boolean;
  /**
   * Сколько накладных скрыл перечень `sites` (issue #1222) — по всему отбору, а не по странице. Без
   * перечня — `null`: «не отбирали» и «ничего не скрыто» — разные ответы.
   */
  hidden?: number | null;
}

export interface WaybillHeader {
  number: string | null;
  issuedOn: string | null;
  warehouse: string | null;
  construction: string | null;
  receivedBy: string | null;
  note: string | null;
}

export interface IssuedMaterial {
  nomenclatureId: string;
  name: string | null;
  unit: string | null;
  quantity: number;
  first: string;
  last: string;
  waybills: number;
}

/** Перечень и оговорка к нему приходят ОДНИМ ответом: перечень без счётчика читался бы как полный. */
export interface IssuedMaterials {
  constructionId: string;
  items: IssuedMaterial[];
  unmatchedLines: number;
  unmatchedWaybills: number;
}

const QK = 'costs-waybills';
// ⚠️ Ключ СПИСКА, а не общий префикс: `[QK]` совпал бы и с карточкой, и только что полученная
// накладная тут же перезапрашивалась бы после каждой правки (ревью PR #1206).
const LIST_KEY = [QK, 'list'] as const;
const MATERIALS_KEY = ['costs-materials'] as const;
/** Ключ открытой накладной в кэше: под ним запись ищет вид ДО себя. */
export const waybillKey = (id: string | undefined) => [QK, 'one', id] as const;

/**
 * Список накладных. `sites` — избранные стройки, по которым отбирает СЕРВЕР (issue #1222): список
 * страничный, и урезанный здесь он показал бы три накладные из трёхсот. `null` — не отбирать.
 *
 * <p>`enabled: false`, пока фильтр не известен: первый запрос без перечня принёс бы «всё», и список
 * мигнул бы полным перед урезанным.</p>
 *
 * <p>Переключение фильтра держит прежний список, пока не пришёл новый (`placeholderData`): иначе список
 * пустел бы до «Загрузки» на каждое «Все / Избранные». Смена очереди («Сопоставить») — нет: прежний
 * список под другой очередью читался бы как её ответ.</p>
 */
export function useWaybills(unmatched: boolean, sites: readonly string[] | null, enabled = true) {
  const key = sites ? [...sites].sort().join(',') : null;
  return useQuery({
    queryKey: [...LIST_KEY, { unmatched, sites: key }] as const,
    queryFn: () => apiClient
      .get<WaybillList>('/costs/waybills', {
        params: { ...(unmatched ? { unmatched: true } : {}), ...(key ? { sites: key } : {}) },
      })
      .then(r => r.data),
    enabled,
    placeholderData: (prev, prevQuery) => (prevQuery?.queryKey[2].unmatched === unmatched ? prev : undefined),
  });
}

export function useWaybill(id: string | undefined) {
  return useQuery({
    queryKey: waybillKey(id),
    queryFn: () => apiClient.get<WaybillView>(`/costs/waybills/${id}`).then(r => r.data),
    enabled: !!id,
    // «Нет такой» повтором не лечится (ревью PR #1299): общий повтор держал бы «загружается…» лишнюю
    // секунду перед «накладной нет» — и так при каждом возврате в окно.
    retry: (failures, error) => !isNotFound(error) && failures < 1,
    // Открытая форма читает накладную заново, а не из кэша: «заперта» в её виде зависит от границ
    // периодов, а те меняют на другом экране. Человек, которому форма сказала «сначала отмените закрытие»,
    // отменяет его и возвращается — и прежний кэш ещё полминуты показывал бы замок (ревью PR #1296).
    // Ключ периодов накладные не сбрасывает нарочно: ядро о кэше модуля не знает.
    refetchOnMount: 'always',
  });
}

/** Прочитать накладную заново, мимо кэша: после отказа 409 только свежий вид говорит, что с ней стало. */
export function useFreshWaybill() {
  const qc = useQueryClient();
  return (id: string) => qc.fetchQuery({
    queryKey: waybillKey(id),
    queryFn: () => apiClient.get<WaybillView>(`/costs/waybills/${id}`).then(r => r.data),
    staleTime: 0,
  });
}

/**
 * Правка накладной, возвращающая её вид: ответ кладётся в кэш, списки перечитываются, отказ 409
 * перечитывает накладную — как у счёта (`useInvoiceWrite`). 409 значит «на экране не то, что в базе»:
 * накладную изменили либо её запер закрытый период. Без перечитывания форма оставалась прежней — с
 * кнопкой, которая откажет снова, и без причины замка (ревью PR #1296).
 */
function useWaybillWrite<T extends { id: string }>(send: (input: T) => Promise<WaybillView>) {
  const settle = useSettle();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: send,
    meta: writeMeta(WAYBILL_WRITE),
    // Вид до записи — в контекст мутации: по нему подписчик узнаёт, что изменила именно эта запись.
    onMutate: (input): WriteContext<WaybillView> => ({ before: qc.getQueryData<WaybillView>(waybillKey(input.id)) }),
    onSuccess: settle,
    onError: (error: unknown, input: T) => {
      // 404 — накладную удалили, пока она была открыта (issue #1283): перечитывание ответит тем же, и
      // страница скажет «накладной больше нет», оставив набранное на экране.
      const status = (error as { response?: { status?: number } })?.response?.status;
      if (status === 409 || status === 404) void qc.invalidateQueries({ queryKey: waybillKey(input.id) });
    },
  });
}

/** Признак записи накладной в `meta` мутации — по нему её узнаёт `onWaybillWritten`. */
const WAYBILL_WRITE = 'waybillWrite';

/**
 * Подписка на СВОИ удавшиеся записи накладной: прежний вид и новый (issue #1222 — получатель сменился
 * на неизбранную стройку, и накладная уходит из списка). Тот же приём, что `onInvoiceWritten`: через кэш
 * мутаций, и чужая правка, пришедшая перечитыванием, сюда не попадает.
 */
export function onWaybillWritten(
  qc: QueryClient, listener: (before: WaybillView | undefined, after: WaybillView) => void,
): () => void {
  return onWritten(qc, WAYBILL_WRITE, listener);
}

/** Что считать устаревшим после любой правки: список, открытую накладную и перечень отпущенного. */
function useSettle() {
  const qc = useQueryClient();
  return (view: WaybillView) => {
    qc.setQueryData(waybillKey(view.id), view);
    void qc.invalidateQueries({ queryKey: LIST_KEY });
    void qc.invalidateQueries({ queryKey: MATERIALS_KEY });
  };
}

export function useCreateWaybill() {
  const settle = useSettle();
  return useMutation({
    mutationFn: (header: Partial<WaybillHeader>) =>
      apiClient.post<WaybillView>('/costs/waybills', header).then(r => r.data),
    onSuccess: settle,
  });
}

/**
 * Сохранить черновик: шапка и строки ОДНИМ запросом и одним сохранением на сервере. Двумя запросами
 * шапка записывалась бы и тогда, когда строки отказали, — человек оставался бы с половиной сохранённого.
 *
 * `seen` — версия накладной, по которой собрана форма: устаревшей сервер отвечает 409.
 */
export function useSaveWaybill() {
  return useWaybillWrite(({ id, seen, header, lines }: {
    id: string; seen: string; header: WaybillHeader; lines: Record<string, unknown>[];
  }) => apiClient.put<WaybillView>(`/costs/waybills/${id}`, { ...header, lines }, seenBy(seen)).then(r => r.data));
}

/** Провести или вернуть в черновик. */
export function useWaybillState() {
  return useWaybillWrite(({ id, seen, to }: { id: string; seen: string; to: 'posted' | 'draft' }) =>
    apiClient.post<WaybillView>(`/costs/waybills/${id}/${to}`, null, seenBy(seen)).then(r => r.data));
}

/** Сопоставить строку с позицией номенклатуры (`null` — снять). Работает и у проведённой накладной — пока её не запер закрытый период. */
export function useMatchWaybillLine() {
  return useWaybillWrite(({ id, seen, lineId, nomenclatureId }: {
    id: string; seen: string; lineId: string; nomenclatureId: string | null;
  }) =>
    apiClient.put<WaybillView>(`/costs/waybills/${id}/lines/${lineId}/nomenclature`, {
      nomenclature: nomenclatureId ? { $ref: 'catalog', entryId: nomenclatureId } : null,
    }, seenBy(seen)).then(r => r.data));
}

/**
 * Удалить накладную (issue #1283): только черновик, насовсем. Ответа-вида нет, поэтому не
 * `useWaybillWrite`; кэш после успеха чистит `forgetWaybill` — когда форма уже закрыта.
 */
export function useDeleteWaybill() {
  // Перечитывания на 409 здесь нет нарочно (ревью PR #1299): отказ разбирает `deleteWaybill`, и он
  // сам читает накладную заново тем же ключом — второй запрос рядом был бы лишним.
  return useMutation({
    mutationFn: ({ id, seen }: { id: string; seen: string }) =>
      apiClient.delete(`/costs/waybills/${id}`, seenBy(seen)).then(() => undefined),
  });
}

/**
 * Забыть удалённую накладную: выбросить её из кэша и перечитать список. Перечень отпущенного не
 * трогается — черновик в него не входил.
 *
 * ⚠️ `removeQueries`, а не `invalidateQueries`: помеченная устаревшей накладная была бы запрошена
 * заново и ответила бы 404. И звать — ПОСЛЕ закрытия формы: у выброшенного при живом наблюдателе
 * запроса наблюдатель тут же начал бы его заново.
 */
export function forgetWaybill(qc: QueryClient, id: string) {
  qc.removeQueries({ queryKey: waybillKey(id) });
  void qc.invalidateQueries({ queryKey: LIST_KEY });
}

export function useIssuedMaterials(constructionId: string | null) {
  return useQuery({
    queryKey: [...MATERIALS_KEY, constructionId] as const,
    queryFn: () => apiClient
      .get<IssuedMaterials>('/costs/materials', { params: { constructionId } })
      .then(r => r.data),
    enabled: !!constructionId,
  });
}
