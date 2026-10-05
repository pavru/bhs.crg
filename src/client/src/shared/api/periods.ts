import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
  /** Что показал диалог при закрытии; null — закрытие сделано до появления перечня либо это отмена. */
  report: ClosingSection[] | null;
}

/** Строка перечня диалога закрытия. Тексты — модуля: экран ядра не знает, что такое «счёт». */
export interface ClosingLine {
  key: string;
  text: string;
  count: number;
  /** Число документов словами: «3 счёта». */
  counted: string;
  /** Сумма; null — строка денег не несёт либо сумма закрыта правом (см. `amountsHidden` раздела). */
  amount: number | null;
  note: string | null;
}

/** Раздел перечня — один модуль. */
export interface ClosingSection {
  module: string;
  title: string;
  /** По какой дате модуль относит документ к периоду. */
  dateRule: string;
  unfinished: ClosingLine[];
  frozen: ClosingLine[];
  /** В разделе есть суммы, которых этому человеку не показали. */
  amountsHidden: boolean;
}

export interface ClosingPreview {
  /** Отпечаток увиденного: закрытие обязано его назвать и откажет, если данные с тех пор изменились. */
  stamp: string;
  sections: ClosingSection[];
}

const KEY = ['periods'] as const;
/** Второй сегмент ключа у перечня диалога закрытия. */
const CLOSING = 'closing';

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

/**
 * Что покажет диалог закрытия (задача E1b, issue #1099): перечень по модулям и отпечаток увиденного.
 * Всегда с сервера и без кеша между открытиями диалога: закрывают по тому, что есть сейчас.
 */
export function useClosingPreview(state: PeriodContourState, from: string, through: string) {
  return useQuery({
    queryKey: [...KEY, CLOSING, state.constructionId, from, through],
    queryFn: () => apiClient
      .post<ClosingPreview>('/periods/close/preview', { ...contourOf(state), from, through })
      .then(r => r.data),
    enabled: !!from && !!through,
    // Смена даты не гасит перечень: прежние числа стоят приглушёнными, пока не пришли новые.
    placeholderData: keepPreviousData,
    staleTime: 0,
    gcTime: 0,
    // Отказ («закрыть можно только прошедшие дни») — ответ, а не сбой сети: повторять его незачем.
    retry: false,
  });
}

export function useClosePeriod() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { state: PeriodContourState; from: string; through: string; report: string }) =>
      apiClient.post('/periods/close', {
        ...contourOf(v.state), from: v.from, through: v.through, ifMatch: seen(v.state), report: v.report,
      }),
    // И при отказе тоже: 409 «границу тем временем изменили» или «данные изменились» означает, что на
    // экране устаревшее, — перечень диалога перечитывается этим же сбросом.
    // ⚠️ А после УСПЕХА перечень не перечитываем: диалог ещё открыт (mutateAsync ждёт этот сброс), и
    // запрос перечня за только что закрытые дни получил бы отказ «уже закрыт» — человек увидел бы
    // красное «Без перечня закрыть нельзя» поверх удавшегося закрытия (ревью PR #1201).
    onSettled: (_data, error) => qc.invalidateQueries({
      queryKey: KEY,
      predicate: q => !!error || q.queryKey[1] !== CLOSING,
    }),
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
