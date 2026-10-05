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
  /** Адрес экрана приложения, где эти документы перечислены (от корня); null — ссылки нет. */
  link: string | null;
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
 * Даты периода названы «наоборот»: конец раньше начала. ISO-дни сравниваются строками.
 *
 * <p>Бывает не только от опечатки: начало следующего периода задаёт граница контура, и она сдвигается
 * под открытым диалогом, когда период закрыл кто-то другой. Конец, выбранный раньше, остаётся позади.</p>
 */
export function inverted(from: string, through: string): boolean {
  return !!from && !!through && from > through;
}

/**
 * Можно ли спрашивать перечень закрытия: обе даты названы и не «наоборот».
 *
 * <p>Выведено из {@link inverted}, а не записано второй формулой: диалог по `inverted` объясняет, почему
 * перечня нет, и разойдись они — запрос был бы выключен молча, под вечным «Считаем…».</p>
 */
export function canPreviewClosing(from: string, through: string): boolean {
  return !!from && !!through && !inverted(from, through);
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
    // Перечень за период «наоборот» не спрашиваем: сервер на него только откажет. Инвариант стоит ЗДЕСЬ,
    // а не в обработчике кнопки: начало периода сдвигается под открытым диалогом и без его участия —
    // чужим закрытием, перечитыванием состояния по фокусу окна (ревью PR #1212). Что даты перепутаны,
    // говорит диалог.
    enabled: canPreviewClosing(from, through),
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
    // После УСПЕХА сброс не ждём: mutateAsync отдаёт управление сразу, и диалог закрывается РАНЬШЕ, чем
    // перечитается состояние контура. Иначе он простоял бы открытым со сдвинувшейся границей — начало
    // следующего периода позже выбранного конца — и собственное удавшееся закрытие выглядело бы чужим:
    // запросом перечня «наоборот» (ревью PR #1212) или красным «период уже закрыт» (ревью PR #1213).
    // ⚠️ Перечень при этом не перечитываем вовсе: в момент сброса диалог ещё смонтирован, его запрос
    // активен, и за только что закрытые дни сервер ответил бы отказом «уже закрыт» (ревью PR #1201).
    //
    // После ОТКАЗА — наоборот: перечитывается всё, и mutateAsync этого дожидается. 409 «данные
    // изменились» означает, что перечень на экране устарел, и сообщение об отказе появляется уже рядом со
    // свежим. Но только пока граница на месте: если её сдвинули, у перечня сменится начало, а с ним и
    // ключ запроса — тот запрос уйдёт уже после, и ждать его здесь нечем. Прежний же ключ получит отказ
    // «уже закрыт»; диалог его не показывает, пока закрытие не досчитано (см. ClosingDialog).
    onSettled: (_data, error) => {
      const reread = qc.invalidateQueries({
        queryKey: KEY,
        predicate: q => !!error || q.queryKey[1] !== CLOSING,
      });
      return error ? reread : undefined;
    },
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
