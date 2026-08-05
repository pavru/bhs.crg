import { SCOPE_LABELS } from '@/shared/api/types';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';
import { scopeBreakdownText, widerThanSet } from './linkScopes';

/**
 * Что зацепит действие над связками — состав по уровням и предупреждение про широкие (issue #649).
 *
 * Разрыв и перепривязка идут поперёк уровней, а до этого в подтверждении стояло одно число. Связка
 * уровня «Стройка» или «Система» действует далеко за пределами комплекта, из которого её завели, —
 * и человек узнавал об этом только по последствиям.
 *
 * Живёт отдельным файлом, а не в `QualityDocLinks`: то же предупреждение понадобилось вкладке
 * документа комплекта (issue #682), а она отдаёт `QualityDocLinks` пикер связывания — импорт в
 * обратную сторону замкнул бы модули в кольцо.
 */
export function ScopeReachNote({ links }: { links: MaterialQualityLink[] }) {
  if (links.length === 0) return null;
  const wide = widerThanSet(links);
  const breakdown = scopeBreakdownText(links);
  const mixed = links.some(l => l.scope !== links[0].scope);
  if (wide.length === 0 && !mixed) return null; // всё в одном комплекте — говорить не о чем

  return (
    <p className="mt-2 text-warning">
      {links.length > 1 && <>По уровням: {breakdown}. </>}
      {wide.length > 0 && (
        links.length === 1
          ? <>Связка уровня «{SCOPE_LABELS[links[0].scope]}» — она действует не только в этом
              комплекте, но и всюду, куда распространяется этот уровень.</>
          : <>Из них шире комплекта: {wide.length} — они действуют и в других комплектах.</>
      )}
    </p>
  );
}
