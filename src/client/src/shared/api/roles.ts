import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { ACCESS_KEY } from './access';

/**
 * Роли и их состав прав (issue #951, ТЗ AUTH-5).
 *
 * Список ролей — единственный источник и для редактора, и для назначения роли пользователю:
 * перечень имён на клиенте означал бы, что заведённую администратором роль назначить нечем.
 */

const QK = 'roles';

export interface AppRole {
  /** Техническое имя. Им роль назначают; человеку оно не показывается. */
  name: string;
  title: string;
  summary: string | null;
  /** Системная: состав прав править можно, удалить и переименовать — нет. */
  system: boolean;
  /** Состав прав правил администратор — с этого момента код им не распоряжается. */
  edited: boolean;
  permissions: string[];
  /** Сколько людей носит роль: снятие права затронет ровно их. */
  users: number;
}

export interface PermissionInfo {
  code: string;
  /** Что право даёт. */
  gives: string;
  /** К каким данным открывает доступ — отдельный вопрос от «что даёт». */
  opens: string;
  /** С какими правами обычно выдаётся вместе. Подсказка, а не зависимость. */
  usuallyWith: string[];
}

export interface PermissionGroup {
  /** Код модуля, «core» — ядро, «*» — составные права. */
  module: string;
  title: string;
  permissions: PermissionInfo[];
}

export function useRoles() {
  return useQuery<AppRole[]>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/roles').then(r => r.data),
  });
}

/** Справочник прав с объяснениями. Меняется только вместе с составом модулей. */
export function usePermissionCatalog() {
  return useQuery<PermissionGroup[]>({
    queryKey: [QK, 'permissions'],
    queryFn: () => apiClient.get('/roles/permissions').then(r => r.data),
    staleTime: Infinity,
  });
}

export function useCreateRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: { title: string; summary?: string; permissions: string[] }) =>
      apiClient.post<AppRole>('/roles', dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useRenameRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, title, summary }: { name: string; title: string; summary?: string }) =>
      apiClient.put<AppRole>(`/roles/${name}`, { title, summary }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useSetRolePermissions() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, permissions }: { name: string; permissions: string[] }) =>
      apiClient.put<AppRole>(`/roles/${name}/permissions`, { permissions }).then(r => r.data),
    // Правка действует немедленно у всех носителей роли (AUTH-5.1) — в том числе у того, кто её
    // правит: свои права он мог только что и сменить, а по ним строится меню.
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: [QK] });
      qc.invalidateQueries({ queryKey: ACCESS_KEY });
      qc.invalidateQueries({ queryKey: ['users'] });
    },
  });
}

export function useDeleteRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => apiClient.delete(`/roles/${name}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}
