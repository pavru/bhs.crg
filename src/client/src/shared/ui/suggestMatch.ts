/** Где подсветить набранное в пункте подсказки (`SuggestField`). Отдельным файлом — его проверяют тесты. */

const normalize = (s: string) => s.toLowerCase().replace(/ё/g, 'е');
const WORD_CHAR = /[\p{L}\p{N}]/u;

/**
 * Совпадение — жирным, не цветом (UI-4): первое вхождение набранного с начала слова, без регистра и с
 * ё=е, как сравнивает сервер. Не нашлось (сервер совпал иначе) — пункт без подсветки, это безвредно.
 */
export function matchStart(value: string, q: string): number {
  const v = normalize(value);
  const needle = normalize(q.trim());
  if (needle === '') return -1;
  for (let i = v.indexOf(needle); i >= 0; i = v.indexOf(needle, i + 1)) {
    if (i === 0 || !WORD_CHAR.test(v[i - 1])) return i;
  }
  return -1;
}

/**
 * Пункт списка подсказки (UI-55): значение, которое уходит в поле, и как пункт виден. Ищется пункт по
 * `value`, а не по подписи: «Склад — в архиве» совпало бы со словом «архив», а приписка тёзки — у всех
 * тёзок разом.
 */
export interface SuggestItem {
  /** Что подставляется в поле; по нему же — совпадение с набранным и ключ пункта. */
  value: string;
  /** Как пункт виден, если отличается от значения только пометкой: «Склад — в архиве». */
  label?: string;
  /** Приписка справа, мельче: «и статья вне строек». */
  note?: string;
  /** Подпись группы — стоит над первым её пунктом в списке и только когда пункты группы в нём есть. */
  group?: string;
}

/** Больше пунктов в списке не бывает: длинный перечень листают набором, а не стрелкой. */
export const SUGGEST_LIMIT = 10;

/**
 * Пункты готового перечня под набранное — правилом подсветки (`matchStart`): с начала слова, без регистра,
 * ё = е. Порядок — перечня; пусто набранное (позвали стрелкой) — первые пункты перечня.
 */
export function matchOptions(options: readonly SuggestItem[], q: string, limit = SUGGEST_LIMIT): SuggestItem[] {
  const needle = q.trim();
  const found = needle === '' ? options : options.filter(o => matchStart(o.value, needle) >= 0);
  return found.slice(0, limit);
}
