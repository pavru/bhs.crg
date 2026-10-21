import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Кандидаты для строки бумаги в окне «Позиция номенклатуры» (issue #1518, адрес — #1515): что подходит
 * строке счёта, накладной или соответствия — с причиной одной фразой.
 */

/** Слово строки: где оно в присланном тексте и нашлось ли в позиции. Разбивает на слова только сервер. */
export interface LineMatchedWord {
  text: string;
  /** Место слова в присланном наименовании — по нему подсвечивается сама строка. */
  start: number;
  found: boolean;
}

/** Сравнение «строка ↔ позиция» словами сервера. */
export interface LineMatched {
  words: LineMatchedWord[];
  /** Заголовок поля ключа, с которым совпал артикул строки; `null` — не совпал. */
  field: string | null;
}

export interface NomenclatureCandidate {
  id: string;
  name: string | null;
  type: string;
  matchedAlias: string | null;
  /** Причины — готовые к показу, старшая первой. */
  why: string[];
  /** «Лучшее совпадение», выбирается по Enter. Ставит только сервер: клиент причину не разбирает. */
  strong: boolean;
  /** `null` — кандидата назвал только чужой выбор: слова строки с позицией не сверялись. */
  matched: LineMatched | null;
}

export interface NomenclatureCandidates {
  items: NomenclatureCandidate[];
  /** Подошло больше, чем показано. */
  more: boolean;
  /** Сколько подошло архивных позиций. */
  inArchive: number;
  /** Сколько позиций сверить не удалось — это не «не подошли». */
  unreadable: number;
  /** Для строки этого поставщика запомнена позиция, которую выбрать нельзя. */
  recalledUnusable: { id: string; name: string | null; issue: 'archived' | 'lost' } | null;
}

/** О чём спросить: строка бумаги и чья она. */
export interface CandidatesQuestion {
  supplierId: string | null;
  /** Наименование как в бумаге, без краевых пробелов: по нему же подсвечиваются слова. */
  name: string;
  code: string;
}

export const candidatesKey = (q: CandidatesQuestion) =>
  ['nomenclature-candidates', q.supplierId, q.name, q.code] as const;

/**
 * Кандидаты для одной строки; `null` — спрашивать не о чем. Спрашивается при открытии окна, а не на
 * каждую букву поиска: вопрос — слова строки, а не набранное.
 *
 * <p>Прошлый ответ НЕ придерживается: он про другую строку, и Enter по его «лучшему» выбрал бы чужое.</p>
 */
export function useNomenclatureCandidates(question: CandidatesQuestion | null) {
  return useQuery({
    queryKey: question ? candidatesKey(question) : ['nomenclature-candidates', 'none'],
    queryFn: ({ signal }) => apiClient
      .post<{ lines: NomenclatureCandidates[] }>('/costs/nomenclature/candidates', {
        supplierId: question!.supplierId,
        lines: [{ supplierText: question!.name || null, supplierCode: question!.code || null }],
      }, { signal })
      .then(r => r.data.lines[0]),
    enabled: question !== null,
    // Стёр запрос — кандидаты из кэша: окно открыто минуты, а не часы.
    staleTime: 5 * 60_000,
    retry: false,
  });
}
