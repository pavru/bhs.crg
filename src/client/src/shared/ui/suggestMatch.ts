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
