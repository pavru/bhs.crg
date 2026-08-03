import { SCOPE_LABELS, SCOPE_PRIORITY, type CatalogScope } from '@/shared/api/types';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

/**
 * Уровень связки материала: разбор состава и аномалий (issue #649).
 *
 * Экран документов качества грузит связки ВСЕХ областей одним запросом, а на строке уровень до сих
 * пор не показывался вовсе — при том что он управляет и поведением экрана (перепривязка идёт по
 * группам областей), и результатом генерации: при совпадении ключа побеждает более узкий уровень
 * (Set=1 … System=5, `QualityLinkResolver`). Две связки одного материала на разных уровнях давали
 * две визуально НЕРАЗЛИЧИМЫЕ строки.
 *
 * Здесь только счёт и сравнение — без React, чтобы правила проверялись тестами, а не глазами.
 */

/** Сколько связок на каждом уровне, от узкого к широкому. Пустые уровни не попадают. */
export function scopeBreakdown(links: MaterialQualityLink[]): { scope: CatalogScope; count: number }[] {
  const counts = new Map<CatalogScope, number>();
  for (const l of links) counts.set(l.scope, (counts.get(l.scope) ?? 0) + 1);
  return [...counts.entries()]
    .sort((a, b) => SCOPE_PRIORITY[a[0]] - SCOPE_PRIORITY[b[0]])
    .map(([scope, count]) => ({ scope, count }));
}

/** «Комплект 9, Система 2» — состав словами. Пустой список даёт пустую строку. */
export function scopeBreakdownText(links: MaterialQualityLink[]): string {
  return scopeBreakdown(links).map(({ scope, count }) => `${SCOPE_LABELS[scope]} ${count}`).join(', ');
}

/**
 * Связки шире комплекта — те, чей разрыв или перепривязка заденет не только текущий комплект.
 * Ради них и заведено предупреждение в подтверждениях: «Система» действует на все стройки.
 */
export function widerThanSet(links: MaterialQualityLink[]): MaterialQualityLink[] {
  return links.filter(l => SCOPE_PRIORITY[l.scope] > SCOPE_PRIORITY.Set);
}

/** Аномалия строки: почему на связке стоит предупреждающий знак. null — всё в порядке. */
export function linkAnomaly(link: MaterialQualityLink, all: MaterialQualityLink[]): string | null {
  const conflict = scopeConflict(link, all);
  if (conflict) return conflict;
  return widerThanRest(link, all);
}

/**
 * Один материал привязан на двух уровнях — строки неразличимы, а в PDF попадёт ровно одна.
 * Победителя называем прямо: догадаться о правиле «узкий побеждает» по экрану нельзя.
 */
function scopeConflict(link: MaterialQualityLink, all: MaterialQualityLink[]): string | null {
  const rivals = all.filter(l => l.materialKey === link.materialKey && l.id !== link.id);
  if (rivals.length === 0) return null;

  const winner = [...rivals, link].reduce((a, b) => (SCOPE_PRIORITY[a.scope] <= SCOPE_PRIORITY[b.scope] ? a : b));
  const levels = scopeBreakdown([...rivals, link]).map(s => SCOPE_LABELS[s.scope]).join(' и ');
  const verdict = winner.id === link.id
    ? 'при генерации подставится ЭТА — уровень уже'
    : `при генерации подставится связка уровня «${SCOPE_LABELS[winner.scope]}» — он уже`;
  return `Материал привязан на разных уровнях (${levels}): ${verdict}.`;
}

/**
 * Связка шире всех остальных в списке. Самый тихий класс ошибок привязки — «заведено шире, чем
 * нужно»: на живых данных все 113 связок сидели на уровне «Система» (#587), и по экрану это никак
 * не читалось. Когда весь список однороден, аномалии нет — предупреждать не о чем.
 */
function widerThanRest(link: MaterialQualityLink, all: MaterialQualityLink[]): string | null {
  const narrowest = Math.min(...all.map(l => SCOPE_PRIORITY[l.scope]));
  if (SCOPE_PRIORITY[link.scope] <= narrowest) return null;
  return `Связка заведена шире остальных: уровень «${SCOPE_LABELS[link.scope]}» — она действует и за пределами этого комплекта.`;
}
