import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { CatalogResource } from '../document-sets/catalog/CatalogResource';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Системный каталог общих данных (scope='System', приоритет 5). Тонкая обёртка над общим
 * `CatalogResource` (issue #210, ось видимости — единый браузер каталога на всех уровнях scope).
 */
export function SystemCommonDataPage() {
  const { data: allDocTypes = [] } = useListDocumentTypes();
  return (
    <div className="px-6 py-4">
      <PageHeader title="Системный каталог" subtitle="Общие данные, доступные во всех проектах (приоритет 5)"
        className="mb-4" />
      <CatalogResource scope="System" scopeId={null} allDocTypes={allDocTypes} />
    </div>
  );
}
