import type { NomenclatureItem } from '@/shared/api/invoices';
import type { LineMatched, LineMatchedWord, NomenclatureCandidate, NomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import type { NomenclatureCard } from '@/shared/api/nomenclatureCard';
import { ruCount, ruPlural } from '@/shared/utils/pluralize';
import { matchKeyText } from './supplierMatches';
import { trimText, unitKey, type LineWords } from './newNomenclature';
import type { CompareRow } from './nomenclatureCardView';

/**
 * Блок «Подходящие для этой строки» окна выбора позиции (issue #1518) — то, что проверяется без окна:
 * что показать строками, какая из них «лучшее совпадение» и как строка сверяется с позицией.
 */

/** Сколько кандидатов показать: блок читают, не прокручивая. Больше — уже поиск. */
export const CANDIDATES_SHOWN = 5;

/** Строка окна — кандидат, находка поиска или запись справочника. */
export interface PickerRow {
  id: string;
  name: string | null;
  type: string;
  /** Почему позиция в списке — одной строкой, причины через «·». */
  why: string | null;
  /** «Лучшее совпадение»: выбирается по Enter сразу при открытии. */
  best: boolean;
  /** Сравнение «строка ↔ позиция» от сервера; `null` — слова не сверялись. */
  matched: LineMatched | null;
}

/** Соседняя строка того же документа, где позиция уже выбрана. */
export interface NeighbourLine {
  /** Номер строки в документе, с единицы. */
  number: number;
  name?: string | null;
  code?: string | null;
  positionId: string;
  positionName: string | null;
}

/**
 * Соседи строки `index`: строки документа, где позиция выбрана и стоит на месте. Потерянная и архивная
 * позиция кандидатом не годится — выбрать её нельзя.
 */
export function neighboursOf<T>(
  lines: readonly T[], index: number, of: (line: T) => Omit<NeighbourLine, 'number'> | null,
): NeighbourLine[] {
  return lines.flatMap((line, i) => {
    const near = i === index ? null : of(line);
    return near ? [{ ...near, number: i + 1 }] : [];
  });
}

const joinWhy = (why: readonly string[]) => (why.length > 0 ? why.join(' · ') : null);

/**
 * Позиции, выбранные в соседних строках этого же документа для того же артикула или того же
 * наименования. Только точное равенство — тем же приведением, что ключ соответствия на сервере:
 * похожее по словам уже подбирает сервер, а здесь — выбор человека, сделанный минуту назад.
 *
 * <p>«Лучшее» — только при равном артикуле: наименование «Кабель» бывает у разных кабелей одного счёта.</p>
 */
export function neighbourCandidates(from: LineWords, lines: readonly NeighbourLine[]): NomenclatureCandidate[] {
  const code = matchKeyText(from.code);
  const name = matchKeyText(from.name);
  const picked = new Map<string, { name: string | null; numbers: number[]; byCode: boolean }>();
  for (const line of lines) {
    const byCode = code !== '' && matchKeyText(line.code) === code;
    const byName = name !== '' && matchKeyText(line.name) === name;
    if (!byCode && !byName) continue;
    const entry = picked.get(line.positionId) ?? { name: line.positionName, numbers: [], byCode: false };
    entry.numbers.push(line.number);
    entry.byCode ||= byCode;
    picked.set(line.positionId, entry);
  }
  return [...picked].map(([id, entry]) => ({
    id, name: entry.name, type: '', matchedAlias: null, matched: null, strong: entry.byCode,
    why: [entry.numbers.length === 1 ? `выбрана в строке ${entry.numbers[0]}`
      : `выбрана в строках ${entry.numbers.join(', ')}`],
  }));
}

/**
 * Кандидаты строками: ответ сервера и соседние строки, склеенные по позиции, без уже выбранной.
 *
 * <p>⚠️ <b>«Лучшее» — одно на строку или ни одного.</b> Сосед, выбравший другую позицию для той же строки,
 * делает ответ спорным — как чужой выбор на сервере: Enter по одному из двух ответов был бы догадкой,
 * которая запомнится для поставщика.</p>
 *
 * <p>Порядок: лучшее, затем выбранное соседями, затем остальное по серверу. Соседний выбор — решение
 * человека в этом же документе, и за пределом пятёрки ему не место.</p>
 */
export function candidateRows(
  answer: NomenclatureCandidates | undefined, neighbours: readonly NomenclatureCandidate[], currentId: string | null,
): { rows: PickerRow[]; more: boolean } {
  const server = answer?.items ?? [];
  const strong = new Set([...server, ...neighbours].filter(c => c.strong).map(c => c.id));
  const [only] = strong;
  const best = strong.size === 1 && neighbours.every(n => n.id === only) ? only : null;

  const extra = new Map(neighbours.map(n => [n.id, n]));
  const merged = server.map(c => {
    const near = extra.get(c.id);
    extra.delete(c.id);
    return near ? { ...c, why: [...c.why, ...near.why] } : c;
  });
  // Соседний выбор без ответа сервера: название — из строки соседа, вид сервер не называл.
  const ordered = [
    ...merged.filter(c => c.id === best),
    ...[...extra.values()].filter(c => c.id === best),
    ...merged.filter(c => c.id !== best && neighbours.some(n => n.id === c.id)),
    ...[...extra.values()].filter(c => c.id !== best),
    ...merged.filter(c => c.id !== best && !neighbours.some(n => n.id === c.id)),
  ].filter(c => c.id !== currentId);

  return {
    rows: ordered.slice(0, CANDIDATES_SHOWN).map(c => ({
      id: c.id, name: c.name, type: c.type, why: joinWhy(c.why), best: c.id === best, matched: c.matched,
    })),
    more: !!answer?.more || ordered.length > CANDIDATES_SHOWN,
  };
}

/** Почему позиция в поиске: набранного в названии нет — найдено по другому имени. */
export const aliasWhy = (item: NomenclatureItem) => (item.matchedAlias ? `найдено по «${item.matchedAlias}»` : null);

/**
 * Находки поиска строками. Находка, бывшая кандидатом, сохраняет причину и сравнение: «совпадает
 * артикул» не перестаёт быть правдой оттого, что человек начал набирать.
 */
export function foundRows(items: readonly NomenclatureItem[], candidates: readonly PickerRow[]): PickerRow[] {
  const known = new Map(candidates.map(c => [c.id, c]));
  return items.map(item => {
    const was = known.get(item.id);
    const why = joinWhy([was?.why, aliasWhy(item)].filter((w): w is string => !!w));
    return { id: item.id, name: item.name, type: item.type || was?.type || '', why, best: false, matched: was?.matched ?? null };
  });
}

/**
 * Набранное похоже на артикул: одно «слово» с цифрами. Поиск идёт только по наименованию, и пустой
 * ответ на артикул читался бы как «такой позиции нет».
 */
export function looksLikeArticle(query: string): boolean {
  const q = query.trim();
  return q.length >= 4 && !/\s/.test(q) && /\d/.test(q) && /[\p{L}-]/u.test(q);
}

/** Куски наименования строки: найденные в позиции слова — подсвеченными. */
export function wordMarks(source: string, words: readonly LineMatchedWord[]): { text: string; hit: boolean }[] {
  const marks: { text: string; hit: boolean }[] = [];
  let at = 0;
  for (const word of [...words].sort((a, b) => a.start - b.start)) {
    // Слово не на своём месте — строку присылали другой: подсветка соврала бы, показываем слова сервера.
    if (word.start < at || source.slice(word.start, word.start + word.text.length) !== word.text)
      return words.map((w, i) => ({ text: (i > 0 ? ' ' : '') + w.text, hit: w.found }));
    if (word.start > at) marks.push({ text: source.slice(at, word.start), hit: false });
    marks.push({ text: word.text, hit: word.found });
    at = word.start + word.text.length;
  }
  if (at < source.length) marks.push({ text: source.slice(at), hit: false });
  return marks;
}

function wordsText(words: readonly LineMatchedWord[]): string {
  const total = words.length;
  const found = words.filter(w => w.found).length;
  if (found === 0) return 'общих слов нет';
  if (found === total)
    return total === 1 ? 'совпадает единственное слово строки' : `совпадают все ${ruCount(total, 'слово', 'слова', 'слов')} строки`;
  return `${ruPlural(found, 'совпадает', 'совпадают', 'совпадают')} ${ruCount(found, 'слово', 'слова', 'слов')} из ${total}`;
}

const sameText = (a: string, b: string) => matchKeyText(a) === matchKeyText(b);

/**
 * Сравнение «строка ↔ позиция» в карточке кандидата. Слова наименования — от сервера (разбивает только
 * он); артикул — признаком сервера, а без него — с полем, размеченным тэгом артикула; единица — с полем
 * тэга единицы. Справочник не размечен — строки нет: сверять не с чем, а «не указан» было бы неправдой.
 */
export function lineVsCard(
  source: string, from: LineWords, matched: LineMatched | null,
  card: Pick<NomenclatureCard, 'article' | 'unit'>,
): CompareRow[] {
  const rows: CompareRow[] = [];
  if (matched && matched.words.length > 0)
    rows.push({ key: 'name', label: 'Наименование', text: wordsText(matched.words), tone: 'plain', marks: wordMarks(source, matched.words) });

  const code = trimText(from.code);
  if (code) {
    const article = card.article;
    if (matched?.field)
      rows.push({ key: 'code', label: 'Артикул', text: `совпадает — поле «${matched.field}»`, tone: 'ok' });
    else if (article && article.text === null)
      rows.push({ key: 'code', label: 'Артикул', text: article.unreadable ? 'в позиции не прочитан' : 'в позиции не указан', tone: 'plain' });
    else if (article?.text && sameText(article.text, code))
      rows.push({ key: 'code', label: 'Артикул', text: `совпадает: ${article.text}`, tone: 'ok' });
    else if (article?.text)
      rows.push({ key: 'code', label: 'Артикул', text: `отличается: в строке ${code}, в позиции ${article.text}`, tone: 'differs' });
  }

  const unit = trimText(from.unit);
  const there = card.unit;
  if (unit && there) {
    if (there.text === null)
      rows.push({ key: 'unit', label: 'Ед. изм.', text: there.unreadable ? 'в позиции не прочитана' : 'в позиции не указана', tone: 'plain' });
    else if (unitKey(there.text) === unitKey(unit))
      rows.push({ key: 'unit', label: 'Ед. изм.', text: `совпадает: ${there.text}`, tone: 'ok' });
    else
      rows.push({ key: 'unit', label: 'Ед. изм.', text: `отличается: в строке ${unit}, в позиции ${there.text}`, tone: 'differs' });
  }
  return rows;
}
