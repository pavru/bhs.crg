import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { CatalogResource } from './catalog/CatalogResource';

/** Панель «Каталог» карточки стройки и страницы раздела (реестр `constructionPanels.ts`, plan-02). */
export function CatalogPanel({ constructionId, sectionId }: { constructionId: string; sectionId?: string }) {
  const { data: docTypes = [] } = useListDocumentTypes();
  return sectionId
    ? <CatalogResource scope="Section" scopeId={sectionId} allDocTypes={docTypes} />
    : <CatalogResource scope="Construction" scopeId={constructionId} allDocTypes={docTypes} />;
}
