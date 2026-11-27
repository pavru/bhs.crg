/**
 * Приёмка отчётов модуля «Учёт работ» (change `work-16-review`, ТЗ WORK-28): типы ответа очереди
 * `GET /api/work/review/queue` и коды пометок. Экраны очереди и карточки — `work-21`.
 *
 * Пометки считает СЕРВЕР: клиент их не вычисляет и текстов не знает — текст приходит в `marks`, число —
 * в `counts`. Здесь только коды, по которым отбирают и читают счётчик.
 *
 * Здесь же — общие типы назначений: кандидат `GET /api/work/assignments/candidates` (`work-11`; поле `account` —
 * `work-29`) — и фото строки отчёта (`work-17`). Галерея и загрузка с экрана — `work-21`, `work-22`.
 */

import type { EmployeeAccount } from './employees';

/**
 * Коды каталога пометок сервера (`Data/ReviewMarks.cs`) — в его порядке. Сверяет `ReviewMarkCatalogTests`:
 * лишний код с любой стороны — красный тест сервера.
 */
export const WORK_MARK_CODES = [
  'edited-after-submit',
  'same-person',
  'manager-edit',
  'site-not-assigned',
  'photos-pending',
  'crew-mismatch',
  'accepted-changed',
  'late-delivery',
  'fields-missing',
  'required-missing',
] as const;

export type WorkMarkCode = (typeof WORK_MARK_CODES)[number];

export type RequiredReportField = { line: number; lineId: string; field: 'location' | 'materials' };

/** Коды SummaryStates сервера. Состав и порядок сверяет SummaryStateCatalogTests. */
export const WORK_SUMMARY_STATES = [
  'report',
  'zero-hours',
  'draft',
  'not-received',
  'not-assigned',
  'not-employed',
  'window-open',
  'future',
] as const;

export type WorkSummaryState = (typeof WORK_SUMMARY_STATES)[number];

/** Пометка отчёта: код — для отбора, текст — для человека. */
export interface WorkMark {
  code: string;
  text: string;
}

/** Что читающий может сделать с отчётом очереди; без права приёмки — ничего. */
export type ReviewAction = 'accept' | 'return' | 'edit';

/** Граница выдачи очереди — строка сервера; клиент её не составляет. */
export interface ReviewBoundary {
  /** `sites` — стройки назначений «менеджер», `all` — все стройки. */
  code: 'sites' | 'all';
  /** «отчёты ваших строек (4)» или «все стройки». */
  text: string;
  /** Число строек охвата `sites`; у `all` — `null`. */
  sites: number | null;
}

/** Почему очередь пуста: нет связи, справочника, назначений, ждущих отчётов или отбор исключил все. */
export type ReviewEmptyReason =
  | 'not-linked'
  | 'directory-missing'
  | 'not-assigned'
  | 'nothing-pending'
  | 'filtered-out';

/** Строка очереди приёмки — поданный отчёт. */
export interface ReviewQueueRow {
  id: string;
  employeeId: string;
  constructionId: string;
  date: string;
  shift: 'day' | 'night';
  hours: number | null;
  status: 'submitted';
  /** Значение для `If-Match` приёмки и возврата. */
  version: string;
  submittedAt: string | null;
  submittedBy: 'manual' | 'auto' | null;
  enteredBy: { userId: string; name: string } | null;
  /** Сколько строк в отчёте. */
  lines: number;
  marks: WorkMark[];
  actions: ReviewAction[];
  /** Приёмка пометит отчёт «ввёл и принял один человек». */
  acceptWouldMark: boolean;
  /** Сколько фото строк объявлено и ещё не дошло — его же называет пометка `photos-pending`. */
  photosPending: number;
}

export const WORK_REVIEW_REPORT_PERMISSIONS = ['work.report.review', 'work.report.read'] as const;

export interface CrewLine {
  id: string;
  reportId: string;
  employeeId: string;
  fullName: string;
  variantId: string | null;
  volume: number;
  unitId: string;
  crewRole: 'lead' | 'member';
}

export interface CrewPublication {
  published: boolean;
  reason: 'empty' | 'not-approved' | 'stale' | 'awaiting-acceptance' | 'invalid-key' | 'multiple-keys' | null;
  keys: { planItemId: string; sectionId: string | null; unitId: string; valid: boolean }[];
  accepted: number;
  participating: number;
}

export interface CrewGroup {
  id: string;
  constructionId: string;
  date: string;
  shift: 'day' | 'night';
  workTypeId: string;
  version: string;
  state: 'pending' | 'approved' | 'stale';
  mismatch: boolean;
  total: number | null;
  unitId: string | null;
  approvedBy: string | null;
  approvedByName: string | null;
  approvedAt: string | null;
  unsubmitted: number;
  publication: CrewPublication;
  actions: string[];
  lines: CrewLine[];
}

export interface CrewGroupsPage {
  items: CrewGroup[];
  constructions: { id: string; name: string }[];
  workTypes: Record<string, string>;
  workVariants: Record<string, string>;
  units: Record<string, string>;
  sections: Record<string, string>;
}

/** Ответ `GET /api/work/review/queue`. */
export interface ReviewQueuePage {
  items: ReviewQueueRow[];
  /** Сколько отчётов в отборе всего — по всем страницам. */
  total: number;
  offset: number;
  limit: number;
  /** Счётчик каждого кода каталога по всему отбору (без отбора по пометке), нулевые — тоже. */
  counts: Record<string, number>;
  /** Имена сотрудников всех поданных отчётов в охвате очереди. */
  employees?: { id: string; name: string }[];
  boundary: ReviewBoundary;
  /** Причина пустой очереди; `null` — отчёты есть. */
  reason: ReviewEmptyReason | null;
}

/**
 * Кандидат назначения — элемент `GET /api/work/assignments/candidates` (change `work-11-assignments`, ТЗ WORK-21).
 * Годность и причину исключения считает сервер теми же правилами, что и заведение.
 */
export interface AssignmentCandidate {
  employeeId: string;
  name: string;
  personnelNumber: string | null;
  eligible: boolean;
  /** Почему кандидат не годен, словами; `null` — годен. */
  reason: string | null;
  /** Пересекающееся назначение того же вида на этой стройке. */
  assigned: { id: string; dateFrom: string; dateTo: string | null } | null;
  /**
   * Связь с учётной записью (ТЗ WORK-47; change `work-29-candidate-account`) — пометка «нет учётной записи» у
   * выбора; годность она не меняет. `deleted` — не `none`: удалённую учётную запись никто не отвязывал.
   */
  account: { state: EmployeeAccount['state'] };
}

/** Ответ кандидатов: `more` — выдача обрезана пределом `limit`, сузьте строкой поиска. */
export interface AssignmentCandidatesPage {
  items: AssignmentCandidate[];
  inArchive: number;
  limit: number;
  more: boolean;
}

/**
 * Фото строки отчёта (change `work-17-photos`, ТЗ WORK-33, WORK-34): `pending` — строка назвала фото, файл ещё не
 * дошёл; `stored` — файл и превью лежат в хранилище. Отчёт фото не ждёт: подать и принять его можно и с `pending`.
 */
export type WorkPhotoState = 'pending' | 'stored';

/** Фото в строке ответа отчёта (`lines[].photos`); у отчёта рядом — число недогруженных `photosPending`. */
export interface WorkLinePhoto {
  id: string;
  state: WorkPhotoState;
  canDeliver?: boolean;
  originalName?: string;
}

/**
 * Фото в теле сохранения строки (`lines[].photos`): идентификатор создаёт клиент (ТЗ MOB-15). Перечень целиком —
 * не названное снимается; не присланный перечень фото не трогает.
 */
export interface WorkLinePhotoRef {
  id: string;
  originalName?: string;
}

/**
 * Ответ загрузки `PUT /api/work/reports/{id}/lines/{lineId}/photos/{photoId}` (тело запроса — сам файл, без
 * `If-Match`). Повтор той же загрузки отвечает тем же. Файл — `GET /api/work/photos/{photoId}`, превью —
 * `GET /api/work/photos/{photoId}/preview`; чужое фото и фото чужого черновика — 404.
 */
export interface WorkPhotoUpload {
  id: string;
  reportId: string;
  lineId: string;
  state: 'stored';
  /** Байты сжатого снимка, без превью. */
  size: number | null;
  width: number | null;
  height: number | null;
  storedAt: string | null;
}
