import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { EnumOptionDef, EnumTypeDef } from './types';

const QK = 'enumTypes';

export function useListEnumTypes() {
  return useQuery<EnumTypeDef[]>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/enum-types').then(r => r.data),
  });
}

interface SaveEnumTypeDto {
  name: string;
  code: string;
  description?: string;
  values: string;
}

function toDto(
  name: string,
  code: string,
  description: string | undefined,
  values: EnumOptionDef[],
): SaveEnumTypeDto {
  return { name, code, description: description || undefined, values: JSON.stringify(values) };
}

export function useCreateEnumType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: SaveEnumTypeDto) =>
      apiClient.post<EnumTypeDef>('/enum-types', dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useUpdateEnumType(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: SaveEnumTypeDto) =>
      apiClient.put<EnumTypeDef>(`/enum-types/${id}`, dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useSetEnumTypeGroup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, group }: { id: string; group: string | null }) =>
      apiClient.put<EnumTypeDef>(`/enum-types/${id}/group`, { group }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useDeleteEnumType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/enum-types/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export { toDto as buildEnumTypeDto };
