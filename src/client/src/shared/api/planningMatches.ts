import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { NomenclatureCandidates } from './nomenclatureCandidates';
import type { NomenclatureSearchResult } from './invoices';
import type { EstimateRow, HiddenPart, PlanMark } from './planningEstimates';
import type { CommonDataEntry } from './types';
import { useListDocumentTypes } from './documentTypes';

/**
 * Сопоставление строк смет с видами работ и материалов с номенклатурой (change `plan-09-estimate-ui`, ТЗ PLAN-6.1,
 * PLAN-9.1) и списки запомненного.
 *
 * ⚠️ Версию называет РЕДАКЦИЯ заголовком `If-Match`, и каждое сопоставление её сдвигает. Окно, которое сопоставляет
 * раздел за разделом, держит версию из ответа, а не из кэша сметы: перечитанная смета догнала бы её позже
 * следующего нажатия, и своя же правка вернулась бы отказом «редакцию тем временем изменили».
 */

/** Чем сопоставили: обе единицы и коэффициент между ними — ошибка «в сто раз» видна сразу. */
export interface MatchUnits {
  sourceUnit: string | null;
  unit: string | null;
  factor: number | null;
}

/** Что запомнилось. `failed` — запомнить НЕ удалось, а сопоставление записано. */
export interface MatchMemory {
  remembered: number;
  replaced: number;
  failed: boolean;
}

/** Строка, которую массовое сопоставление не тронуло: `alreadyMatched`, `differentUnit`, `material`. */
export interface SkippedRow {
  rowId: string;
  order: number;
  reason: string;
  text: string;
}

export interface RowMatchResult {
  row: EstimateRow;
  version: string;
  units: MatchUnits;
  memory: MatchMemory;
  hidden?: HiddenPart[];
}

export interface LinesMatchResult {
  matched: number;
  skipped: SkippedRow[];
  version: string;
  units: MatchUnits;
  memory: MatchMemory;
}

/** Выбор сметчика: вид работы, единица (не названа — единица вида работы) и коэффициент (не назван — предложенный). */
export interface RateChoice {
  workTypeId: string;
  unitId?: string | null;
  factor?: number | null;
}

/** Вид работы на выбор: код, наименование и единица классификатора — из данных записи. */
export interface WorkTypeChoice {
  id: string;
  name: string;
  code: string | null;
  unitId: string | null;
  unitName: string | null;
}

/** Ссылка на запись в данных записи — как её кладёт форма. */
interface RecordRef { entryId?: string; displayName?: string }

export function workTypeChoice(entry: CommonDataEntry): WorkTypeChoice {
  const unit = entry.data['ЕдиницаИзмерения'] as RecordRef | undefined;
  const code = entry.data['Код'];
  return {
    id: entry.id,
    name: entry.displayName,
    code: typeof code === 'string' && code.trim() ? code.trim() : null,
    unitId: unit?.entryId ?? null,
    unitName: unit?.displayName ?? null,
  };
}

const WORK_TYPES = ['planning', 'work-type-choices'] as const;

/** Виды работ НА ВЫБОР (архивных нет) — классификатор целиком: видов десятки, не тысячи. */
export function useWorkTypeChoices(enabled = true) {
  return useQuery({
    queryKey: WORK_TYPES,
    queryFn: () => apiClient.get<CommonDataEntry[]>('/work-types', { params: { purpose: 'choice' } })
      .then(r => r.data.map(workTypeChoice)),
    enabled,
    staleTime: 60_000,
  });
}

export interface UnitFactor {
  factor: number | null;
  sourceUnit: string | null;
  unitId: string;
  unit: string;
  /** Почему коэффициента нет; `null` — он есть. */
  reason: string | null;
}

/** Коэффициент из единицы расценки в единицу классификатора — тем же правилом, что у сопоставления, без записи. */
export function useUnitFactor(sourceUnit: string | null, unitId: string | null) {
  return useQuery({
    queryKey: ['planning', 'unit-factor', sourceUnit, unitId],
    queryFn: () => apiClient.get<UnitFactor>('/plan/unit-factor', { params: { sourceUnit: sourceUnit ?? undefined, unitId } })
      .then(r => r.data),
    enabled: unitId !== null,
    staleTime: 5 * 60_000,
  });
}

/** Код справочника единиц измерения — зеркало серверного `CoreRecordTypes.UnitCode`. */
export const UNIT_TYPE_CODE = 'ЕдиницаИзмерения';

/**
 * Единицы измерения на выбор — для нового вида работы. Справочник находится по коду типа: идентификатор у
 * каждой установки свой. Типы — общим запросом приложения (`useListDocumentTypes`), а не своим: тот же список
 * уже прочитан и сбрасывается правкой типов. Справочника нет — пустой список, и шаг говорит, что выбрать не из чего.
 */
export function useUnitChoices(enabled: boolean) {
  const types = useListDocumentTypes();
  const unitTypeId = types.data ? types.data.find(t => t.code === UNIT_TYPE_CODE)?.id ?? null : undefined;
  const entries = useQuery({
    queryKey: ['planning', 'unit-choices', unitTypeId],
    queryFn: () => apiClient.get<CommonDataEntry[]>('/common-data', { params: { purpose: 'choice', typeId: unitTypeId } })
      .then(r => r.data.map(e => ({ id: e.id, name: e.displayName })).sort((a, b) => a.name.localeCompare(b.name, 'ru'))),
    enabled: enabled && !!unitTypeId,
    staleTime: 60_000,
  });
  if (types.isError) return { data: undefined, isError: true as const, error: types.error };
  // Типы прочитаны, справочника единиц среди них нет — выбирать не из чего.
  if (unitTypeId === null) return { data: [] as { id: string; name: string }[], isError: false as const, error: null };
  return { data: entries.data, isError: entries.isError, error: entries.error };
}

/** Новый вид работы из окна сопоставления (CORE-8, WORK-59): своя дверь классификатора, право `core.worktypes.edit`. */
export interface NewWorkType {
  code: string;
  name: string;
  unit: { id: string; name: string };
}

export function useCreateWorkType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (w: NewWorkType) => apiClient.post<CommonDataEntry>('/work-types', {
      displayName: `${w.code} ${w.name}`.trim(),
      data: JSON.stringify({
        Код: w.code,
        Наименование: w.name,
        ЕдиницаИзмерения: { $ref: 'catalog', entryId: w.unit.id, displayName: w.unit.name },
      }),
    }).then(r => workTypeChoice(r.data)),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: WORK_TYPES });
      void qc.invalidateQueries({ queryKey: ['planning', 'work-type-names'] });
    },
  });
}

/**
 * После сопоставления перечитываются строки и разделы редакции и сметы (счётчики «не сопоставлено» у редакции).
 * Версию для следующей правки окно берёт из ответа — см. описание файла.
 */
function useRevisionChanged() {
  const qc = useQueryClient();
  return () => Promise.all([
    qc.invalidateQueries({ queryKey: ['planning', 'revisions'] }),
    qc.invalidateQueries({ queryKey: ['planning', 'estimates'] }),
  ]);
}

const ifMatch = (version: string) => ({ headers: { 'If-Match': version } });

/** Строка одна: выбор вида работы либо `clear` — снять сопоставление (так отменяют и подстановку запомненного). */
export function useMatchRow(revisionId: string) {
  const changed = useRevisionChanged();
  return useMutation({
    mutationFn: ({ lineId, version, choice, remember }: {
      lineId: string; version: string; choice: RateChoice | null; remember: boolean;
    }) => apiClient.put<RowMatchResult>(`/plan/revisions/${revisionId}/lines/${lineId}/match`,
      choice ? { ...choice, remember } : { clear: true }, ifMatch(version)).then(r => r.data),
    onSettled: changed,
  });
}

/** Выбранные строки одним видом работы (D9 `plan-31`): одна фиксация, пропущенные — с причиной. */
export function useMatchLines(revisionId: string) {
  const changed = useRevisionChanged();
  return useMutation({
    mutationFn: ({ lineIds, version, choice, sourceUnit, remember }: {
      lineIds: string[]; version: string; choice: RateChoice; sourceUnit: string | null; remember: boolean;
    }) => apiClient.post<LinesMatchResult>(`/plan/revisions/${revisionId}/lines/match`,
      { lineIds, ...choice, sourceUnit, remember }, ifMatch(version)).then(r => r.data),
    onSettled: changed,
  });
}

/** Материал файла во всех строках редакции (D10 `plan-31`): одно решение на все его ресурсы. */
export interface MaterialGroup {
  key: string;
  sourceName: string;
  sourceCode: string | null;
  sourceUnit: string | null;
  rows: number;
  quantity: number;
  nomenclatureId: string | null;
  /** `unmatched` — есть ресурсы без позиции; `mixed` — позиции у ресурсов разные. */
  marks: PlanMark[];
}

export function useRevisionMaterials(revisionId: string) {
  return useQuery({
    queryKey: ['planning', 'revisions', revisionId, 'materials'],
    queryFn: () => apiClient.get<{ items: MaterialGroup[] }>(`/plan/revisions/${revisionId}/materials`).then(r => r.data.items),
  });
}

export interface MaterialGroupMatchResult {
  resources: number;
  matched: number;
  version: string;
  memory: MatchMemory;
}

export function useMatchMaterialGroup(revisionId: string) {
  const changed = useRevisionChanged();
  return useMutation({
    mutationFn: ({ key, nomenclatureId, version, remember }: {
      key: string; nomenclatureId: string; version: string; remember: boolean;
    }) => apiClient.put<MaterialGroupMatchResult>(`/plan/revisions/${revisionId}/materials/match`,
      { key, nomenclatureId, remember }, ifMatch(version)).then(r => r.data),
    onSettled: changed,
  });
}

/**
 * Похожие позиции для ресурса — того же вида ответ, что у строки счёта: окно выбора позиции общее. Спрашивается
 * при открытии материала, а не на каждую букву поиска.
 */
export function useResourceCandidates(revisionId: string, resourceId: string | null) {
  return useQuery({
    queryKey: ['planning', 'resource-candidates', revisionId, resourceId],
    queryFn: ({ signal }) => apiClient
      .get<NomenclatureCandidates>(`/plan/revisions/${revisionId}/resources/${resourceId}/candidates`, { signal })
      .then(r => r.data),
    enabled: resourceId !== null,
    staleTime: 5 * 60_000,
    retry: false,
  });
}

/** Поиск по номенклатуре для окна материала — поиск ядра, как у счетов, своим адресом модуля смет. */
export function usePlanNomenclature(query: string) {
  return useQuery({
    queryKey: ['planning', 'nomenclature', query] as const,
    queryFn: () => apiClient.get<NomenclatureSearchResult>('/plan/nomenclature', { params: { query } })
      .then(r => r.data),
    enabled: query !== '',
    // Прошлый ответ стоит, пока едет новый: иначе список мигал бы пустотой на каждую букву.
    placeholderData: keepPreviousData,
  });
}

/** Почему запомненное НЕ подставится: `lost` — цели нет, `archived` — в архиве. */
export type MatchIssue = 'lost' | 'archived' | null;

export interface RateMatch {
  id: string;
  version: string;
  rateCode: string;
  normativeBase: string | null;
  workTypeId: string;
  workTypeName: string | null;
  unitId: string;
  unitName: string | null;
  factor: number;
  sourceUnit: string | null;
  issue: MatchIssue;
  updatedAt: string;
  updatedBy: string | null;
}

export interface ResourceMatch {
  id: string;
  version: string;
  /** `code` — узнаётся по коду ресурса, `name` — по наименованию. */
  kind: 'code' | 'name';
  sourceText: string;
  nomenclatureId: string;
  nomenclatureName: string | null;
  issue: MatchIssue;
  updatedAt: string;
  updatedBy: string | null;
}

interface MatchPage<T> {
  items: T[];
  /** Сколько под отбором ВСЕГО: без числа порция читалась бы как весь список. */
  total: number;
}

/** Порция списка запомненного; сервер больше двухсот за раз не отдаёт. */
export const MATCH_LIST_PAGE = 50;

export type MatchListKind = 'rate' | 'resource';
const LIST_PATH: Record<MatchListKind, string> = { rate: '/plan/rate-matches', resource: '/plan/resource-matches' };
const LISTS = ['planning', 'match-lists'] as const;

/** Список запомненного порциями: «Показать ещё» дочитывает следующую, а не перечитывает всё. */
export function useMatchList<T extends RateMatch | ResourceMatch>(kind: MatchListKind, query: string) {
  return useInfiniteQuery({
    queryKey: [...LISTS, kind, query],
    initialPageParam: 0,
    placeholderData: keepPreviousData,
    queryFn: ({ pageParam }) => apiClient.get<MatchPage<T>>(LIST_PATH[kind], {
      params: { query: query.trim() || undefined, skip: pageParam, take: MATCH_LIST_PAGE },
    }).then(r => r.data),
    getNextPageParam: (last, pages) => {
      const read = pages.reduce((sum, page) => sum + page.items.length, 0);
      return last.items.length > 0 && read < last.total ? read : undefined;
    },
  });
}

/**
 * Список перечитывается ДО того, как правка объявит себя законченной: строка несёт версию записи, и следующее
 * действие над ней со старой версией получило бы отказ в ответ на собственную правку. Строки смет — тоже: их
 * пометка «запомнено» показывает состояние запомненного.
 */
function useListsChanged() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: ['planning', 'revisions'] });
    return qc.invalidateQueries({ queryKey: LISTS });
  };
}

/** Перенацелить запомненный шифр: другой вид работы, единица или коэффициент. */
export function usePointRateMatch() {
  const changed = useListsChanged();
  return useMutation({
    mutationFn: ({ item, choice }: { item: RateMatch; choice: RateChoice }) =>
      apiClient.put<RateMatch>(`${LIST_PATH.rate}/${item.id}`, choice, ifMatch(item.version)).then(r => r.data),
    onSettled: changed,
  });
}

/** Перенацелить запомненный ресурс на другую позицию номенклатуры. */
export function usePointResourceMatch() {
  const changed = useListsChanged();
  return useMutation({
    mutationFn: ({ item, nomenclatureId }: { item: ResourceMatch; nomenclatureId: string }) =>
      apiClient.put<ResourceMatch>(`${LIST_PATH.resource}/${item.id}`, { nomenclatureId }, ifMatch(item.version))
        .then(r => r.data),
    onSettled: changed,
  });
}

/** Забыть запомненное. Строки, которые оно уже сопоставило, остаются — их пометка скажет «с тех пор забыли». */
export function useForgetMatch(kind: MatchListKind) {
  const changed = useListsChanged();
  return useMutation({
    mutationFn: (item: { id: string; version: string }) =>
      apiClient.delete(`${LIST_PATH[kind]}/${item.id}`, ifMatch(item.version)),
    onSettled: changed,
  });
}
