import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { FieldConstraints, PrimitiveTypeDef } from './types';

const QK = 'primitiveTypes';

export function useListPrimitiveTypes() {
  return useQuery<PrimitiveTypeDef[]>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/primitive-types').then(r => r.data),
  });
}

interface SavePrimitiveTypeDto {
  name: string;
  code: string;
  baseType: 'string' | 'number' | 'date';
  description?: string;
  constraints: string;
}

function toDto(
  name: string,
  code: string,
  baseType: 'string' | 'number' | 'date',
  description: string | undefined,
  constraints: FieldConstraints,
): SavePrimitiveTypeDto {
  return { name, code, baseType, description: description || undefined, constraints: JSON.stringify(constraints) };
}

export function useCreatePrimitiveType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: SavePrimitiveTypeDto) =>
      apiClient.post<PrimitiveTypeDef>('/primitive-types', dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useUpdatePrimitiveType(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: SavePrimitiveTypeDto) =>
      apiClient.put<PrimitiveTypeDef>(`/primitive-types/${id}`, dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useDeletePrimitiveType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/primitive-types/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export { toDto as buildPrimitiveTypeDto };
