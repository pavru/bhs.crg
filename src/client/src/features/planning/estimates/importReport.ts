import type { CarryOverCountsInfo } from '@/shared/api/planningDistribution';
import { hasMark, type EstimateImport, type ImportTarget, type UnparsedPosition } from '@/shared/api/planningImports';
import { formatCount } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { KIND_LABEL, type RowQueue } from './estimateRows';

/**
 * Правила отчёта импорта (change `plan-09-estimate-ui`, PR 2, ТЗ PLAN-7): что человеку можно сделать с
 * импортом и как назвать цель. Без отрисовки — это то, что проверяет тест.
 *
 * Состояние — с сервера (D17 зонтика): `state`, `kind`, пометки отчёта. Клиент только решает, какие действия
 * показать, и не прячет отказ за пустым отчётом.
 */

/**
 * Что делать с импортом:
 * - `parsing` — разбор идёт, решать нечего;
 * - `refused` — файл не разобран или разбор прерван: только убрать;
 * - `attachment` — это не ЛСР (ВОР, КА): редакцией не станет, «создать редакцию» нет (решение В-7). `estimateId` —
 *   смета, к которой он прикладывается (`plan-11`); `null` — импорт заводит новую смету или его смету удалили:
 *   приложить не к чему, «сначала загрузите смету» (ответ на вопрос 2 листа 0 П11). `unparsed > 0` — приложить
 *   можно только явным согласием, как частичную редакцию;
 * - `empty` — разобрано, но строк у будущей редакции нет (все позиции неактивны);
 * - `ready` — можно создать редакцию; `unparsed > 0` — только явным согласием (ТЗ PLAN-7);
 * - `committed` — редакция уже создана.
 */
export type ImportVerdict =
  | { kind: 'parsing' }
  | { kind: 'refused'; text: string }
  | { kind: 'attachment'; estimateId: string | null; unparsed: number }
  | { kind: 'empty' }
  | { kind: 'ready'; unparsed: number }
  | { kind: 'committed' };

export function importVerdict(report: EstimateImport): ImportVerdict {
  const interrupted = report.marks.find(m => m.code === 'interrupted');
  if (interrupted) return { kind: 'refused', text: interrupted.text };
  switch (report.state) {
    case 'parsing': return { kind: 'parsing' };
    case 'committed': return { kind: 'committed' };
    case 'rejected': return { kind: 'refused', text: report.refusal?.text ?? 'Файл не разобран.' };
  }
  if (hasMark(report, 'notEstimate') || (report.kind !== null && report.kind !== 'lsr'))
    return { kind: 'attachment', estimateId: report.target.newEstimate ? null : report.target.estimateId, unparsed: report.unparsed.length };
  if (!report.summary || report.summary.rows === 0) return { kind: 'empty' };
  return { kind: 'ready', unparsed: report.unparsed.length };
}

/** Причины не разобранного — группами, частые первыми (макет П1, вариант 2: «чаще всего: …»). */
export function reasonGroups(unparsed: readonly UnparsedPosition[]): { reason: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const u of unparsed) counts.set(u.reason, (counts.get(u.reason) ?? 0) + 1);
  return [...counts.entries()]
    .map(([reason, count]) => ({ reason, count }))
    .sort((a, b) => b.count - a.count || a.reason.localeCompare(b.reason, 'ru'));
}

/** Куда ляжет редакция — словами отчёта. */
export function targetLabel(target: ImportTarget, sectionName?: (id: string) => string | undefined): string {
  if (target.newEstimate) {
    const { name, kind, sectionId } = target.newEstimate;
    const section = sectionId ? sectionName?.(sectionId) : undefined;
    return `новая смета «${name}» · ${KIND_LABEL[kind]}${section ? ` · раздел «${section}»` : ''}`;
  }
  return target.estimateName ? `новая редакция сметы «${target.estimateName}»` : 'новая редакция сметы, которую удалили';
}

/** Число с разрядами и словом в нужном падеже: «1 280 позиций». Одно на отчёт, кнопки и подтверждение. */
export const countOf = (n: number, one: string, few: string, many: string) => `${formatCount(n)} ${ruPlural(n, one, few, many)}`;

/** Сколько строк не войдёт в частичную редакцию: «без 1 280 строк». */
export const withoutRows = (unparsed: number) => `без ${countOf(unparsed, 'строки', 'строк', 'строк')}`;

/** Что сказать на кнопке частичной редакции: она называет последствие (макет П1, вариант 2). */
export const partialCommitLabel = (unparsed: number) => `Создать редакцию ${withoutRows(unparsed)}…`;

/** Сколько позиций не войдёт в приложение: «без 2 позиций». */
export const withoutPositions = (unparsed: number) => `без ${countOf(unparsed, 'позиции', 'позиций', 'позиций')}`;

/**
 * Главная кнопка приложения в отчёте (общий лист макета П11): смета названа в кнопке; не разобранное — кнопка
 * называет последствие и ведёт на шаг согласия.
 */
export const attachLabel = (estimateName: string, unparsed: number) =>
  unparsed > 0 ? `Приложить ${withoutPositions(unparsed)}…` : `Приложить к смете «${estimateName}»`;

/** Редакция создана — куда вести: в смету, на созданную редакцию; есть несопоставленное — с отбором по нему. */
export interface ImportCreated {
  estimateId: string;
  revisionId: string;
  unmatched: boolean;
  /** Перенос распределения ждёт решения (`ambiguous`, `overflow`, `lost`). */
  carryOver: boolean;
}

/** Ждёт ли перенос решения: `shifted` решения не требует. */
export const carryOverPending = (c: CarryOverCountsInfo | null | undefined) =>
  (c?.ambiguous ?? 0) + (c?.overflow ?? 0) + (c?.lost ?? 0) > 0;

/**
 * Переход к созданной редакции: её открывает карточка сметы, с отбором «не сопоставлено», если оно есть. Нет — а
 * перенос распределения ждёт решения — в режиме распределения на чипе «Перенос: проверить» (макет П2): в отчёте
 * импорта только числа. Несопоставленное — первым: без вида работы строке нечего распределять в плане.
 */
export interface RevisionOpening {
  revisionId: string;
  queue: RowQueue;
  mode?: 'match' | 'distribution';
}

export const openingOf = (created: ImportCreated): RevisionOpening =>
  created.unmatched ? { revisionId: created.revisionId, queue: 'unmatched', mode: 'match' }
    : created.carryOver ? { revisionId: created.revisionId, queue: 'all', mode: 'distribution' }
    : { revisionId: created.revisionId, queue: 'all', mode: 'match' };
