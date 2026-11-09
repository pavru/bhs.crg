import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Распределение редакции сметы по разделам стройки (change `plan-10-distribution`, ТЗ PLAN-5.1): правила разделов
 * ЛСР, части строк, итог по целям и отчёт переноса — одним ответом.
 *
 * Версия — СВОЯ, распределения (`version`, «0» — ещё не распределяли), с версией редакции и сметы не смешивается.
 * Её называет каждая запись заголовком `If-Match`; ответ записи несёт новое распределение целиком, перечитывать не
 * нужно. Счётчики, итоги и статусы строк считает сервер (D17 зонтика `plan-00-epic`) — экран их только показывает.
 */

/** Количество в обеих единицах: строки, как в файле («100 м»), и позиции плана («м»); у несопоставленной — только первое. */
export interface Quantity {
  row: number;
  plan: number | null;
}

/** Цель: раздел стройки или «стройка в целом». */
export type DistributionTarget = { kind: 'whole'; sectionId?: null } | { kind: 'section'; sectionId: string };

export interface DistributionPart {
  sectionId: string;
  quantity: Quantity;
}

/** Счётчики распределения редакции: `distributed + partial + whole + undistributed = rows`. */
export interface DistributionCounts {
  rows: number;
  distributed: number;
  partial: number;
  whole: number;
  undistributed: number;
  /** Позиций отчёта переноса, ждущих решения. */
  carryOverPending: number;
  changedAt: string | null;
}

export interface DistributionSectionTotal {
  sectionId: string;
  /** `null` — раздела в стройке больше нет. */
  name: string | null;
  /** Раздел удалён, а распределение на него есть — тоном warning. */
  deleted: boolean;
  rows: number;
}

export interface DistributionRule {
  /** Номер главы в файле; `null` — строки вне разделов ЛСР. */
  lsrSectionCode: string | null;
  lsrSectionName: string | null;
  /** `null` — правила нет: «не распределено». */
  target: DistributionTarget | null;
  rows: number;
  /** Строк со своими частями. */
  exceptions: number;
  undistributed: number;
}

export type LineSource = 'rule' | 'parts' | 'none';
export type LineStatus = 'distributed' | 'partial' | 'whole' | 'undistributed';

export interface DistributionLine {
  rowId: string;
  order: number;
  lsrSectionCode: string | null;
  number: string;
  name: string;
  kind: 'work' | 'material';
  subordinate: boolean;
  source: LineSource;
  status: LineStatus;
  /** Части задать можно; нет — `splitReason`, а правилом раздела строка распределяется всё равно. */
  splittable: boolean;
  splitReason?: 'no-volume';
  volume: Quantity | null;
  units: { row: string | null; plan: string | null };
  parts: DistributionPart[];
  /** Остаток «стройка в целом» у строки с частями. */
  rest: Quantity | null;
  /** Разделы стройки строки — для отбора по цели. */
  targets: string[];
  /** Часть объёма идёт в «стройку в целом». */
  whole: boolean;
}

export type CarryOverCategory = 'shifted' | 'ambiguous' | 'overflow' | 'lost';

export interface CarryOverItem {
  id: string;
  category: CarryOverCategory;
  subject: 'row' | 'rule';
  /** Ждёт решения; `shifted` не ждёт. */
  pending: boolean;
  lsrSectionCode: string | null;
  lsrSectionName: string | null;
  rateCode: string | null;
  numberInSection: string | null;
  number: string | null;
  name: string | null;
  sourceVolume: number | null;
  sourceUnit: string | null;
  /** Прежние части строки источника — в его единице строки. */
  parts: DistributionPart[];
  /** Цель правила источника — у позиции-правила. */
  target: DistributionTarget | null;
  /** Строка новой редакции (`shifted`, `overflow`). */
  rowId: string | null;
  newVolume: number | null;
  /** Строки-кандидаты — у `ambiguous`. */
  candidates: string[];
}

export interface CarryOverCountsInfo {
  sourceRevisionId: string | null;
  sourceNumber: number | null;
  carried: number;
  shifted: number;
  ambiguous: number;
  overflow: number;
  lost: number;
}

export interface CarryOver {
  counts: CarryOverCountsInfo;
  pending: number;
  /** Нерешённые позиции. */
  items: CarryOverItem[];
}

export interface Distribution {
  revisionId: string;
  /** `false` — распределять нечего: смета на разделе стройки (`reason`). */
  available: boolean;
  reason?: { code: 'estimate-section'; sectionId: string; sectionName: string | null };
  version: string;
  changedAt: string | null;
  changedBy: string | null;
  current: boolean;
  frozen: boolean;
  counts: DistributionCounts;
  targets: { sections: DistributionSectionTotal[]; wholeRows: number; undistributedRows: number };
  rules: DistributionRule[];
  lines: DistributionLine[];
  carryOver: CarryOver | null;
}

/** Строка набора: единица одна на строку; `parts: null` — «к правилу раздела». */
export interface LineWrite {
  rowId: string;
  unit: 'row' | 'plan';
  parts: { sectionId: string; quantity: number }[] | null;
}

/** Прежнее состояние изменённого — им делается «Отменить» одной записью. */
export interface DistributionPrevious {
  rule?: { lsrSectionCode: string | null; target: DistributionTarget | null };
  lines: LineWrite[];
}

export interface DistributionWrite {
  version: string;
  previous: DistributionPrevious;
  distribution: Distribution;
}

export interface RuleWrite {
  lsrSectionCode: string | null;
  target: DistributionTarget | null;
  resetExceptions?: boolean;
  lines?: LineWrite[];
}

export type CarryOverDecision = { action: 'apply'; rowId: string } | { action: 'skip' } | { action: 'acknowledge' };

const key = (revisionId: string) => ['planning', 'revisions', revisionId, 'distribution'] as const;

export function useDistribution(revisionId: string, enabled = true) {
  return useQuery({
    queryKey: key(revisionId),
    queryFn: () => apiClient.get<Distribution>(`/plan/revisions/${revisionId}/distribution`).then(r => r.data),
    enabled,
  });
}

/**
 * Запись распределения. Ответ кладётся в кэш как есть — он и есть новое распределение; смета перечитывается ради
 * полосы «распределено N из M» у редакции. Отказ кэш не трогает: набранное в окне остаётся, а перечитать
 * распределение человек решает сам («перечитайте» у 409).
 */
function useDistributionWrite<T>(revisionId: string, send: (body: T, version: string) => Promise<DistributionWrite>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ body, version }: { body: T; version: string }) => send(body, version),
    onSuccess: done => {
      qc.setQueryData(key(revisionId), done.distribution);
      void qc.invalidateQueries({ queryKey: ['planning', 'estimates'] });
    },
  });
}

const ifMatch = (version: string) => ({ headers: { 'If-Match': version } });

export function useWriteRule(revisionId: string) {
  return useDistributionWrite<RuleWrite>(revisionId, (body, version) =>
    apiClient.put<DistributionWrite>(`/plan/revisions/${revisionId}/distribution/rules`, body, ifMatch(version)).then(r => r.data));
}

export function useWriteLines(revisionId: string) {
  return useDistributionWrite<LineWrite[]>(revisionId, (lines, version) =>
    apiClient.put<DistributionWrite>(`/plan/revisions/${revisionId}/distribution/lines`, { lines }, ifMatch(version)).then(r => r.data));
}

export function useDecideCarryOver(revisionId: string) {
  return useDistributionWrite<{ itemId: string; decision: CarryOverDecision }>(revisionId, ({ itemId, decision }, version) =>
    apiClient.post<DistributionWrite>(`/plan/revisions/${revisionId}/distribution/carry-over/${itemId}`, decision, ifMatch(version))
      .then(r => r.data));
}

/** Версия распределения в кэше сейчас — для записи, которую человек делает позже ответа (снэкбар «Отменить»). */
export function useDistributionVersion(revisionId: string) {
  const qc = useQueryClient();
  return () => qc.getQueryData<Distribution>(key(revisionId))?.version;
}

/** Перечитать распределение — действие «Перечитать» у 409. */
export function useRereadDistribution(revisionId: string) {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: key(revisionId) });
}

/** Строка, у которой части не сходятся: сетка и окно подсвечивают её, текст называет обе единицы. */
export interface BalanceError {
  rowId: string;
  number: string;
  name: string;
  /** `over`, `no-volume`, `not-positive`, `too-small`, `not-matched`, `section-twice`. */
  code: string;
  sum: Quantity;
  volume: Quantity | null;
  unit: 'row' | 'plan';
  text: string;
}

/** Отказ записи распределения, разобранный для показа. */
export type DistributionRefusal =
  | { kind: 'balance'; text: string; errors: BalanceError[] }
  | { kind: 'estimate-section'; text: string; sectionName: string | null }
  | { kind: 'conflict'; text: string; changedBy: string | null; changedAt: string | null }
  | { kind: 'invalid' | 'forbidden' | 'gone' | 'failed'; text: string };

const CONFLICT_TEXT = 'Распределение тем временем изменили — перечитайте его и повторите: вы видели прежнее состояние.';

/**
 * Отказ сервера распределения словами (ТЗ PLAN-5.1, COST-13). Текст сервера — первым: он называет, что не так.
 * Поля отказа — по `code`, а не по фразе: строки баланса для подсветки, кто и когда изменил — для «перечитайте».
 * Без тела (403 от ворот права) — своя формулировка того же смысла, а не «status code 403».
 */
export function distributionRefusal(e: unknown): DistributionRefusal {
  const err = e as { response?: { status?: number; data?: unknown }; message?: string };
  const status = err?.response?.status;
  const data = err?.response?.data;
  const body = typeof data === 'object' && data !== null ? data as Record<string, unknown> : {};
  const server = (typeof data === 'string' ? data.trim() : typeof body.error === 'string' ? body.error : '') || '';
  if (status === 400 && body.code === 'balance' && Array.isArray(body.errors))
    return { kind: 'balance', text: server || 'Части строк не сходятся с объёмом — не записано ничего.', errors: body.errors as BalanceError[] };
  if (status === 400 && body.code === 'estimate-section')
    return { kind: 'estimate-section', text: server || 'Смета составлена на раздел стройки — распределять её не нужно.',
      sectionName: typeof body.sectionName === 'string' ? body.sectionName : null };
  switch (status) {
    case 400: return { kind: 'invalid', text: server || 'Сервер не принял запрос.' };
    case 403: return { kind: 'forbidden', text: server || 'Нет права править сметы — его выдаёт администратор.' };
    case 404: return { kind: 'gone', text: server || 'Редакцию удалили, пока она была открыта.' };
    case 409: return {
      kind: 'conflict', text: server || CONFLICT_TEXT,
      changedBy: typeof body.changedBy === 'string' ? body.changedBy : null,
      changedAt: typeof body.changedAt === 'string' ? body.changedAt : null,
    };
    default: return { kind: 'failed', text: server || err?.message || 'Не получилось — повторите.' };
  }
}
