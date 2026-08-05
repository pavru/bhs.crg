import { Boxes, Building2, FolderOpen, Layers } from 'lucide-react';
import { SCOPE_LABELS, type CatalogScope } from '@/shared/api/types';

/**
 * Значок уровня области (issue #649).
 *
 * Иконки не выдуманы заново, а взяты те, которыми уровни уже подписаны в приложении: стройка —
 * `Building2` (`ConstructionsList`/`ConstructionDetail`), раздел — `Layers` (`SectionDetail`),
 * комплект — `FolderOpen` (`SetDetail`). Своей иконки у «Системы» не было; `Globe` занят на экране
 * документов качества кнопкой «Найти в интернете», поэтому взят `Boxes` — «всё сразу».
 *
 * Уровень — порядковая шкала из четырёх значений, и одной картинкой она не выучивается, поэтому
 * слово всегда рядом: `title` для мыши и `aria-label` для чтения с экрана. Цвет приглушённый:
 * в типичном списке уровень у всех строк один, и яркий значок, повторённый десятки раз, стал бы
 * фоном — как повторённый номер документа, о котором говорит комментарий в `QualityDocsPage`.
 */
const SCOPE_ICONS: Record<CatalogScope, typeof Boxes> = {
  Set: FolderOpen,
  Section: Layers,
  Construction: Building2,
  System: Boxes,
};

export function ScopeIcon({ scope, size = 13, className = '', title }: {
  scope: CatalogScope; size?: number; className?: string;
  /** Своя подпись вместо «Уровень связи: X» — когда сказать нужно больше, чем сам уровень
   *  (например, что он расходится с выбранным в селекторе, issue #681). Слово об уровне в ней
   *  обязано остаться: значок один, а значений четыре. */
  title?: string;
}) {
  const Icon = SCOPE_ICONS[scope];
  const label = title ?? `Уровень связи: ${SCOPE_LABELS[scope]}`;
  return (
    <span title={label} aria-label={label} role="img"
      className={`inline-flex items-center shrink-0 text-fg4 ${className}`}>
      <Icon size={size} />
    </span>
  );
}
