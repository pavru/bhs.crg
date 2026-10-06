import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

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
   * Версия накладной. Правка шапки и строк называет её (`ifMatch`): форма, открытая давно, иначе
   * записалась бы поверх чужой правки и молча удалила бы строки, которых на экране не было.
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

export function useWaybills(unmatched = false) {
  return useQuery({
    queryKey: [...LIST_KEY, { unmatched }] as const,
    queryFn: () => apiClient
      .get<WaybillList>('/costs/waybills', { params: unmatched ? { unmatched: true } : {} })
      .then(r => r.data),
  });
}

export function useWaybill(id: string | undefined) {
  return useQuery({
    queryKey: [QK, 'one', id] as const,
    queryFn: () => apiClient.get<WaybillView>(`/costs/waybills/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

/** Что считать устаревшим после любой правки: список, открытую накладную и перечень отпущенного. */
function useSettle() {
  const qc = useQueryClient();
  return (view: WaybillView) => {
    qc.setQueryData([QK, 'one', view.id], view);
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
 * `ifMatch` — версия накладной, по которой собрана форма: устаревшей сервер отвечает 409.
 */
export function useSaveWaybill() {
  const settle = useSettle();
  return useMutation({
    mutationFn: ({ id, ifMatch, header, lines }: {
      id: string; ifMatch: string; header: WaybillHeader; lines: Record<string, unknown>[];
    }) => apiClient.put<WaybillView>(`/costs/waybills/${id}`, { ...header, lines, ifMatch }).then(r => r.data),
    onSuccess: settle,
  });
}

/** Провести или вернуть в черновик. */
export function useWaybillState() {
  const settle = useSettle();
  return useMutation({
    mutationFn: ({ id, to }: { id: string; to: 'posted' | 'draft' }) =>
      apiClient.post<WaybillView>(`/costs/waybills/${id}/${to}`).then(r => r.data),
    onSuccess: settle,
  });
}

/** Сопоставить строку с позицией номенклатуры (`null` — снять). Работает и у проведённой накладной. */
export function useMatchWaybillLine() {
  const settle = useSettle();
  return useMutation({
    mutationFn: ({ id, lineId, nomenclatureId }: { id: string; lineId: string; nomenclatureId: string | null }) =>
      apiClient.put<WaybillView>(`/costs/waybills/${id}/lines/${lineId}/nomenclature`, {
        nomenclature: nomenclatureId ? { $ref: 'catalog', entryId: nomenclatureId } : null,
      }).then(r => r.data),
    onSuccess: settle,
  });
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
