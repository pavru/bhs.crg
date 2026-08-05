/**
 * Сопоставление материала с документом качества (issue #552).
 *
 * Вынесено из `QualityLinksTab`, чтобы оценку массовой привязки можно было проверить тестами:
 * именно массовая привязка без единого сигнала и породила 68 неверных связок из 69 на одном
 * сертификате.
 */

const STOP = new Set(['для', 'или', 'при', 'без', 'шт', 'штук', 'тип', 'сертификат', 'декларация', 'соответствия']);

export function normText(s: string): string {
  return s.toLowerCase().replace(/ё/g, 'е').replace(/[^a-zа-я0-9]+/g, ' ').trim();
}

export function tokenize(s: string): string[] {
  return normText(s).split(/\s+/).filter(t => t.length >= 3 && !STOP.has(t));
}

/**
 * Грубая основа слова: снимаем ОДНУ конечную гласную (или мягкий знак) и ограничиваем длину.
 *
 * Срезом «длиннее шести» ограничиться нельзя: он не сводит короткие существительные, а именно они
 * и составляют номенклатуру — «кабель»/«кабели», «лампа»/«лампы», «муфта»/«муфты» — все ровно по
 * шесть букв и остаются разными словами. Померено на 113 живых связках: вариант без снятия
 * окончания ложно тревожит ТРИ верные связки («Кабель ВВГнг … РЭМЗ» ↔ «РЭМЗ — Кабели силовые»,
 * термоусадка EKF ↔ информационное письмо EKF) и при этом не ловит ни одной неверной сверх текущих.
 */
export function stem(t: string): string {
  const base = t.length >= 5 && /[аеиоуыюяьй]$/.test(t) ? t.slice(0, -1) : t;
  return base.length > 6 ? base.slice(0, 6) : base;
}

/** Все строковые значения объекта — «стог» для сопоставления. */
export function collectStrings(v: unknown, out: string[]): void {
  if (typeof v === 'string') out.push(v);
  else if (Array.isArray(v)) for (const x of v) collectStrings(x, out);
  else if (v && typeof v === 'object') for (const x of Object.values(v)) collectStrings(x, out);
}

export interface WeightedToken { t: string; w: number }

export function weighted(query: string): WeightedToken[] {
  const seen = new Set<string>(); const out: WeightedToken[] = [];
  for (const t of tokenize(query)) {
    if (seen.has(t)) continue; seen.add(t);
    // числа/модели и длинные слова важнее общих коротких слов
    out.push({ t, w: /\d/.test(t) ? 3 : t.length >= 6 ? 2 : 1 });
  }
  return out;
}

export function relevance(tokens: WeightedToken[], hayStems: Set<string>): number {
  if (tokens.length === 0) return 0;
  let matched = 0, total = 0;
  for (const { t, w } of tokens) { total += w; if (hayStems.has(stem(t))) matched += w; }
  return total ? matched / total : 0;
}

/** Материал подходит документу, если совпала хотя бы такая доля веса слов. */
export const FITS_THRESHOLD = 0.34;

/**
 * Как выбранные материалы соотносятся с одним документом.
 *
 * Три группы, а не «процент», и это принципиально. Прогон по 113 живым связкам показал: 58 из них
 * дают ровно 0 %, и почти все нули — артикулы (`mb15-07-01m-54`), где сопоставлять просто нечего.
 * Красить такой ноль тревогой значит приучить человека игнорировать сигнал на самой аккуратной
 * половине данных. Поэтому «непроверяемо» отделено от «не похоже»:
 *
 * <ul>
 *   <li><b>fits</b> — слова материала нашлись в документе;</li>
 *   <li><b>unverifiable</b> — у материала нет слов для сравнения (один артикул/цифры);</li>
 *   <li><b>mismatched</b> — слова ЕСТЬ, и ни одно не совпало. Только это и есть повод предупредить.</li>
 * </ul>
 */
export interface BulkLinkAssessment<T> {
  fits: T[];
  unverifiable: T[];
  mismatched: T[];
}

/**
 * Слово, по которому вообще можно судить о совпадении: чисто буквенное и не короткое.
 *
 * Артикул `mb15-07-01m-54` распадается на «слова» вроде `mb15` и `01m` — они выглядят токенами, но
 * сравнивать по ним нечего: ни один сертификат их не содержит, и материал был бы объявлен «не
 * похожим» без всяких оснований. Именно на этом ломается наивная проверка «релевантность = 0».
 */
function isWordy(token: string): boolean {
  return token.length >= 4 && !/\d/.test(token);
}

/**
 * Сколько словесных признаков должно быть у САМОГО документа, чтобы по нему вообще можно было судить.
 *
 * Симметрично материалу: у нераспознанного документа («[PDF] Информационное письмо» — название и
 * пустые реквизиты) слов почти нет, и тогда «не похоже» получают ВСЕ материалы подряд, включая
 * верные. Живые 17 документов дают 12…280 словесных основ, а единственный нераспознанный — 2, так
 * что порог отделяет вырожденный случай, не задевая содержательные.
 */
const MIN_DOC_WORDS = 5;

/** Как ОДИН материал соотносится с одним документом — те же три исхода, что у массовой оценки. */
export type MaterialVerdict = keyof BulkLinkAssessment<unknown>;

/**
 * Вердикт по одному материалу против готового стога основ документа.
 *
 * Отдельно от `assessBulkLink`, потому что тем же вопросом задаются подсказки из библиотеки: они
 * считались по СВОЕЙ линейке (все токены, включая цифровые) и потому могли предложить материал,
 * который ручная привязка объявила бы «не похожим» (issue #682). Один ответ на один вопрос.
 */
export function materialVerdict(name: string, hay: Set<string>): MaterialVerdict {
  // Документ нечем проверять — судить не о чем ни по одному материалу.
  if ([...hay].filter(isWordy).length < MIN_DOC_WORDS) return 'unverifiable';

  const tokens = weighted(name).filter(x => isWordy(x.t));
  if (tokens.length === 0) return 'unverifiable';
  return relevance(tokens, hay) >= FITS_THRESHOLD ? 'fits' : 'mismatched';
}

export function assessBulkLink<T>(
  materials: readonly T[],
  nameOf: (m: T) => string,
  docText: readonly string[],
): BulkLinkAssessment<T> {
  const hay = new Set(tokenize(docText.join(' ')).map(stem));
  const result: BulkLinkAssessment<T> = { fits: [], unverifiable: [], mismatched: [] };
  for (const m of materials) result[materialVerdict(nameOf(m), hay)].push(m);
  return result;
}

/**
 * Лучший документ библиотеки для материала — подсказка в строке. null, если предлагать нечего.
 *
 * Порог и линейка — общие с ручной проверкой (issue #682). До этого подсказки жили на своей копии
 * порога и считались по ВСЕМ токенам, включая цифровые: материал мог набрать порог на одних цифрах
 * и получить подсказку, которую ручная привязка объявила бы «не похожей» с предупреждением. То
 * есть «Принять предложения» молча привязывало ровно то, на чём ручной путь останавливает.
 *
 * Цифровые токены при этом не выброшены: совпавший артикул — сигнал сильнее любого слова, и по
 * нему подсказка честно выдаётся. Отсекается только вердикт «не похоже», а не «непроверяемо».
 */
export function bestSuggestion<D>(
  material: { label: string; idValues: string[] },
  library: readonly { doc: D; stems: Set<string> }[],
): { doc: D; score: number } | null {
  const tokens = weighted(material.idValues.join(' '));
  if (tokens.length === 0) return null;

  let best: { doc: D; score: number; stems: Set<string> } | null = null;
  for (const entry of library) {
    const score = relevance(tokens, entry.stems);
    if (score > (best?.score ?? 0)) best = { doc: entry.doc, score, stems: entry.stems };
  }
  if (!best || best.score < FITS_THRESHOLD) return null;
  if (materialVerdict(material.label, best.stems) === 'mismatched') return null;
  return { doc: best.doc, score: best.score };
}

/** Название документа + все его строковые реквизиты — стог основ слов. */
export function docHaystackStems(displayName: string, requisites: unknown): Set<string> {
  const parts: string[] = [displayName];
  collectStrings(requisites, parts);
  return new Set(tokenize(parts.join(' ')).map(stem));
}

/**
 * Материалы, спорящие за одну связку (issue #585): совпадает ХОТЯ БЫ ОДНО значение идентичности
 * (тот же артикул, то же наименование), а полный ключ разный — то есть это либо одна позиция,
 * записанная в двух таблицах по-разному, либо два разных товара с общим артикулом.
 *
 * Различить их система не может и не должна: решение за пользователем — исправить материал или
 * завести две связки. Её дело — показать, что выбор есть. До составного ключа (#582) такие строки
 * молча делили одну связку, и побеждала та, чьё поле стояло в схеме раньше.
 *
 * Возвращает ключ строки → значения, которыми она пересекается с другими.
 */
export function collidingIdentities(
  rows: readonly { key: string; idValues: string[] }[],
  normalize: (s: string) => string,
): Map<string, string[]> {
  const keysByValue = new Map<string, Set<string>>();
  for (const row of rows)
    for (const value of row.idValues) {
      const norm = normalize(value);
      if (!norm) continue;
      const keys = keysByValue.get(norm) ?? new Set<string>();
      keys.add(row.key);
      keysByValue.set(norm, keys);
    }

  const result = new Map<string, string[]>();
  for (const row of rows) {
    // Значение считается спорным, когда его делят РАЗНЫЕ полные ключи: одинаковые ключи — это одна
    // и та же строка, встреченная дважды (набор данных и реквизиты), и спорить ей не с кем.
    const shared = row.idValues.filter(v => (keysByValue.get(normalize(v))?.size ?? 0) > 1);
    if (shared.length > 0) result.set(row.key, shared);
  }
  return result;
}
