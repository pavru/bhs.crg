import type { InvoiceView } from '@/shared/api/invoices';
import type { SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { hiddenEmpty as hiddenEmptyOf } from '@/shared/siteFilter/siteFilterTexts';

/**
 * Рейл счетов под фильтром «Избранные стройки» (issue #1221) — чистые функции и тексты.
 *
 * <p>Отбирает клиент, правилом из #1218 (`isVisible`): список счетов приходит целиком, с `constructionIds`
 * у каждого элемента. Неразнесённый счёт и счёт со ссылкой на удалённую стройку видны всегда; счёт на
 * нескольких стройках виден, если хоть одна избранная, — и сумма в строке остаётся целой.</p>
 *
 * <p>⚠️ Когда список станет страничным (G4), отбор переедет на сервер параметром `sites`, как у
 * накладных (#1222): страница из ста счетов, урезанная здесь, показала бы три — и «скрыто» считало бы
 * только эту страницу.</p>
 */

/**
 * Скрыт ли счёт с такими стройками в рейле. Решает одно правило — `visible` фильтра (оно же знает, что
 * фильтр не действует): вторая проверка здесь разошлась бы с ним при первой правке (ревью PR #1334).
 */
export function hiddenBySite(sites: readonly string[] | undefined, filter: Pick<SiteFilterValue, 'visible'>): boolean {
  return !filter.visible(sites ?? []);
}

/**
 * Стройки разноски открытого счёта — по частям строк и частям счёта целиком, каждая один раз. Тем же
 * составом, что `constructionIds` в элементе списка: по ним решается, уйдёт ли счёт из рейла.
 */
export function viewSites(view: InvoiceView): string[] {
  const parts = [...view.lines.flatMap(l => l.allocation.parts), ...view.allocation.document.parts];
  return [...new Set(parts.map(p => p.constructionId).filter((id): id is string => !!id))];
}

/** Над формой счёта, которого в рейле нет: открыт по ссылке или ушёл из списка после разноски. */
export const NOT_IN_RAIL = 'Этого счёта нет в списке слева: он разнесён на стройки не из избранного';

/** Тост после своей записи, которая увела счёт из рейла. */
export const SAVED_HIDDEN = 'Сохранено. В списке счёт скрыт: он разнесён на стройки не из избранного';

/** Пустой рейл, когда счета есть, но все скрыты: стоит ПЕРЕД «Счетов пока нет». */
export function hiddenEmpty(hidden: number): string {
  return hiddenEmptyOf('счетов', hidden);
}

/** Поиск ничего не нашёл среди видимых, а среди скрытых нашёл бы. */
export const SEARCH_HIDDEN = 'Среди избранных строек не найдено.';
