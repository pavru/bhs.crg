import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { INVOICES_KEY } from './invoices';

/**
 * Запомненное для строки поставщика (задача C3, issue #1079, ТЗ COST-7.1).
 *
 * ⚠️ `issue` — причина, по которой подставлять НЕЛЬЗЯ, и она приходит вместе с соответствием: строка,
 * чья запомненная позиция в архиве, не «незнакома». Промолчи сервер о ней — человек запомнил бы
 * наименование заново, не узнав, что прежний выбор лежит в архиве.
 */
export interface MatchSuggestion {
  /** Место строки в вопросе, с нуля. Строк без запомненного в ответе нет. */
  index: number;
  matchId: string;
  /** По чему строка узнана: артикул или наименование. */
  by: 'code' | 'name';
  /** Как ключ записан в бумаге, с которой его запомнили. */
  source: string;
  nomenclatureId: string;
  nomenclatureName: string | null;
  nomenclatureType: string | null;
  issue: 'archived' | 'lost' | null;
  rememberedAt: string;
  rememberedBy: string | null;
}

export interface MatchQuestionLine {
  supplierCode: string | null;
  supplierText: string | null;
}

/**
 * Спросить запомненное. Адрес только читает: позицию с пометкой форма кладёт в строки сама и
 * отправляет обычным сохранением строк — с версией счёта.
 */
export function askMatchSuggestions(supplierId: string, lines: MatchQuestionLine[]): Promise<MatchSuggestion[]> {
  return apiClient
    .post<{ items: MatchSuggestion[] }>('/costs/supplier-matches/suggestions', { supplierId, lines })
    .then(r => r.data.items);
}

/**
 * Запомненное для строк, которые УЖЕ лежат в счёте без позиции, — по идентификатору строки.
 *
 * Ключ запроса собран из самих строк: сохранение строк меняет их — и вопрос задаётся заново. Набор в
 * форме сюда не попадает нарочно: иначе запрос уходил бы на каждый удар по клавише.
 */
export function useLaidMatchSuggestions(
  supplierId: string | null, lines: readonly (MatchQuestionLine & { id: string })[], enabled: boolean,
) {
  return useQuery({
    queryKey: ['costs', 'supplier-matches', 'laid', supplierId, lines],
    enabled: enabled && supplierId !== null && lines.length > 0,
    queryFn: async () => {
      const found = await askMatchSuggestions(supplierId!,
        lines.map(line => ({ supplierCode: line.supplierCode, supplierText: line.supplierText })));
      return new Map(found.map(item => [lines[item.index].id, item]));
    },
  });
}

/** Строка списка соответствий (issue #1079). */
export interface SupplierMatchItem {
  id: string;
  /** Версия записи: её называет правка заголовком `If-Match`. */
  version: string;
  supplierId: string;
  supplierName: string | null;
  supplierArchived: boolean;
  /** Записи поставщика больше нет — имя взять неоткуда, и это не «без имени». */
  supplierLost: boolean;
  by: 'code' | 'name';
  source: string;
  nomenclatureId: string;
  nomenclatureName: string | null;
  nomenclatureType: string | null;
  /** Почему соответствие НЕ подставляется; `null` — подставляется. */
  issue: 'archived' | 'lost' | null;
  updatedAt: string;
  updatedBy: string | null;
}

export interface SupplierMatchList {
  items: SupplierMatchItem[];
  /** Сколько под отбором ВСЕГО: без числа порция читалась бы как весь список. */
  total: number;
  counts: { lost: number; archived: number };
  /** Все поставщики с соответствиями — независимо от отбора. */
  suppliers: { id: string; name: string | null; archived: boolean; lost: boolean; count: number }[];
}

export interface SupplierMatchFilter {
  supplierId: string | null;
  query: string;
  issue: 'archived' | 'lost' | null;
  take: number;
}

const LIST_KEY = ['costs', 'supplier-matches', 'list'] as const;

export function useSupplierMatches(filter: SupplierMatchFilter) {
  return useQuery({
    queryKey: [...LIST_KEY, filter],
    // Прежняя порция остаётся на экране, пока едет следующая: иначе «Показать ещё» моргало бы пустым списком.
    placeholderData: keepPreviousData,
    queryFn: () => apiClient.get<SupplierMatchList>('/costs/supplier-matches', {
      params: {
        supplierId: filter.supplierId ?? undefined,
        query: filter.query.trim() || undefined,
        issue: filter.issue ?? undefined,
        take: filter.take,
      },
    }).then(r => r.data),
  });
}

/**
 * После правки соответствия перечитываются и список, и счета: пометка строки («запомнено») показывает
 * состояние соответствия, а форма счёта помнит ответ о запомненном для лежащих строк.
 */
function useMatchesChanged() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: ['costs', 'supplier-matches'] });
    void qc.invalidateQueries({ queryKey: INVOICES_KEY });
  };
}

/** Направить соответствие на другую позицию. Называет версию, по которой собрано. */
export function usePointSupplierMatch() {
  const changed = useMatchesChanged();
  return useMutation({
    mutationFn: ({ item, nomenclatureId }: { item: SupplierMatchItem; nomenclatureId: string }) =>
      apiClient.put<SupplierMatchItem>(`/costs/supplier-matches/${item.id}`, { nomenclatureId },
        { headers: { 'If-Match': item.version } }).then(r => r.data),
    onSettled: changed,
  });
}

/** Забыть соответствие. */
export function useForgetSupplierMatch() {
  const changed = useMatchesChanged();
  return useMutation({
    mutationFn: (item: SupplierMatchItem) =>
      apiClient.delete(`/costs/supplier-matches/${item.id}`, { headers: { 'If-Match': item.version } }),
    onSettled: changed,
  });
}
