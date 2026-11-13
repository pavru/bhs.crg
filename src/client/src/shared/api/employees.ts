import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { ACCESS_KEY } from './access';
import { directoryMissingOf as directoryMissingRefusalOf } from './directoryMissing';
import type { CommonDataEntry } from './types';

/**
 * Справочник сотрудников и связь «учётная запись → сотрудник» (ТЗ CORE-7, WORK-47; change
 * `work-01-employee-account`). Всё, что показывает экран о связи, считает СЕРВЕР: состояние, пометку
 * «уволен, но связан», предложение по ФИО. Клиент ничего из этого не выводит из реквизитов — догадка
 * на экране и правило на сервере разошлись бы при первой же правке одного из них.
 */

/** Состояние связи у сотрудника — из таблицы связей ядра, а не из поля «УчётнаяЗапись» карточки. */
export interface EmployeeAccount {
  state: 'linked' | 'none' | 'deleted';
  /** Имя учётной записи: у связанной — живое, у удалённой — снимок на момент связывания. */
  name: string | null;
  login: string | null;
  /** «Уволен с», когда связь действует, а дата наступила: пометка «уволен, но связан». */
  dismissedOn: string | null;
}

export interface Employee extends CommonDataEntry {
  account: EmployeeAccount;
}

/** Почему справочника нет: без «Персоны» «Завести» не поможет, с ней — поможет. */
export type DirectoryMissingReason = 'person-missing' | 'not-created';

export interface DirectoryMissing {
  reason: DirectoryMissingReason;
  error: string;
}

/**
 * Отказ «справочник не заведён» — общим разбором (`directoryMissing.ts`), с причиной, суженной до двух
 * значений этой двери: экран по ней решает, предлагать ли «Завести».
 */
export function directoryMissingOf(e: unknown): DirectoryMissing | null {
  const missing = directoryMissingRefusalOf(e, 'Справочник сотрудников не заведён.');
  if (!missing) return null;
  return { reason: missing.reason === 'person-missing' ? 'person-missing' : 'not-created', error: missing.error };
}

export const EMPLOYEES_KEY = ['employees'] as const;

/**
 * Список сотрудников — показ: архивные на месте, с признаком. Отказ `directory-missing` — не ошибка
 * загрузки, а состояние экрана, поэтому он приходит значением, а не исключением.
 */
export function useEmployees() {
  return useQuery({
    queryKey: [...EMPLOYEES_KEY, 'list'],
    queryFn: async (): Promise<{ items: Employee[]; missing: null } | { items: null; missing: DirectoryMissing }> => {
      try {
        return { items: (await apiClient.get<Employee[]>('/employees')).data, missing: null };
      } catch (e) {
        const missing = directoryMissingOf(e);
        if (missing) return { items: null, missing };
        throw e;
      }
    },
  });
}

/** Карточка целиком — по ней строится блок связи и форма правки. */
export function useEmployee(id: string | undefined) {
  return useQuery({
    queryKey: [...EMPLOYEES_KEY, 'by-id', id],
    queryFn: () => apiClient.get<Employee>(`/employees/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

/** Сбросить всё прочитанное о сотрудниках и доступ: связь меняет и строку «Сотрудник: …» в меню. */
function useInvalidateEmployees() {
  const qc = useQueryClient();
  return () => Promise.all([
    qc.invalidateQueries({ queryKey: EMPLOYEES_KEY }),
    qc.invalidateQueries({ queryKey: ACCESS_KEY }),
  ]);
}

/** Связать с учётной записью. Версия — версия КАРТОЧКИ: связывание её двигает. */
export function useLinkAccount() {
  const invalidate = useInvalidateEmployees();
  return useMutation({
    mutationFn: ({ id, userId, version }: { id: string; userId: string; version: string }) =>
      apiClient.put<Employee>(`/employees/${id}/account`, { userId }, { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: () => invalidate(),
  });
}

export function useUnlinkAccount() {
  const invalidate = useInvalidateEmployees();
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version: string }) =>
      apiClient.delete<Employee>(`/employees/${id}/account`, { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: () => invalidate(),
  });
}

/** Учётная запись — кандидат на связь: имя, логин и с кем уже связана. Ролей и почты адрес не отдаёт. */
export interface AccountCandidate {
  userId: string;
  name: string;
  login: string;
  linkedTo: { id: string; name: string } | null;
}

export function useAccountCandidates(q: string, enabled: boolean) {
  return useQuery({
    queryKey: [...EMPLOYEES_KEY, 'candidates', q],
    queryFn: () => apiClient.get<{ items: AccountCandidate[]; more: boolean }>('/employees/account-candidates', { params: { q } })
      .then(r => r.data),
    enabled,
    placeholderData: prev => prev,
  });
}

/** Строка режима «Сопоставить»: сотрудник без связи и предложенная сервером учётная запись. */
export interface AccountMatch {
  employee: { id: string; name: string; version: string };
  account: EmployeeAccount;
  suggested: { userId: string; name: string; login: string } | null;
  reason: 'no-match' | 'ambiguous' | null;
}

export function useAccountMatches(enabled: boolean) {
  return useQuery({
    queryKey: [...EMPLOYEES_KEY, 'matches'],
    queryFn: () => apiClient.get<AccountMatch[]>('/employees/account-matches').then(r => r.data),
    enabled,
    // Окно собирается по свежему ответу: предложения по устаревшему списку дали бы «занята».
    refetchOnMount: 'always',
  });
}

/** Итог строки пачки: код для экрана и слова сервера для человека. */
export interface LinkRowResult {
  employeeId: string;
  outcome: 'linked' | 'not-found' | 'stale' | 'archived' | 'account-gone' | 'taken' | 'employee-linked' | string;
  error: string | null;
  linkedTo: { id: string; name: string } | null;
  accountName: string | null;
  version: string | null;
}

export interface LinkBatchResult {
  linked: number;
  total: number;
  results: LinkRowResult[];
}

export function useLinkAccounts() {
  const invalidate = useInvalidateEmployees();
  return useMutation({
    mutationFn: (links: { employeeId: string; userId: string; version: string }[]) =>
      apiClient.post<LinkBatchResult>('/employees/account-links', { links }).then(r => r.data),
    onSuccess: () => invalidate(),
  });
}

/** «Завести» справочник: тип «Сотрудник» из скелета ядра, без перезапуска сервера. */
export function useCreateEmployeeDirectory() {
  const invalidate = useInvalidateEmployees();
  return useMutation({
    mutationFn: () => apiClient.post<{ typeId: string; created: boolean }>('/employees/directory').then(r => r.data),
    onSuccess: () => invalidate(),
  });
}
