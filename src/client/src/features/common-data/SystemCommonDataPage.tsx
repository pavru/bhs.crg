import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { CatalogResource } from '../document-sets/catalog/CatalogResource';

/**
 * Системный каталог общих данных (scope='System', приоритет 5). Тонкая обёртка над общим
 * `CatalogResource` (issue #210, ось видимости — единый браузер каталога на всех уровнях scope).
 */
export function SystemCommonDataPage() {
  const { data: allDocTypes = [] } = useListDocumentTypes();
  return (
    <div className="px-6 py-4">
      <div className="mb-4">
        <h1 className="text-xl font-semibold text-fg1">Системный каталог</h1>
        <p className="text-xs text-fg3 mt-0.5">Общие данные, доступные во всех проектах (приоритет 5)</p>
      </div>
      <CatalogResource scope="System" scopeId={null} allDocTypes={allDocTypes} />
    </div>
  );
}
