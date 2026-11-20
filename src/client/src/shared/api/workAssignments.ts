import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { directoryMissingOf } from './directoryMissing';
import type { AssignmentCandidatesPage } from './work';

/**
 * Назначения стройки (change `work-12-assignments-ui`, ТЗ WORK-21, WORK-46) — запросы к адресам `work-11`.
 *
 * Носители панели — поля ответа, клиент их не вычисляет (D15 зонтика): можно ли назначать на стройке
 * (`construction.canAssign` и `reason`), действует ли назначение (`active`, по поясу стройки), можно ли его
 * изменить или снять (`canChange`, `canRemove` с причинами), «сегодня» стройки (`construction.today`). Правка и
 * снятие отправляют версию строки из ответа чтения заголовком `If-Match`; заведение версии не называет —
 * исключение `work-11`.
 */

export type AssignmentKind = 'installer' | 'manager';

export const KIND_LABEL: Record<AssignmentKind, string> = { installer: 'монтажник', manager: 'менеджер' };

/** Назначение стройки — строка `GET /api/work/assignments`. */
export interface Assignment {
  id: string;
  employeeId: string;
  employeeName: string;
  personnelNumber: string | null;
  constructionId: string;
  kind: AssignmentKind;
  dateFrom: string;
  /** `null` — без срока (только у менеджера). */
  dateTo: string | null;
  /** Действует сегодня по поясу стройки. */
  active: boolean;
  /** Значение `If-Match` правки и снятия. */
  version: string;
  canChange: boolean;
  changeReason: string | null;
  /** «Снять» у последнего менеджера стройки сервер запрещает здесь же. */
  canRemove: boolean;
  removeReason: string | null;
}

/** Стройка в ответе чтения назначений: можно ли на ней назначать спрашивающему и почему нет. */
export interface AssignmentSite {
  id: string;
  name: string;
  timeZoneId: string;
  timeZoneInherited: boolean;
  /** Сегодня по поясу стройки; `null` — пояс не определён. */
  today: string | null;
  canAssign: boolean;
  reason: string | null;
}

export interface AssignmentsPage {
  construction: AssignmentSite;
  /** Порядок — сервера: менеджеры первыми, дальше по ФИО и началу. */
  items: Assignment[];
}

/** Отказавший в пачке или одиночном заведении — поимённо, с причиной словами. */
export interface RefusedEmployee {
  employeeId: string;
  name: string;
  reason: string;
}

/**
 * Отказ адреса назначений, разобранный для показа. Текст — всегда сервера (`error`); вид — по КОДУ ответа и
 * его полям: по нему окно решает, оставаться ли открытым, предлагать ли перечитать и у кого рисовать причину.
 *
 * - `refused` — 422: заведение или правка не прошли по сотруднику; `refused` называет каждого отказавшего.
 * - `missing` — 409 `directory-missing`: справочник сотрудников не заведён.
 * - `conflict` — прочий 409: назначение изменили под окном, период пересекается с другим, на стройке не
 *   останется менеджера. Различает их только текст сервера, и выход у всех один — перечитать.
 * - `forbidden` — 403: назначать на этой стройке может её менеджер.
 * - `other` — 400, 404 и прочее: текст сервера как есть.
 */
export type AssignmentRefusal =
  | { kind: 'refused'; error: string; refused: RefusedEmployee[] }
  | { kind: 'missing'; error: string }
  | { kind: 'conflict'; error: string }
  | { kind: 'forbidden'; error: string }
  | { kind: 'other'; error: string };

const FORBIDDEN = 'Назначать на этой стройке может её менеджер или пользователь с правом «на любых стройках».';

/**
 * Текст отказа — только из тела ответа. Без тела `apiError` отдал бы сообщение axios («Request failed with status
 * code 403»), а человеку нужна фраза о стройке.
 */
function said(e: unknown, fallback: string): string {
  const data = (e as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  const error = (data as { error?: unknown } | null | undefined)?.error;
  return typeof error === 'string' && error.trim() ? error : fallback;
}

export function assignmentRefusalOf(e: unknown): AssignmentRefusal {
  const response = (e as { response?: { status?: number; data?: { refused?: unknown } } })?.response;
  const missing = directoryMissingOf(e, 'Справочник «Сотрудник» не заведён.');
  if (missing) return { kind: 'missing', error: missing.error };
  switch (response?.status) {
    case 422: {
      const refused = Array.isArray(response.data?.refused) ? (response.data.refused as RefusedEmployee[]) : [];
      return { kind: 'refused', error: said(e, 'Назначение не заведено.'), refused };
    }
    case 409: return { kind: 'conflict', error: said(e, 'Назначение тем временем изменили — перечитайте.') };
    case 403: return { kind: 'forbidden', error: said(e, FORBIDDEN) };
    default: return { kind: 'other', error: said(e, 'Назначение не записано.') };
  }
}

export const ASSIGNMENTS_KEY = ['work', 'assignments'] as const;

const listKey = (constructionId: string) => [...ASSIGNMENTS_KEY, 'list', constructionId] as const;

/**
 * Назначения стройки. Отказ «справочник не заведён» — не ошибка загрузки, а состояние панели: приходит
 * значением, как у списка сотрудников.
 */
export function useAssignments(constructionId: string) {
  return useQuery({
    queryKey: listKey(constructionId),
    queryFn: async (): Promise<{ page: AssignmentsPage; missing: null } | { page: null; missing: string }> => {
      try {
        const page = (await apiClient.get<AssignmentsPage>('/work/assignments', { params: { constructionId } })).data;
        return { page, missing: null };
      } catch (e) {
        const refusal = assignmentRefusalOf(e);
        if (refusal.kind === 'missing') return { page: null, missing: refusal.error };
        throw e;
      }
    },
  });
}

export interface CandidatesQuery {
  constructionId: string;
  kind: AssignmentKind;
  dateFrom: string;
  /** Пусто — без срока (только у менеджера). */
  dateTo: string;
  q: string;
}

/** Задан ли период настолько, что сервер подберёт кандидатов: монтажнику нужен и конец. */
export function periodComplete(kind: AssignmentKind, dateFrom: string, dateTo: string): boolean {
  if (!dateFrom) return false;
  if (!dateTo) return kind === 'manager';
  return dateTo >= dateFrom;
}

/**
 * Кандидаты под стройку, вид и период: годность и причину исключения считает сервер теми же правилами, что и
 * заведение. Пока период не задан — не спрашивает. Прежняя выдача держится на время нового запроса: смена
 * периода не опустошает список под рукой.
 */
export function useAssignmentCandidates(query: CandidatesQuery, enabled: boolean) {
  const ready = enabled && periodComplete(query.kind, query.dateFrom, query.dateTo);
  return useQuery({
    queryKey: [...ASSIGNMENTS_KEY, 'candidates', query],
    queryFn: async (): Promise<{ page: AssignmentCandidatesPage; missing: null } | { page: null; missing: string }> => {
      try {
        const params = {
          constructionId: query.constructionId, kind: query.kind, dateFrom: query.dateFrom,
          dateTo: query.dateTo || undefined, q: query.q.trim() || undefined,
        };
        const page = (await apiClient.get<AssignmentCandidatesPage>('/work/assignments/candidates', { params })).data;
        return { page, missing: null };
      } catch (e) {
        const refusal = assignmentRefusalOf(e);
        if (refusal.kind === 'missing') return { page: null, missing: refusal.error };
        throw e;
      }
    },
    enabled: ready,
    placeholderData: keepPreviousData,
  });
}

/** Перечитать назначения: правка одного назначения двигает признаки соседей («последний менеджер»). */
function useInvalidateAssignments() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ASSIGNMENTS_KEY });
}

export interface BulkAssign {
  constructionId: string;
  kind: AssignmentKind;
  dateFrom: string;
  dateTo: string | null;
  employeeIds: string[];
}

/** Пачка «всё или ничего»: 422 называет отказавших, и не заведено ни одного назначения. */
export function useBulkAssign() {
  const invalidate = useInvalidateAssignments();
  return useMutation({
    mutationFn: (body: BulkAssign) =>
      apiClient.post<{ items: Assignment[] }>('/work/assignments/bulk', body).then(r => r.data),
    onSuccess: () => invalidate(),
  });
}

/** Правка периода. Сотрудник, стройка и вид правкой не меняются. */
export function useChangeAssignment() {
  const invalidate = useInvalidateAssignments();
  return useMutation({
    mutationFn: ({ id, version, dateFrom, dateTo }: { id: string; version: string; dateFrom: string; dateTo: string | null }) =>
      apiClient.put<Assignment>(`/work/assignments/${id}`, { dateFrom, dateTo }, { headers: { 'If-Match': version } })
        .then(r => r.data),
    onSuccess: () => invalidate(),
  });
}

export function useRemoveAssignment() {
  const invalidate = useInvalidateAssignments();
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version: string }) =>
      apiClient.delete(`/work/assignments/${id}`, { headers: { 'If-Match': version } }),
    onSuccess: () => invalidate(),
  });
}
