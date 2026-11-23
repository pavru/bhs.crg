import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

export interface EntryCatalogScope {
  siteId: string;
  date: string;
  reportId?: string;
  reportVersion?: string;
}

export interface WorkVariantOption { id: string; name: string; archived: boolean }

export interface WorkTypeOption {
  id: string;
  displayName: string;
  unitId: string | null;
  unitName: string | null;
  archived: boolean;
  requiresLocation: boolean;
  requiresMaterials: boolean;
  variants: WorkVariantOption[];
}

export interface MaterialOption { id: string; displayName: string | null; archived: boolean }
export const materialName = (item: MaterialOption) => `${item.displayName || 'Материал без названия'}${item.archived ? ' · в архиве' : ''}`;
export interface UnitOption { id: string; name: string; archived: boolean }
export interface SectionOption { id: string; name: string }

export interface EntryCatalogItem {
  'work-types': WorkTypeOption;
  units: UnitOption;
  materials: MaterialOption;
  sections: SectionOption;
}

export interface EntryCatalogPage<T> {
  items: T[];
  selected?: T[];
  reason?: string | null;
  more?: boolean;
  limit?: number;
}

export function catalogLabels<T extends { id: string }>(data?: EntryCatalogPage<T>) {
  return new Map([...(data?.items ?? []), ...(data?.selected ?? [])].map(item => [item.id, item]));
}

export function useEntryCatalog<K extends keyof EntryCatalogItem>(
  { siteId, date, reportId, reportVersion }: EntryCatalogScope, kind: K, q = '', enabled = true,
) {
  return useQuery({
    queryKey: ['work', 'entry-catalog', siteId, kind, date, reportId, reportVersion, q],
    enabled: enabled && !!siteId && (!!date || !!reportId),
    queryFn: ({ signal }) => apiClient.get<EntryCatalogPage<EntryCatalogItem[K]>>(
      `/work/entry/sites/${encodeURIComponent(siteId)}/catalog/${kind}`, { params: { date, reportId, q }, signal },
    ).then(response => response.data),
  });
}

export function useCrewCandidates({ siteId, date }: EntryCatalogScope, enabled: boolean) {
  return useQuery({
    queryKey: ['work', 'crew-candidates', siteId, date],
    enabled: enabled && !!siteId && !!date,
    queryFn: ({ signal }) => apiClient.get<{ items: { id: string; fullName: string }[]; reason: 'not-assigned' | null }>(
      `/work/constructions/${encodeURIComponent(siteId)}/crew-candidates`, { params: { date }, signal },
    ).then(response => response.data),
  });
}
