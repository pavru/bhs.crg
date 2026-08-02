import type { FieldGroup } from '@/shared/api/schema';

/**
 * Проверки ключей схемы, ловящие опечатки (issue #639).
 *
 * Ключ печатается руками — у сохранённого поля он заморожен и правится только вручную (#355), — и
 * опечатка ничем не отличается от нового поля. Так в типе АОСР появился `ДатаДокумнета` рядом с
 * настоящим `ДатаДокумента`: две переставленные буквы, и оба ключа мирно живут годами.
 */

/**
 * Расстояние Дамерау—Левенштейна (с перестановкой соседних символов) — не больше `limit`.
 * Возвращает −1, если различий заведомо больше: считать точное расстояние незачем, а ранний выход
 * бережёт проход по всем ключам типа на каждое нажатие клавиши.
 *
 * Перестановка учитывается намеренно: «Докумнета» отличается от «Документа» ровно ею, и по обычному
 * Левенштейну это ДВА различия — то есть та самая опечатка, ради которой всё затевалось, не прошла бы
 * порог, поставленный по одному различию.
 */
export function boundedEditDistance(a: string, b: string, limit: number): number {
  if (a === b) return 0;
  if (Math.abs(a.length - b.length) > limit) return -1;

  const width = b.length + 1;
  let twoAgo: number[] = new Array(width).fill(0);
  let prev: number[] = Array.from({ length: width }, (_, j) => j);
  let cur: number[] = new Array(width).fill(0);

  for (let i = 1; i <= a.length; i++) {
    cur[0] = i;
    let rowMin = cur[0];
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      let v = Math.min(cur[j - 1] + 1, prev[j] + 1, prev[j - 1] + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) v = Math.min(v, twoAgo[j - 2] + 1);
      cur[j] = v;
      if (v < rowMin) rowMin = v;
    }
    // Вся строка уже дороже предела — дальше расстояние только растёт.
    if (rowMin > limit) return -1;
    const spare = twoAgo;
    twoAgo = prev;
    prev = cur;
    cur = spare;
  }
  return prev[b.length] <= limit ? prev[b.length] : -1;
}

/** Короткие ключи не сравниваем: «Код» и «Кол» различаются одной буквой и оба законны. */
const MIN_LENGTH_FOR_TYPO = 6;

/**
 * Ключ из `others`, подозрительно похожий на `key`, либо null.
 *
 * Похожими считаются два случая: совпадение без учёта регистра (разный регистр даёт РАЗНЫЕ ключи, и
 * различить их глазами в списке невозможно) и одно различие в достаточно длинных ключах.
 *
 * Это предупреждение, а не запрет: законно похожие ключи существуют, и запрет заставил бы обходить
 * проверку выдумыванием имён.
 */
export function similarKeyOf(key: string, others: readonly string[]): string | null {
  const k = key.trim();
  if (!k) return null;
  const lower = k.toLocaleLowerCase();

  for (const other of others) {
    const o = other.trim();
    if (!o || o === k) continue;
    if (o.toLocaleLowerCase() === lower) return o;
  }
  if (k.length < MIN_LENGTH_FOR_TYPO) return null;

  for (const other of others) {
    const o = other.trim();
    if (!o || o === k || o.length < MIN_LENGTH_FOR_TYPO) continue;
    if (boundedEditDistance(lower, o.toLocaleLowerCase(), 1) >= 0) return o;
  }
  return null;
}

/** Где именно встретилась ссылка на несуществующий ключ — чтобы человек знал, что чинит. */
export interface DanglingKeyRef {
  key: string;
  /** Заголовки групп, в раскладке которых стоит ключ. */
  groups: string[];
  /** Ключ перечислен в исключениях унаследованных полей. */
  excluded: boolean;
}

/**
 * Ссылки на поля, которых нет ни среди своих, ни среди унаследованных (issue #639).
 *
 * Такая ссылка не видна в интерфейсе ВООБЩЕ: в раскладке групп рисуются поля, а не имена ключей, а
 * список исключений показывает унаследованные поля. Поэтому `ДатаДокумнета` и пережил все правки
 * схемы — убрать его из UI нельзя, потому что его в UI нет.
 *
 * `knownKeys` — свои поля плюс ПОЛНЫЙ набор родительских (до исключений): исключённый ключ ссылается
 * на существующее родительское поле, в этом и смысл исключения, и висячим он не является.
 */
export function danglingKeyRefs(
  groups: readonly FieldGroup[],
  excludedFields: readonly string[],
  knownKeys: Iterable<string>,
): DanglingKeyRef[] {
  const known = new Set(knownKeys);
  const found = new Map<string, DanglingKeyRef>();

  const at = (key: string) => {
    let ref = found.get(key);
    if (!ref) { ref = { key, groups: [], excluded: false }; found.set(key, ref); }
    return ref;
  };

  for (const g of groups) {
    for (const key of g.fieldKeys ?? []) {
      if (known.has(key)) continue;
      at(key).groups.push(g.title || '(без названия)');
    }
  }
  for (const key of excludedFields) {
    if (known.has(key)) continue;
    at(key).excluded = true;
  }
  return [...found.values()];
}

/** Человеческое перечисление мест, где ключ встретился («в группе «Реквизиты», в исключениях»). */
export function danglingRefPlaces(ref: DanglingKeyRef): string {
  const places = ref.groups.map(g => `в группе «${g}»`);
  if (ref.excluded) places.push('в исключениях');
  return places.join(', ');
}
