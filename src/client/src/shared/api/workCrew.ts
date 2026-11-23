import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CrewGroup, CrewGroupsPage } from './work';

export interface CrewGroupsFilters {
  constructionId?: string;
  from?: string;
  to?: string;
}

export const CREW_GROUPS_KEY = ['work', 'crew-groups'] as const;
const path = '/work/crew-groups';

export function useCrewGroups(params: CrewGroupsFilters) {
  return useQuery<CrewGroupsPage>({
    queryKey: [...CREW_GROUPS_KEY, params],
    queryFn: ({ signal }) => apiClient.get<CrewGroupsPage>(path, { params, signal }).then(response => response.data),
    placeholderData: keepPreviousData,
  });
}

export function approveCrewGroup(id: string, version: string, total: number) {
  return apiClient.post<CrewGroup>(`${path}/${encodeURIComponent(id)}/approve`, { total },
    { headers: { 'If-Match': version } }).then(response => response.data);
}
