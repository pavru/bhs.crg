/**
 * Приёмка отчётов модуля «Учёт работ» (change `work-16-review`, ТЗ WORK-28): типы ответа очереди
 * `GET /api/work/review/queue` и коды пометок. Экраны очереди и карточки — `work-21`.
 *
 * Пометки считает СЕРВЕР: клиент их не вычисляет и текстов не знает — текст приходит в `marks`, число —
 * в `counts`. Здесь только коды, по которым отбирают и читают счётчик.
 */

/**
 * Коды каталога пометок сервера (`Review/ReviewMarks.cs`) — в его порядке. Сверяет `ReviewMarkCatalogTests`:
 * лишний код с любой стороны — красный тест сервера.
 */
export const WORK_MARK_CODES = [
  'edited-after-submit',
  'same-person',
  'manager-edit',
  'site-not-assigned',
  'photos-pending',
  'late-delivery',
] as const;

export type WorkMarkCode = (typeof WORK_MARK_CODES)[number];

/** Пометка отчёта: код — для отбора, текст — для человека. */
export interface WorkMark {
  code: WorkMarkCode;
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

/** Почему очередь пуста: своих строек нет, ждущих нет, или отбор исключил все. */
export type ReviewEmptyReason = 'not-assigned' | 'nothing-pending' | 'filtered-out';

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
}

/** Ответ `GET /api/work/review/queue`. */
export interface ReviewQueuePage {
  items: ReviewQueueRow[];
  /** Сколько отчётов в отборе всего — по всем страницам. */
  total: number;
  offset: number;
  limit: number;
  /** Счётчик каждого кода каталога по всему отбору (без отбора по пометке), нулевые — тоже. */
  counts: Record<WorkMarkCode, number>;
  boundary: ReviewBoundary;
  /** Причина пустой очереди; `null` — отчёты есть. */
  reason: ReviewEmptyReason | null;
}
