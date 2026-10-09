import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

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
