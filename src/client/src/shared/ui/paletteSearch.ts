/**
 * Поиск командной палитры (issue #1433): по СЛОВАМ, а не подстрокой. «тёмная тема» находит
 * «Тема: тёмная», «новый счет» — «Новый счёт»: человек пишет, как говорит, а не как подписан пункт.
 *
 * <p>Пункт подходит, когда каждое слово запроса встречается в его подписи или в его словах поиска
 * («создать», «скан»). Подсвечиваются совпадения только в подписи: слова поиска не показываются.</p>
 */

/**
 * Регистр и «ё» не различаются. Длина строки не меняется — по ней режется подсветка: буква, у которой
 * строчная длиннее («İ» → «i̇»), остаётся как есть (ревью PR #1478).
 */
const fold = (s: string) => [...s].map(c => {
  const lower = c.toLowerCase();
  return lower.length === c.length ? lower : c;
}).join('').replaceAll('ё', 'е');

export function queryWords(query: string): string[] {
  return fold(query).split(/\s+/).filter(Boolean);
}

export function matches(label: string, words: string, query: string[]): boolean {
  const haystack = fold(`${label} ${words}`);
  return query.every(w => haystack.includes(w));
}

export interface Piece { text: string; hit: boolean }

/** Подпись кусками: совпавшие со словами запроса — `hit`. Пересекающиеся совпадения сливаются. */
export function highlight(label: string, query: string[]): Piece[] {
  const folded = fold(label);
  const hit = new Array<boolean>(label.length).fill(false);
  for (const w of query)
    for (let at = folded.indexOf(w); at !== -1; at = folded.indexOf(w, at + 1))
      hit.fill(true, at, at + w.length);

  const pieces: Piece[] = [];
  for (let i = 0; i < label.length; i++) {
    const last = pieces.at(-1);
    if (last && last.hit === hit[i]) last.text += label[i];
    else pieces.push({ text: label[i], hit: hit[i] });
  }
  return pieces;
}

/**
 * Насколько пункт подходит: 0 — подпись начинается с запроса, 1 — все слова в подписи, 2 — найден
 * только словами поиска. «счета» обязано вести в раздел «Счета», а не заводить «Новый счёт», у которого
 * «счета» лишь в словах: Enter исполняет первый пункт.
 */
export function rank(label: string, query: string[]): number {
  const folded = fold(label);
  if (query.length === 0 || folded.startsWith(query.join(' '))) return 0;
  return query.every(w => folded.includes(w)) ? 1 : 2;
}

/**
 * Порядок выдачи: группа — по лучшему пункту в ней, пункты внутри — по своему месту; при равенстве
 * держится исходный порядок. Группа не рвётся: заголовок у неё один.
 *
 * <p>Найденное в данных (`found`, shell-05) началом подписи не выигрывает: совпадение подписью раздела
 * выше совпадения в данных — «счета» ведёт в раздел, а не в счёт «Счета за май».</p>
 */
export function ranked<T extends { label: string; group: string; found?: true; trails?: true }>(items: T[], query: string[]): T[] {
  // `trails` — действие с найденной записью: на полшага за записью, чтобы первой в выдаче стояла она.
  const scored = items.map((it, at) => ({
    it, at, score: Math.max(rank(it.label, query), it.found ? 1 : 0) + (it.trails ? 0.5 : 0),
  }));
  const best = new Map<string, number>();
  for (const s of scored) best.set(s.it.group, Math.min(best.get(s.it.group) ?? Infinity, s.score));
  const first = new Map<string, number>();
  for (const s of scored) if (!first.has(s.it.group)) first.set(s.it.group, s.at);
  const key = (s: (typeof scored)[number]) => [best.get(s.it.group)!, first.get(s.it.group)!, s.score, s.at];
  return scored
    .sort((a, b) => {
      const [x, y] = [key(a), key(b)];
      for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] - y[i];
      return 0;
    })
    .map(s => s.it);
}
