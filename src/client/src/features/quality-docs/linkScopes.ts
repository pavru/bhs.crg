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

/**
 * Аномалия строки: почему на связке стоит предупреждающий знак. null — всё в порядке.
 *
 * Два вопроса задаются РАЗНЫМ спискам: «шире остальных» — про связки этого документа (`inDocument`),
 * «за материал спорят два документа» — про всю библиотеку (`all`). Считать спор внутри одного
 * документа бессмысленно: кто бы ни победил, в PDF попадёт он же.
 */
export function linkAnomaly(
  link: MaterialQualityLink,
  { inDocument, all }: { inDocument: MaterialQualityLink[]; all: MaterialQualityLink[] },
): string | null {
  return documentConflict(link, all) ?? widerThanRest(link, inDocument);
}

/**
 * За один материал спорят ДВА РАЗНЫХ документа качества, и в PDF попадёт ровно один. По экрану
 * догадаться нельзя: на карточке проигравшего связка выглядит совершенно здоровой.
 *
 * Соперниками считаем только те связки, чьи области заведомо пересекаются, — то есть когда одна из
 * них общесистемная. Про пару «Комплект — Раздел» сказать нечего: содержит ли ТОТ раздел ЭТОТ
 * комплект, знает дерево объектов, а его мы на этот экран намеренно не тянем (решение по #649).
 * Ложная тревога здесь хуже молчания: экран и заведён ради поиска настоящих дефектов.
 */
function documentConflict(link: MaterialQualityLink, all: MaterialQualityLink[]): string | null {
  const rivals = all.filter(l =>
    l.materialKey === link.materialKey
    && l.id !== link.id
    && l.qualityDocumentId !== link.qualityDocumentId
    && (l.scope === 'System' || link.scope === 'System')); // области пересекаются заведомо
  if (rivals.length === 0) return null;

  const winner = [...rivals, link].reduce((a, b) => (SCOPE_PRIORITY[a.scope] <= SCOPE_PRIORITY[b.scope] ? a : b));
  const rivalNames = [...new Set(rivals.map(r => r.qualityDocumentName))].join(', ');
  return winner.id === link.id
    ? `Тот же материал привязан и к другому документу (${rivalNames}) на уровне «${SCOPE_LABELS[rivals[0].scope]}»: `
      + 'при генерации подставится ЭТА связка — её уровень уже.'
    : `Тот же материал привязан к документу «${winner.qualityDocumentName}» на уровне `
      + `«${SCOPE_LABELS[winner.scope]}»: при генерации подставится ОН, а не этот документ.`;
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
