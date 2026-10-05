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
  sourceText: string | null;
  unit: string | null;
  quantity: number | null;
  note: string | null;
}

export interface WaybillView {
  id: string;
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
const LIST_KEY = [QK] as const;
const MATERIALS_KEY = ['costs-materials'] as const;

export function useWaybills(unmatched = false) {
  return useQuery({
    queryKey: [QK, 'list', { unmatched }] as const,
    queryFn: () => apiClient
      .get<WaybillListItem[]>('/costs/waybills', { params: unmatched ? { unmatched: true } : {} })
      .then(r => r.data),
  });
}

export function useWaybill(id: string | undefined) {
  return useQuery({
    queryKey: [QK, id] as const,
    queryFn: () => apiClient.get<WaybillView>(`/costs/waybills/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

/** Что считать устаревшим после любой правки: список, открытую накладную и перечень отпущенного. */
function useSettle() {
  const qc = useQueryClient();
  return (view: WaybillView) => {
    qc.setQueryData([QK, view.id], view);
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
 * Сохранить черновик: шапку, затем строки. Двумя запросами, потому что у сервера это два адреса; если
 * второй откажет, шапка уже записана — отказ называет строки, и повтор сохранения ничего не удваивает.
 */
export function useSaveWaybill() {
  const settle = useSettle();
  return useMutation({
    mutationFn: async ({ id, header, lines }: {
      id: string; header: WaybillHeader; lines: Record<string, unknown>[];
    }) => {
      await apiClient.put<WaybillView>(`/costs/waybills/${id}`, header);
      return apiClient.put<WaybillView>(`/costs/waybills/${id}/lines`, { lines }).then(r => r.data);
    },
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
