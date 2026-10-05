import type { AllocationPartView, InvoiceView, TargetIssue } from '@/shared/api/invoices';

/**
 * Слова пометки потерянной ссылки (issue #1184) — по виду записи: человек должен увидеть, ЧТО пропало,
 * а не что «запись справочника удалена» (решение владельца 06.10.2026). Общая фраза остаётся сводке.
 *
 * <p>«Удалена», а не «не найдена»: второе читается как промах поиска, который можно повторить.</p>
 */
export const LOST = {
  organization: 'организация удалена',
  position: 'позиция удалена',
  construction: 'стройка удалена',
  section: 'раздел удалён',
  article: 'статья удалена',
  /** Не потеря: раздел на месте, но у другой стройки. В число потерянных ссылок не входит. */
  foreignSection: 'раздел другой стройки',
} as const;

export function issueText(issue: TargetIssue): string {
  switch (issue) {
    case 'construction-lost': return LOST.construction;
    case 'section-lost': return LOST.section;
    case 'article-lost': return LOST.article;
    case 'section-foreign': return LOST.foreignSection;
  }
}

/**
 * Что не так с целью части. Старый сервер поля `targetIssue` не присылает — тогда вид потери
 * угадывается по части, как раньше: молчать о потере хуже, чем назвать её неточно.
 */
export function issueOf(part: AllocationPartView): TargetIssue | null {
  if (part.targetIssue) return part.targetIssue;
  if (!part.targetLost) return null;
  return part.articleId ? 'article-lost' : 'construction-lost';
}

export interface LostSummary {
  /** Потерянных ссылок — тем же счётом, что у сервера: удалённая стройка с разделом даёт две. */
  count: number;
  /** Где они: «поставщик; позиция в строках 3, 7; разноска строки 5». */
  places: string;
  /** Строки с разделом другой стройки — не потеря, называется отдельно. */
  foreign: number[];
}

const numbers = (ordinals: number[]) => ordinals.join(', ');

/**
 * Сводка потерянных ссылок счёта — для шапки формы. Нужна потому, что потеря бывает за краем экрана:
 * строка под прокруткой, колонка «Разноска», свёрнутая матрица.
 *
 * @returns `null` — потерь и чужих разделов нет.
 */
export function lostSummary(view: InvoiceView): LostSummary | null {
  const places: string[] = [];
  let count = 0;

  const references = view.references;
  if (references?.supplier === 'lost') { places.push('поставщик'); count++; }
  if (references?.payer === 'lost') { places.push('плательщик'); count++; }
  if (references?.documentType === 'lost') { places.push('тип счёта'); count++; }

  const positions = view.lines.filter(l => l.nomenclatureLost).map(l => l.ordinal);
  if (positions.length > 0) {
    places.push(`позиция в ${positions.length === 1 ? 'строке' : 'строках'} ${numbers(positions)}`);
    count += positions.length;
  }

  // Удалённая стройка уносит разделы: часть с разделом на ней — две потерянные ссылки.
  const weight = (part: AllocationPartView) => {
    const issue = issueOf(part);
    if (issue === null || issue === 'section-foreign') return 0;
    return issue === 'construction-lost' && part.sectionId ? 2 : 1;
  };
  const allocated: number[] = [];
  const foreign: number[] = [];
  for (const line of view.lines) {
    const lost = line.allocation.parts.reduce((sum, part) => sum + weight(part), 0);
    if (lost > 0) { allocated.push(line.ordinal); count += lost; }
    if (line.allocation.parts.some(p => issueOf(p) === 'section-foreign')) foreign.push(line.ordinal);
  }
  if (allocated.length > 0)
    places.push(`разноска ${allocated.length === 1 ? 'строки' : 'строк'} ${numbers(allocated)}`);

  const whole = view.allocation.document.parts.reduce((sum, part) => sum + weight(part), 0);
  if (whole > 0) { places.push('разноска счёта'); count += whole; }

  return count === 0 && foreign.length === 0 ? null : { count, places: places.join('; '), foreign };
}
