import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Учётный период (ТЗ CORE-35, issue #1081): границы закрытия, закрытие и отмена.
 *
 * Границы приходят с сервера ДЕЙСТВУЮЩИМИ и посчитанными: у стройки без своих закрытий —
 * граница компании, начало следующего периода и «что отменит отмена» — готовыми полями. Считать
 * их здесь нельзя: вторая формула разошлась бы с серверной ровно на границе месяца.
 */

export interface PeriodContourState {
  /** Стройка; у компании — null. */
  constructionId: string | null;
  /** Последний закрытый день, ISO. У стройки — позднейшая из своей границы и границы компании. */
  closedThrough: string | null;
  /** Собственная граница контура — по ней видно, чьим закрытием он закрыт. */
  ownClosedThrough: string | null;
  /** С какого дня начнётся следующее закрытие; null — закрытий не было, начало называет человек. */
  expectedFrom: string | null;
  /** Закрытие, которое отменит отмена; null — отменять нечего. */
  reopenable: {
    from: string;
    through: string;
    /** Стройки, которым отмена этих дней НЕ откроет: они закрыты своим закрытием. Только у компании. */
    keptClosed: string[];
  } | null;
}

export interface Periods {
  /** «Сегодня» по часам компании: закрыть можно только дни раньше него. */
  today: string;
  company: PeriodContourState;
  constructions: PeriodContourState[];
}

export interface PeriodClosureRecord {
  id: string;
  kind: 'Close' | 'Reopen';
  contour: 'Company' | 'Construction';
  constructionId: string | null;
  from: string;
  through: string;
  at: string;
  byName: string;
  reason: string | null;
}

const KEY = ['periods'] as const;

export function usePeriods() {
  return useQuery({
    queryKey: KEY,
    queryFn: () => apiClient.get<Periods>('/periods').then(r => r.data),
  });
}

export function usePeriodHistory() {
  return useQuery({
    queryKey: [...KEY, 'history'],
    queryFn: () => apiClient.get<PeriodClosureRecord[]>('/periods/history').then(r => r.data),
  });
}

/** Граница, которую видел человек, — в том виде, в каком её ждёт сервер (ifMatch). */
function seen(state: PeriodContourState): string {
  return state.closedThrough ?? 'none';
}

function contourOf(state: PeriodContourState) {
  return state.constructionId
    ? { contour: 'construction', constructionId: state.constructionId }
    : { contour: 'company' };
}

export function useClosePeriod() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { state: PeriodContourState; from: string; through: string }) =>
      apiClient.post('/periods/close', { ...contourOf(v.state), from: v.from, through: v.through, ifMatch: seen(v.state) }),
    // И при отказе тоже: 409 «границу тем временем изменили» означает, что на экране устаревшее.
    onSettled: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useReopenPeriod() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { state: PeriodContourState; reason: string }) =>
      apiClient.post('/periods/reopen', { ...contourOf(v.state), reason: v.reason, ifMatch: seen(v.state) }),
    onSettled: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}
