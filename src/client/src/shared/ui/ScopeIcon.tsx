import { Icon } from './Icon';
import { Apartment, FolderOpen, Layers, Widgets, type IconDef, type IconSize } from './icons';
import { SCOPE_LABELS, type CatalogScope } from '@/shared/api/types';
import { Tooltip } from './Tooltip';

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
const SCOPE_ICONS: Record<CatalogScope, IconDef> = {
  Set: FolderOpen,
  Section: Layers,
  Construction: Apartment,
  System: Widgets,
};

export function ScopeIcon({ scope, size = 18, className = '', title }: {
  scope: CatalogScope; size?: IconSize; className?: string;
  /** Своя подпись вместо «Уровень связи: X» — когда сказать нужно больше, чем сам уровень
   *  (например, что он расходится с выбранным в селекторе, issue #681). Слово об уровне в ней
   *  обязано остаться: значок один, а значений четыре. */
  title?: string;
}) {
  const icon = SCOPE_ICONS[scope];
  const label = title ?? `Уровень связи: ${SCOPE_LABELS[scope]}`;
  return (
    <Tooltip content={label}>
      <span aria-label={label} role="img"
        className={`inline-flex items-center shrink-0 text-on-surface-variant ${className}`}>
        <Icon icon={icon} size={size} />
      </span>
    </Tooltip>
  );
}
