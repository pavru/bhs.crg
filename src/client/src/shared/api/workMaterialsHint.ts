import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { EntryCatalogScope } from './workEntryCatalog';

export type MaterialHintTierId = 'issued' | 'estimate' | 'catalog';

export interface MaterialHintItem {
  id: string;
  displayName: string | null;
  fullName?: string | null;
  matchedAlias?: string | null;
  unit?: string | null;
  notIssued?: boolean | null;
}

export interface MaterialHintTier {
  state: 'ok' | 'not-connected' | 'hidden' | 'no-item';
  items: MaterialHintItem[];
  reason?: string | null;
  permission?: string | null;
  inArchive?: number | null;
  more?: boolean | null;
}

export interface MaterialHintLine {
  workTypeId: string;
  unitId: string;
  sectionId: string | null;
  planItemId?: string;
}

export function useWorkMaterialsHint({ siteId, date, reportVersion }: EntryCatalogScope, line: MaterialHintLine, q: string) {
  const params = { constructionId: siteId, date, q, workTypeId: line.workTypeId || undefined,
    unitId: line.unitId || undefined, sectionId: line.sectionId ?? undefined, planItemId: line.planItemId };
  return useQuery({
    queryKey: ['work', 'materials-hint', params, reportVersion],
    queryFn: ({ signal }) => apiClient.get<Record<MaterialHintTierId, MaterialHintTier>>('/work/materials/hint', { params, signal })
      .then(response => response.data),
  });
}
