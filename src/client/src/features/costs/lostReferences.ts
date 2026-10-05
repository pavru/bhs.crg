import type { AllocationPartView, InvoiceLineView, InvoiceView, TargetIssue } from '@/shared/api/invoices';

/**
 * Слова пометки потерянной ссылки (issue #1184) — по виду записи: человек должен увидеть, ЧТО пропало,
 * а не что «запись справочника удалена» (решение владельца 06.10.2026). Общая фраза остаётся сводке.
 *
 * <p>«Удалена», а не «не найдена»: второе читается как промах поиска, который можно повторить.</p>
 *
 * <p>⚠️ Слова — только отсюда. Литерал в одном из экранов разводит форму и матрицу при первой же смене
 * формулировки.</p>
 */
export const LOST = {
  organization: 'организация удалена',
  position: 'позиция удалена',
  construction: 'стройка удалена',
  section: 'раздел удалён',
  article: 'статья удалена',
  // Ниже — НЕ потеря: запись на месте, и в число потерянных ссылок она не входит.
  foreignSection: 'раздел другой стройки',
  movedPosition: 'позиция другого вида',
  movedArticle: 'статья другого вида',
  movedOrganization: 'запись другого вида',
  unreadArticle: 'статьи не прочитаны',
} as const;

export function issueText(issue: TargetIssue): string {
  switch (issue) {
    case 'construction-lost': return LOST.construction;
    case 'section-lost': return LOST.section;
    case 'article-lost': return LOST.article;
    case 'section-foreign': return LOST.foreignSection;
    case 'article-moved': return LOST.movedArticle;
    case 'article-unread': return LOST.unreadArticle;
  }
}

/** Потерянная ссылка — записи больше нет. Остальные состояния цели потерей не считаются. */
const isLost = (issue: TargetIssue | null) => issue !== null && issue.endsWith('-lost');

/**
 * Что не так с целью части. Старый сервер поля `targetIssue` не присылает — тогда вид потери
 * угадывается по части, как раньше: молчать о потере хуже, чем назвать её неточно.
 */
export function issueOf(part: AllocationPartView): TargetIssue | null {
  if (part.targetIssue) return part.targetIssue;
  if (!part.targetLost) return null;
  return part.articleId ? 'article-lost' : 'construction-lost';
}

/** Позиция строки потеряна — а не переведена в другой вид. */
const positionLost = (line: InvoiceLineView) => line.nomenclatureLost && line.nomenclatureIssue !== 'moved';

export interface LostSummary {
  /**
   * В скольких МЕСТАХ счёта стоит удалённая запись. Не число ссылок счётчика модуля: тот считает
   * колонки (удалённая стройка с разделом — две ссылки), а узнать это по ответу счёта нельзя — и
   * выдавать догадку за то же число не нужно.
   */
  count: number;
  /** Где они: «поставщик; позиция в строках 3, 7; разноска строки 5». */
  places: string;
  /** Что на месте, но не то: «раздел другой стройки — разноска строки 2». Не потеря. */
  others: string[];
}

const numbers = (ordinals: number[]) => ordinals.join(', ');
const inLines = (ordinals: number[]) => `${ordinals.length === 1 ? 'строки' : 'строк'} ${numbers(ordinals)}`;

/**
 * Сводка потерянных ссылок счёта — для шапки формы. Нужна потому, что потеря бывает за краем экрана:
 * строка под прокруткой, колонка «Разноска», свёрнутая матрица.
 *
 * @returns `null` — сказать не о чем.
 */
export function lostSummary(view: InvoiceView): LostSummary | null {
  const places: string[] = [];
  let count = 0;

  const references = view.references;
  if (references?.supplier === 'lost') { places.push('поставщик'); count++; }
  if (references?.payer === 'lost') { places.push('плательщик'); count++; }
  if (references?.documentType === 'lost') { places.push('тип счёта'); count++; }

  const positions = view.lines.filter(positionLost).map(l => l.ordinal);
  if (positions.length > 0) {
    places.push(`позиция в ${positions.length === 1 ? 'строке' : 'строках'} ${numbers(positions)}`);
    count += positions.length;
  }

  const allocated: number[] = [];
  const odd = new Map<string, number[]>();
  for (const line of view.lines) {
    const issues = line.allocation.parts.map(issueOf);
    const lost = issues.filter(isLost).length;
    if (lost > 0) { allocated.push(line.ordinal); count += lost; }
    for (const issue of new Set(issues.filter(i => i !== null && !isLost(i))))
      odd.set(issueText(issue!), [...(odd.get(issueText(issue!)) ?? []), line.ordinal]);
  }
  if (allocated.length > 0) places.push(`разноска ${inLines(allocated)}`);

  const whole = view.allocation.document.parts.map(issueOf).filter(isLost).length;
  if (whole > 0) { places.push('разноска счёта'); count += whole; }

  const others = [...odd].map(([text, lines]) => `${text} — разноска ${inLines(lines)}`);
  const moved = view.lines.filter(l => l.nomenclatureIssue === 'moved').map(l => l.ordinal);
  if (moved.length > 0) others.unshift(`${LOST.movedPosition} — в ${moved.length === 1 ? 'строке' : 'строках'} ${numbers(moved)}`);

  return count === 0 && others.length === 0 ? null : { count, places: places.join('; '), others };
}
