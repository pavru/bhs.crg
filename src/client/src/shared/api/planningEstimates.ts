import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CommonDataEntry } from './types';

/**
 * Сметы модуля «Планирование» (change `plan-09-estimate-ui`, ТЗ PLAN-1, PLAN-2, PLAN-5, PLAN-5.2): сметы
 * стройки и общий список, редакции, строки и разделы ЛСР редакции, смена действующей редакции.
 *
 * ⚠️ Имя файла — `planning…`, а не `plan…`: слово «план» в клиенте занято планом документов (`plans.ts`, #796).
 *
 * Носители состояний — с сервера (D17 зонтика `plan-00-epic`): «действующая», «зафиксирована», пометки
 * строки, счётчики «не сопоставлено» и «без номенклатуры». Клиент их не вычисляет — только показывает и
 * отбирает по ним.
 */

/**
 * Часть ответа, которой нет, потому что у читающего нет права (ТЗ CORE-33, D4 зонтика). Закрытые поля не
 * приходят вовсе — ни пустыми, ни нулём, — и экран отличает «скрыто» от «нет данных» только по носителю.
 */
export interface HiddenPart {
  /** Что скрыто: `cost` — себестоимость сметы. */
  code: string;
  /** Код права, которое это открывает. */
  permission: string;
  /** То же право словами каталога прав — им экран называет причину. */
  name: string;
}

/** Пометка состояния с сервера: код — для отбора, текст — для человека. */
export interface PlanMark {
  code: string;
  text: string;
}

export interface EstimateRevision {
  id: string;
  number: number;
  /** Действующая: по ней считается план стройки (ТЗ PLAN-5.2). */
  current: boolean;
  importedAt: string;
  importedBy: string | null;
  sourceFileName: string | null;
  schemaVersion: string | null;
  rows: number;
  /** Строк-работ не сопоставлено — в план они не входят. Строки-материалы сюда не входят никогда. */
  unmatched: number;
  /** Материалов без позиции номенклатуры. */
  materialsUnmatched: number;
  /** Строк с пометкой `unit-changed`: шифр знаком, единица расценки другая. */
  unitChanged: number;
  /** Импорт, из которого редакция создана; `null` — не импортом. */
  importId: string | null;
  /** Создана из неполного разбора: «частично N из M». */
  partial: { parsed: number; inFile: number } | null;
  /** Из редакции сделана производная (КП, график) — строки не правятся (ТЗ PLAN-5). */
  frozen: { at: string; code: string; label: string } | null;
  version: string;
  /** Итог редакции — только с `plan.estimate.cost`. */
  cost?: { total: number };
}

export type EstimateKind = 'main' | 'additional';

export interface Estimate {
  id: string;
  constructionId: string;
  sectionId: string | null;
  name: string;
  kind: EstimateKind;
  /** Версия сметы: её называет смена действующей редакции заголовком `If-Match`. */
  version: string;
  currentRevisionId: string | null;
  currentRevisionNumber: number | null;
  revisions: EstimateRevision[];
  hidden?: HiddenPart[];
}

export interface EstimateList {
  items: Estimate[];
  /** Курсор следующей страницы общего списка; нет — смет больше нет. */
  next?: string;
  hidden?: HiddenPart[];
}

export interface EstimateMaterial {
  id: string;
  order: number;
  nomenclatureId: string | null;
  sourceName: string;
  sourceCode: string | null;
  sourceUnit: string | null;
  quantity: number;
  marks: PlanMark[];
  cost?: { unitPrice: number | null; amount: number | null };
}

export type EstimateRowKind = 'work' | 'material';

export interface EstimateRow {
  id: string;
  order: number;
  lsrSection: { code: string | null; name: string | null } | null;
  numberInSection: string | null;
  /** Сквозной № п/п из файла как есть («13», «13.1»); `null` у редакций до `plan-31`. */
  number: string | null;
  subordinate: boolean;
  /** `material` — позиция-материал ЛСР: вида работы не требует, в «не сопоставлено» не входит. */
  kind: EstimateRowKind;
  rateCode: string | null;
  name: string;
  sourceVolume: number | null;
  sourceUnit: string | null;
  workTypeId: string | null;
  unitId: string | null;
  factor: number | null;
  volume: number | null;
  planItemId: string | null;
  marks: PlanMark[];
  materials: EstimateMaterial[];
  labor: { manHours: number }[];
  /** Себестоимость строки — только с `plan.estimate.cost`. */
  cost?: { unitCost: number | null; total: number | null; data: unknown };
}

interface EstimateRowsPage {
  items: EstimateRow[];
  next: string | null;
  hidden?: HiddenPart[];
}

export interface RevisionRows {
  items: EstimateRow[];
  hidden?: HiddenPart[];
}

export interface RevisionSection {
  /** Код раздела ЛСР; `null` — строки вне разделов. */
  code: string | null;
  name: string | null;
  rows: number;
  unmatched: number;
  materialsUnmatched: number;
  units: { sourceUnit: string | null; rows: number }[];
}

export interface PlanVolumeChange {
  workTypeId: string;
  unitId: string | null;
  /** Объём плана по смете сейчас — 0, если вида работы в действующей нет. */
  was: number;
  becomes: number;
}

/** Что станет с планом, если сделать редакцию действующей (D7 `plan-31`). Считает сервер. */
export interface CurrentRevisionPreview {
  revisionId: string;
  revisionNumber: number;
  currentRevisionNumber: number | null;
  /** Только изменившиеся объёмы по виду работы и единице. */
  changes: PlanVolumeChange[];
  /** Строк-работ предлагаемой редакции без сопоставления — в план не войдут. */
  unmatched: number;
  /** Версия сметы — её называет смена в `If-Match`. */
  version: string;
  cost?: { was: number; becomes: number };
  hidden?: HiddenPart[];
}

/** Носитель «скрыта себестоимость», если он есть в ответе. */
export function costHidden(hidden: HiddenPart[] | undefined): HiddenPart | null {
  return hidden?.find(h => h.code === 'cost') ?? null;
}

const KEY = ['planning', 'estimates'] as const;
const REVISIONS = ['planning', 'revisions'] as const;

/** Сметы стройки — целиком, основные первыми (порядок сервера). Стройка не выбрана — не спрашивать. */
export function useConstructionEstimates(constructionId: string) {
  return useQuery({
    queryKey: [...KEY, 'construction', constructionId],
    queryFn: () => apiClient.get<EstimateList>('/plan/estimates', { params: { constructionId } }).then(r => r.data),
    enabled: constructionId !== '',
  });
}

/** Порция общего списка: сервер отдаёт сметы всех строек страницами по курсору. */
export const ESTIMATES_PAGE = 50;

/** Сметы всех строек страницами (D6 `plan-31`, ТЗ PLAN-20): «Показать ещё» дочитывает следующую. */
export function useAllEstimates() {
  return useInfiniteQuery({
    queryKey: [...KEY, 'all'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => apiClient.get<EstimateList>('/plan/estimates', {
      params: { cursor: pageParam, limit: ESTIMATES_PAGE },
    }).then(r => r.data),
    getNextPageParam: last => last.next ?? undefined,
  });
}

export function useEstimate(id: string) {
  return useQuery({
    queryKey: [...KEY, 'one', id],
    queryFn: () => apiClient.get<Estimate>(`/plan/estimates/${id}`).then(r => r.data),
  });
}

/** Сколько строк спрашивать за раз — предел сервера: редакция в тысячу строк читается одним запросом. */
const ROWS_PAGE = 1000;

/**
 * Строки редакции — ВСЕ, страницами по курсору. Таблица группирует их по разделам и отбирает по пометкам
 * сама, а неполный ответ показал бы раздел «всё сопоставлено», не дочитав его.
 */
export async function fetchRevisionRows(revisionId: string): Promise<RevisionRows> {
  const items: EstimateRow[] = [];
  let cursor: string | null = null;
  let hidden: HiddenPart[] | undefined;
  do {
    const page: EstimateRowsPage = (await apiClient.get<EstimateRowsPage>(`/plan/revisions/${revisionId}/rows`, {
      params: { cursor: cursor ?? undefined, limit: ROWS_PAGE },
    })).data;
    items.push(...page.items);
    hidden = page.hidden;
    cursor = page.next;
  } while (cursor);
  return { items, hidden };
}

export function useRevisionRows(revisionId: string | null) {
  return useQuery({
    queryKey: [...REVISIONS, revisionId, 'rows'],
    queryFn: () => fetchRevisionRows(revisionId!),
    enabled: revisionId !== null,
  });
}

/** Разделы ЛСР редакции со счётчиками — свёрнутая таблица рисуется по ним, в порядке файла. */
export function useRevisionSections(revisionId: string | null) {
  return useQuery({
    queryKey: [...REVISIONS, revisionId, 'sections'],
    queryFn: () => apiClient.get<{ items: RevisionSection[] }>(`/plan/revisions/${revisionId}/sections`)
      .then(r => r.data.items),
    enabled: revisionId !== null,
  });
}

const previewKey = (estimateId: string, revisionId: string) => [...KEY, 'one', estimateId, 'preview', revisionId];

/**
 * Предпросмотр смены действующей. Ключ — под ключом сметы: смена и отказ 409 перечитывают смету, а с ней и
 * предпросмотр — цифры в окне не должны остаться прежними, когда смету тем временем изменили.
 */
export function useCurrentRevisionPreview(estimateId: string, revisionId: string | null) {
  return useQuery({
    queryKey: previewKey(estimateId, revisionId ?? ''),
    queryFn: () => apiClient.get<CurrentRevisionPreview>(`/plan/estimates/${estimateId}/current-revision/preview`, {
      params: { revisionId },
    }).then(r => r.data),
    enabled: revisionId !== null,
    // Цифры окна — подсказка к решению прямо сейчас: открытое снова окно спрашивает заново.
    staleTime: 0,
  });
}

/**
 * Сделать редакцию действующей (ТЗ PLAN-5.2, решение В-5). Версия — сметы, из ответа ПРЕДПРОСМОТРА, который
 * человек видел: смена по версии из кэша сметы прошла бы поверх изменений, которых окно не показало.
 */
export function useMakeCurrentRevision() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ estimateId, revisionId, version }: { estimateId: string; revisionId: string; version: string }) =>
      apiClient.post<Estimate>(`/plan/estimates/${estimateId}/current-revision`, { revisionId },
        { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: estimate => {
      qc.setQueryData([...KEY, 'one', estimate.id], estimate);
      void qc.invalidateQueries({ queryKey: KEY });
    },
    // 409 и 404 — смету тем временем изменили или удалили: перечитывается она и предпросмотр, иначе окно
    // предлагало бы повторить по тем же устаревшим цифрам.
    onError: (_e, { estimateId }) => {
      void qc.invalidateQueries({ queryKey: [...KEY, 'one', estimateId] });
    },
  });
}

/** Отказ записи, разобранный для показа: что случилось и что сказать человеку. */
export interface PlanRefusal {
  kind: 'invalid' | 'forbidden' | 'conflict' | 'gone' | 'failed';
  text: string;
}

const CONFLICT_TEXT = 'Смету тем временем изменили — перечитайте её и повторите: вы видели прежнее состояние.';

/**
 * Отказ сервера смет словами (D17, ТЗ PLAN-5.2). Текст сервера — первым: он написан для человека и
 * называет, что именно не так. Без текста (403 от ворот права приходит с пустым телом, 409 старого
 * сервера) — своя формулировка того же смысла, а не «Request failed with status code 409».
 */
export function planRefusal(e: unknown): PlanRefusal {
  const err = e as { response?: { status?: number; data?: unknown }; message?: string };
  const status = err?.response?.status;
  const data = err?.response?.data;
  const server = typeof data === 'string' ? data.trim()
    : (data as { error?: string; detail?: string } | undefined)?.error ?? (data as { detail?: string } | undefined)?.detail;
  switch (status) {
    case 400: return { kind: 'invalid', text: server || 'Сервер не принял запрос.' };
    case 403: return { kind: 'forbidden', text: server || 'Нет права на это действие — его выдаёт администратор.' };
    case 404: return { kind: 'gone', text: server || 'Смету удалили, пока она была открыта.' };
    case 409: return { kind: 'conflict', text: server || CONFLICT_TEXT };
    default: return { kind: 'failed', text: server || err?.message || 'Не получилось — повторите.' };
  }
}

/**
 * Названия видов работ классификатора — для показа того, что у строки уже стоит (архивные на месте).
 * Классификатор читается целиком: видов в одной смете десятки, и запрос на каждый был бы дороже.
 */
export function useWorkTypeNames(enabled = true) {
  return useQuery({
    queryKey: ['planning', 'work-type-names'],
    queryFn: () => apiClient.get<CommonDataEntry[]>('/work-types', { params: { purpose: 'display' } })
      .then(r => new Map(r.data.map(e => [e.id, e.displayName]))),
    enabled,
    staleTime: 60_000,
  });
}

/** Названия единиц измерения по идентификаторам — их в смете единицы, и каждая читается своей записью. */
export function useUnitNames(ids: readonly string[]): ReadonlyMap<string, string> {
  const unique = [...new Set(ids)];
  const results = useQueries({
    queries: unique.map(id => ({
      queryKey: ['planning', 'unit-name', id],
      queryFn: () => apiClient.get<CommonDataEntry>(`/common-data/${id}`).then(r => r.data.displayName),
      staleTime: 60_000,
    })),
  });
  const names = new Map<string, string>();
  unique.forEach((id, i) => { const name = results[i]?.data; if (name) names.set(id, name); });
  return names;
}
