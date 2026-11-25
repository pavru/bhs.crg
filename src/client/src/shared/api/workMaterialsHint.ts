import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { apiError } from '@/shared/utils/apiError';
import { apiClient } from './client';
import { getEntryCatalog, type EntryCatalogScope } from './workEntryCatalog';

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

type HintResult = { kind: 'hint'; tiers: Record<MaterialHintTierId, MaterialHintTier> }
  | { kind: 'report-catalog'; reason: string; catalog: MaterialHintTier };

export function useWorkMaterialsHint(scope: EntryCatalogScope, line: MaterialHintLine, q: string) {
  const { siteId, date, reportId, reportVersion } = scope;
  const params = { constructionId: siteId, date, q, workTypeId: line.workTypeId || undefined,
    unitId: line.unitId || undefined, sectionId: line.sectionId ?? undefined, planItemId: line.planItemId };
  return useQuery<HintResult>({
    queryKey: ['work', 'materials-hint', params, reportId, reportVersion],
    placeholderData: keepPreviousData,
    queryFn: async ({ signal }) => {
      try {
        const { data } = await apiClient.get<Record<MaterialHintTierId, MaterialHintTier>>('/work/materials/hint', { params, signal });
        return { kind: 'hint', tiers: data };
      } catch (error) {
        if (!reportId || !isAxiosError(error) || error.response?.status !== 404) throw error;
        const { reason, items, more } = await getEntryCatalog(scope, 'materials', q, signal);
        return { kind: 'report-catalog', reason: apiError(error, 'Подсказка по стройке недоступна.'),
          catalog: { state: reason ? 'not-connected' : 'ok', reason, items, more } };
      }
    },
  });
}
