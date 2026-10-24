import { useQuery, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { onWritten, writeMeta, type WriteContext } from './writeWatch';

/**
 * Счета модуля «Счета и накладные» (задача C1 этапа 2, issue #1076, ТЗ COST-6.2).
 *
 * ⚠️ Реквизиты уезжают и приезжают ОБЪЕКТОМ «ключ поля: значение» — тем же, что отдаёт сервер, без
 * изъятий. Поля, которые ведёт код (состояния, скан), в этом объекте есть, и присылать их обратно
 * не только можно, но и нужно: правило сервера — «присылать можно, менять нельзя», и сравнивает он
 * значения, а не наличие ключа. Вырезав их, клиент получил бы отказ охраны записи ядра —
 * отсутствующее запертое поле она читает как стёртое.
 */
export interface InvoiceRequisites {
  [key: string]: unknown;
}

export interface InvoiceDuplicate {
  id: string;
  number: string | null;
  issuedOn: string | null;
  total: number | null;
}

/**
 * Счёт, к которому приложен файл с тем же содержимым (issue #1295). У черновика из файла нет ни
 * номера, ни даты — узнают его по времени заведения.
 */
export interface InvoiceSameFile extends InvoiceDuplicate {
  scanFileName: string | null;
  createdAt: string;
}

/**
 * Строка счёта (C2, issue #1078, ТЗ COST-7).
 *
 * ⚠️ Пустое `nomenclatureName` при заполненном `nomenclatureId` потерей НЕ является — потерю называет
 * `nomenclatureLost`, и считает её сервер. Пустым имя бывает у законной записи справочника без имени
 * (такие в базе есть) и там, где справочника нет вовсе; выведи мы потерю из пустоты — законная позиция
 * краснела бы «не найдена» сразу после выбора, а снять такую ссылку было нечем (ревью PR #1117).
 *
 * ⚠️ Потеря отличается от «строка ждёт позиции» ровно наличием ссылки, и путать их нельзя: первое
 * чинит справочник, второе — человек за формой.
 */
export interface InvoiceLineView {
  id: string;
  ordinal: number;
  nomenclatureId: string | null;
  nomenclatureName: string | null;
  nomenclatureLost: boolean;
  /**
   * Что именно не так со ссылкой (issue #1184): `lost` — записи нет; `moved` — запись есть, но она больше
   * не позиция номенклатуры (это не потерянная ссылка). Старый сервер поля не присылает.
   */
  nomenclatureIssue?: 'lost' | 'moved' | null;
  /** Позиция в архиве (issue #1185): строка сведена и названа, в поиске этой позиции больше нет. */
  nomenclatureArchived?: boolean;
  supplierText: string | null;
  supplierCode: string | null;
  unit: string | null;
  quantity: number | null;
  price: number | null;
  vatRate: number | null;
  vatAmount: number | null;
  /**
   * Сумма строки С НДС — в обоих режимах счёта (issue #1497). ⚠️ При НДС сверху это производное
   * сервера, а не бумага: отправленная обратно как бумага, она прибавила бы НДС второй раз (сервер
   * ответит 400). Бумажная сумма такого счёта — `netAmount`.
   */
  amount: number | null;
  /** Сумма строки из бумаги, без НДС, — только у счёта с НДС сверху (issue #1497). */
  netAmount?: number | null;
  note: string | null;
  /** Разноска строки по стройкам и остаток «не разнесено» (F1, issue #1085). */
  allocation: LineAllocationView;
  /**
   * Пометка «(запомнено)» (issue #1079): позиция подставлена из соответствий поставщика. `null` —
   * позицию выбрал человек либо её нет. Старый сервер поля не присылает.
   */
  match?: InvoiceLineMatch | null;
}

/**
 * Пометка подстановки и то, что стало с соответствием с тех пор. Состояние считает сервер на чтении:
 * правка соответствия строки чужих счетов не переписывает, и узнать о ней строка может только так.
 */
export interface InvoiceLineMatch {
  id: string;
  /** По чему строка узнана; `null` — соответствия больше нет, и сказать нечем. */
  by: 'code' | 'name' | null;
  /**
   * `current` — соответствие на месте и ведёт туда же; `changed` — его направили на другую позицию;
   * `gone` — его забыли; `foreign` — оно другого поставщика (поставщика у счёта сменили).
   */
  state: 'current' | 'changed' | 'gone' | 'foreign';
  source: string | null;
  rememberedAt: string | null;
  rememberedBy: string | null;
}

/** Что запомнилось сохранением строк (issue #1079); есть только в ответе этого сохранения. */
export interface InvoiceMatchMemory {
  remembered: number;
  /** Сколько из запомненного ЗАМЕНИЛО запомненное раньше — другой позицией. */
  replaced: number;
  /** Запомнить не удалось: строки при этом сохранены. */
  failed?: boolean;
}

/**
 * Часть разноски строки (F1, issue #1085, ТЗ COST-10).
 *
 * ⚠️ `amount` у строки с количеством ПОСЧИТАН сервером (доля суммы строки), а не введён: форма его
 * только показывает. `rounding` и `discrepancy` не нуль у той части, в которую ушли копейки округления
 * и расхождение с суммой к оплате, — её форма помечает, иначе «33,34» среди «33,33» выглядело бы
 * опечаткой.
 */
/**
 * Что не так с целью части разноски. `…-lost` — записи больше нет: это потерянная ссылка. Остальное —
 * не потеря, и в число потерянных ссылок не входит: `section-foreign` — раздел на месте, но принадлежит
 * другой стройке; `article-moved` — запись есть, но она больше не статья; `article-unread` — справочник
 * статей не прочитан, сказать о цели нечего.
 */
export type TargetIssue =
  | 'construction-lost' | 'section-lost' | 'article-lost'
  | 'section-foreign' | 'article-moved' | 'article-unread';

export interface AllocationPartView {
  id: string;
  ordinal: number;
  /** Стройка; `null` — часть легла на статью вне строек (F3, issue #1087). */
  constructionId: string | null;
  constructionName: string | null;
  sectionId: string | null;
  sectionName: string | null;
  /** Статья вне строек — «Склад», «Общие расходы»; `null` — часть легла на стройку. Ровно одно из двух. */
  articleId: string | null;
  articleName: string | null;
  /** С целью что-то не так — «разобран» с такой частью не проходит. Что именно, говорит `targetIssue`. */
  targetLost: boolean;
  /** Что не так с целью (issue #1184); `null` — цель на месте. Старый сервер поля не присылает. */
  targetIssue?: TargetIssue | null;
  quantity: number | null;
  amount: number | null;
  rounding: number;
  discrepancy: number;
  /** Часть не того вида, что строка (у строки убрали количество) — не разносит ничего. */
  mismatched: boolean;
}

/**
 * Разноска строки. `mode` решает сервер по самой строке: есть количество — делится количество, нет —
 * сумма; форма не выбирает, иначе предлагала бы метры строке, которую сервер разносит рублями.
 */
export interface LineAllocationView {
  mode: 'quantity' | 'amount' | 'none';
  parts: AllocationPartView[];
  unallocatedQuantity: number | null;
  unallocatedAmount: number | null;
  balanced: boolean;
}

/** «Разнесён» — ровно то условие, которое проверяет переход «разобран» (ТЗ COST-9, COST-13). */
export interface AllocationSummaryView {
  allocated: boolean;
  /** Номера строк, разнесённых не полностью. */
  unbalanced: number[];
  /** Частей на удалённую стройку или раздел. */
  lost: number;
  /** Сумма к оплате минус сумма строк; `null` — суммы к оплате нет. */
  discrepancy: number | null;
  /** Допуск расхождения — приезжает от сервера: по ТЗ это настройка, повторять её в форме нельзя. */
  tolerance: number;
  withinTolerance: boolean;
  /** Разноска счёта целиком суммой — у счёта без строк (F2, ТЗ COST-11). */
  document: DocumentAllocationView;
  /** Отметка версии разноски — её присылает запись матрицы, чтобы устаревший набор был отвергнут. */
  stamp: string;
}

/**
 * Разноска счёта целиком суммой (F2, issue #1086). Живёт, пока строк нет; появились строки — ждёт
 * пересчёта (`pending`), и «разнесён» до него не наступает.
 */
export interface DocumentAllocationView {
  parts: AllocationPartView[];
  /** «Не разнесено» от суммы к оплате; `null` — у счёта есть строки или нет суммы к оплате. */
  unallocatedAmount: number | null;
  pending: boolean;
  balanced: boolean;
}

export interface CostsSection {
  id: string;
  name: string;
}

export interface CostsConstruction {
  id: string;
  name: string;
  sections: CostsSection[];
}

/**
 * Как бумага счёта считает НДС (issue #1497): `Included` — суммы строк с НДС, «в том числе»; `OnTop` — цены
 * и суммы строк без НДС, НДС начислен сверху. Признак счёта, а не строки: смешанных режимов в одной бумаге
 * не бывает.
 */
export type VatMode = 'Included' | 'OnTop';

/** Сверка суммы строк с суммой к оплате и счётчик ждущих позицию (ТЗ COST-6.2). */
export interface InvoiceLineTotals {
  count: number;
  withoutNomenclature: number;
  /** Сумма строк С НДС — в обоих режимах; строка сверху без НДС в неё не входит. */
  amount: number;
  /** Сумма НДС по строкам, где он указан. */
  vat: number;
  /** Сколько строк без НДС (issue #1409): равно `count` — «не указан», а не ноль. */
  vatMissing: number;
  /** Режим НДС, по которому сложены итоги (issue #1497). Старый сервер поля не присылает. */
  mode?: VatMode;
  /** Сумма строк без НДС из бумаги — только при НДС сверху, иначе `null`. Старый сервер поля не присылает. */
  net?: number | null;
  /** Откуда НДС счёта: у всех строк — `lines`, иначе из шапки — `requisites`; нет нигде — `null`. */
  vatFrom?: 'lines' | 'requisites' | null;
}

/**
 * Оплата в ответе счёта (C5, issue #1082). Всё здесь — слова и решения сервера: форма не выводит
 * «заперт» из границ периодов и «оплатить нельзя» из сверки сумм своей формулой.
 */
export interface PaymentView {
  paid: boolean;
  paidOn: string | null;
  /** Платёжный документ — необязательный текст. */
  document: string | null;
  paidAt: string | null;
  /** Почему счёт сейчас оплатить нельзя; у оплаченного — `null`. */
  refusal: string | null;
  /** Чем счёт заперт («период закрыт по … у стройки …»); `null` — не заперт. Признак запертого —
   *  только это поле, а не `paid`: оплаченный счёт открытого периода правится. */
  lockedBy: string | null;
  /** Учётные месяцы счёта — «09.2026», по возрастанию. */
  periods: string[];
}

/**
 * Одно, чего счёту не хватает до «Разобран». `kind` говорит, к какому месту формы пункт ведёт:
 * `field`/`reference` — поле шапки (`field`), `lines`/`nomenclature`/`position`/`catalog` — таблица
 * строк, `allocation` — разноска. Строкой, а не перечислением: новое условие сервера обязано доехать
 * до экрана пунктом, а не потеряться на незнакомом значении.
 */
export interface InvoiceReadinessGap {
  kind: string;
  /** Пункт словами сервера: «плательщик», «3 строки без позиции». */
  label: string;
  field?: string | null;
  lines?: number[] | null;
  /**
   * Расхождение суммы строк объясняет режим НДС (issue #1500): `onTop` — похоже, цены без НДС; `included`
   * — флажок «сверху» поставлен зря. Догадка сервера, а не решение: сам режим не меняет никто, кроме
   * человека.
   */
  vatExplains?: 'onTop' | 'included' | null;
}

export interface InvoiceReadiness {
  /** Ключи обязательных полей шапки. */
  required: string[];
  /** Чего не хватает; пусто — всё готово; `null` — вопрос не стоит: счёт разобран либо отклонён. */
  gaps: InvoiceReadinessGap[] | null;
}

export interface InvoiceView {
  id: string;
  /** Версия счёта: её называет каждая правка (`seen`), и устаревшая получает отказ 409 (issue #1176). */
  version: string;
  documentTypeId: string;
  requisites: InvoiceRequisites;
  /** Ключи полей, заполненных распознаванием и не подтверждённых человеком. */
  unconfirmed: string[];
  duplicates: InvoiceDuplicate[];
  /** Счета с тем же содержимым файла — оговорка, не запрет: в одном файле бывает несколько счетов. */
  sameFile: InvoiceSameFile[];
  lines: InvoiceLineView[];
  totals: InvoiceLineTotals;
  allocation: AllocationSummaryView;
  payment: PaymentView;
  /**
   * Что стало с записями ядра, на которые ссылается шапка (issue #1184); `null` у поля — ссылки нет.
   * Считает сервер: выводи это форма сравнением со списком организаций, «список ещё грузится» выглядел
   * бы потерей. Старый сервер поля не присылает.
   */
  references?: InvoiceReferences;
  /**
   * Готовность к «Разобран» (issue #1291): какие поля обязательны и чего счёту не хватает. Перечень
   * строит сервер той же функцией, что проверяет переход, — форма его не выводит сама. Старый сервер
   * поля не присылает.
   */
  readiness?: InvoiceReadiness;
  /**
   * Срок «оплатить до» глазами правила (issue #1080): `manual` — срок вписан человеком, правило его не
   * трогает; `byRule` — что дало бы правило сейчас (`null` — считать не из чего). Сам срок — реквизит
   * «Срок». Старый сервер поля не присылает.
   */
  /** Срок по правилу (C4, issue #1080); `supplierDeferral` — отсрочка в условиях поставщика счёта. */
  due?: { manual: boolean; byRule: string | null; supplierDeferral?: number | null };
  createdAt: string;
  updatedAt: string;
  /**
   * Записей в книге проводок счёта (E2, PR 6b-2): больше нуля — у формы есть секция «Исправления».
   * Старый сервер поля не присылает — секции нет.
   */
  book?: number;
  /** Что запомнилось этим сохранением строк; у чтения счёта поля нет. */
  memory?: InvoiceMatchMemory | null;
  /** Режим НДС (issue #1497). Меняется только вместе со строками; старый сервер поля не присылает. */
  vatMode?: VatMode;
}

export type ReferenceState = 'present' | 'archived' | 'lost';

export interface InvoiceReferences {
  supplier: ReferenceState | null;
  payer: ReferenceState | null;
  documentType: ReferenceState;
  /**
   * Название стороны, если она в архиве (issue #1185): в списке на выбор её нет, и взять название
   * форме больше неоткуда. У действующей стороны — `null`: её называет список.
   */
  supplierName?: string | null;
  payerName?: string | null;
}

export interface InvoiceListItem {
  id: string;
  number: string | null;
  issuedOn: string | null;
  supplierId: string | null;
  /** ⚠️ `null` при заполненном `supplierId` означает ПОТЕРЮ: ссылка есть, записи нет. */
  supplierName: string | null;
  /** Поставщик в архиве (issue #1185) — значок у названия, не неисправность. */
  supplierArchived?: boolean;
  total: number | null;
  state: string;
  payment: string;
  dueDate: string | null;
  purpose: string | null;
  unconfirmedCount: number;
  hasScan: boolean;
  linesCount: number;
  /** Сколько строк ждёт позиции номенклатуры — счётчик «Разобрать». */
  linesWithoutNomenclature: number;
  /** Что со ссылками счёта на записи справочников (issue #1186). Старый сервер поля не присылает. */
  references?: InvoiceListReferences | null;
  /** Имя файла скана: до распознавания у счёта из скана нет ни номера, ни поставщика. */
  scanFileName?: string | null;
  /**
   * Что со сканом (issue #1077): `running` — читается; остальное — у черновика со сканом без строк:
   * `failed` — распознавание отказало (вид отказа — `reason`), `done` — прочитано, но строк нет,
   * `none` — не распознавался. Пусто — сказать нечего.
   */
  recognition?: { state: 'running' | 'failed' | 'done' | 'none'; reason: string | null } | null;
  /**
   * Чего черновику не хватает до «Разобран» (issue #1292) — пунктами, словами сервера и той же
   * функцией, что в открытом счёте. Пусто — всё готово, осталось нажать. `null` либо поля нет —
   * сказать нечего: счёт не черновик, заперт закрытым периодом, причины не прочитаны или сервер
   * прежний. ⚠️ Это не «всё готово».
   */
  readiness?: string[] | null;
  /**
   * Дней до срока «оплатить до» от сегодня в поясе компании (C4, issue #1080); у просроченного —
   * отрицательное. `null` — счёт оплаты не ждёт либо срок не определён.
   */
  daysLeft?: number | null;
  /** Срок прошёл, а счёт оплаты ждёт. Считает сервер — признак нигде не хранится. */
  overdue?: boolean;
  /**
   * Стройки разноски (issue #1221), каждая один раз; пусто — счёт не разнесён ни на одну стройку.
   * По ним рейл отбирает под фильтром «Избранные стройки». Старый сервер поля не присылает — тогда
   * счёт считается неразнесённым и виден всегда.
   */
  constructionIds?: string[];
  /**
   * Счёт менялся после закрытия периода — у него есть книга исправлений (E2, issue #1100, PR 6c): тот же
   * признак, что у колонки реестра «Исправлен после закрытия». Старый сервер поля не присылает — пометки нет.
   */
  corrected?: boolean;
}

/** Где в счёте стоят ссылки не на месте. `count` — ссылок, а не мест формы. */
export interface InvoiceReferencePlace {
  kind: 'supplier' | 'payer' | 'type' | 'position' | 'allocation' | 'other';
  count: number;
}

export interface InvoiceListReferences {
  /**
   * Сервер подтвердил: запись поставщика удалена. ⚠️ `false` — не «на месте» и не «другого вида»,
   * а только «потеря не найдена»: списку остаются осторожные слова.
   */
  supplierLost: boolean;
  /** Что можно сделать с удалёнными записями: исправить, ждать отмены закрытия периода, ничего. */
  lostState: 'fixable' | 'locked' | 'type' | null;
  /** Архивная запись зовёт к правке: счёт ещё не оплачен. */
  archivedCalls: boolean;
  lost: InvoiceReferencePlace[];
  archived: InvoiceReferencePlace[];
}

/** Очередь списка счетов — отбор, который считает сервер. Выбор один: пересечений он не считает. */
export type InvoiceQueue = 'parsing' | 'unrecognized' | 'lost' | 'archived';

export interface CostsOrganization {
  id: string;
  name: string;
  type: string;
  /** В архиве — бывает только в списке на показ. */
  archived: boolean;
}

/** Позиция номенклатуры в выборе строки. */
export interface NomenclatureItem {
  id: string;
  name: string | null;
  type: string;
  /**
   * Альтернативное имя, по которому позиция найдена, — когда набранного нет в названии. Показывать
   * обязательно: позиция, в названии которой набранного нет, без пояснения выглядит ошибкой поиска.
   */
  matchedAlias: string | null;
}

/**
 * Найденное и оговорка о неполноте.
 *
 * ⚠️ `more` обязана доехать до человека словами. Неполный список, выданный за полный, читается как
 * «такой позиции нет» — и человек заводит вторую такую же позицию номенклатуры.
 */
export interface NomenclatureSearchResult {
  items: NomenclatureItem[];
  more: boolean;
  /**
   * Сколько позиций под тот же запрос лежит в архиве (issue #1185). В `items` их нет — и сказать об
   * этом человеку обязательно: иначе ненайденная архивная позиция читается как отсутствующая.
   */
  inArchive: number;
}

export const QK = 'costs-invoices';

export const INVOICES_KEY = [QK] as const;

/**
 * Ключ вида одного счёта — `[QK, id]`. Рядом под тем же QK лежат списки (`'list'`) и числа очередей
 * (`'queues'`); перечитать только виды открытых счетов значит отобрать по этому признаку.
 */
export function isInvoiceView(key: readonly unknown[]): boolean {
  return key.length === 2 && key[0] === QK && key[1] !== 'list' && key[1] !== 'queues';
}

/**
 * Реестр счетов. Очередь: «Разобрать» — все недоведённые счета, то есть черновики; «удалённые записи»
 * и «в архиве» — отборы «наведите порядок» (issue #1186).
 *
 * ⚠️ Отбор входит в ключ запроса. Без этого React Query отдал бы отобранному списку кэш полного (и
 * наоборот), и «счёт есть, а в списке его нет» стало бы поведением.
 */
export function useInvoices(queue: InvoiceQueue | null = null) {
  return useQuery({
    // Пока чей-то скан читается, список опрашивается: иначе «распознаётся…» стояло бы в строке вечно.
    refetchInterval: query => (scansRunning(query.state.data).length > 0 ? 5000 : false),
    queryKey: [QK, 'list', queue] as const,
    queryFn: () => apiClient
      .get<InvoiceListItem[]>('/costs/invoices', {
        params: queue === 'parsing' ? { needsParsing: true }
          : queue === 'unrecognized' ? { unrecognized: true }
          : queue ? { fix: queue } : {},
      })
      .then(r => r.data),
    // Свежесть — как у чисел над списком (`useInvoiceQueues`): запись в общих данных удаляют и
    // убирают в архив на другом экране, ключ счетов это не сбрасывает. Из кэша список вернулся бы
    // с тремя счетами под чипом «4» (ревью PR #1241). Мигания нет: прежние строки стоят, пока идёт запрос.
    staleTime: 0,
  });
}

/** Счета списка, чей скан читается сейчас. Одно место: по нему список опрашивается и перечитываются
 *  числа чипов, когда чтение кончилось. */
export const scansRunning = (items: InvoiceListItem[] | undefined): string[] =>
  (items ?? []).filter(i => i.recognition?.state === 'running').map(i => i.id);

/** Чтение счёта — одно на обычный запрос и на перечитывание после отказа: ключ и адрес не разойдутся. */
export function invoiceQuery(id: string | undefined) {
  return {
    queryKey: [QK, id] as const,
    queryFn: () => apiClient.get<InvoiceView>(`/costs/invoices/${id}`).then(r => r.data),
  };
}

/** Отказ «такого нет» (404) — по ответу axios. У счёта это значит «удалён»: другого пути исчезнуть у него нет. */
export const isNotFound = (error: unknown): boolean =>
  (error as { response?: { status?: number } })?.response?.status === 404;

export function useInvoice(id: string | undefined) {
  return useQuery({
    ...invoiceQuery(id),
    enabled: !!id,
    // «Счёта нет» повтором не лечится (issue #1282): второй запрос лишь оттянул бы ответ человеку.
    retry: (failures, error) => !isNotFound(error) && failures < 1,
  });
}

/**
 * Счёт, прочитанный СЕЙЧАС, — после отказа «счёт изменили» (issue #1176).
 *
 * Версия у счёта одна на все его части, и вид на экране мог отстать: сосед сохранил шапку минуту назад,
 * а кэш ещё прежний. Свежий вид отвечает на вопрос, который отказ оставил открытым: изменилась ли МОЯ
 * часть. Нет — запись повторяется со свежей версией (`useDraftBase`).
 */
export function useFreshInvoice() {
  const qc = useQueryClient();
  return (id: string) => qc.fetchQuery({ ...invoiceQuery(id), staleTime: 0 });
}

/**
 * Организации — узкий список модуля. Назначение обязательно (issue #1185), как у общих данных ядра:
 * `choice` — выбор поставщика и плательщика, архивных организаций в нём нет; `display` — все, с
 * признаком. Название архивной организации, которая в счёте уже стоит, приходит с самим счётом
 * (`InvoiceReferences`), а не отсюда.
 */
export function useCostsOrganizations(purpose: 'choice' | 'display') {
  return useQuery({
    queryKey: ['costs-organizations', purpose],
    queryFn: () => apiClient.get<CostsOrganization[]>('/costs/organizations', { params: { purpose } }).then(r => r.data),
  });
}

/**
 * Правка называет версию счёта, по которой собрана (issue #1176): заголовок `If-Match`. Без него
 * сервер отказывает 400, с устаревшей версией — 409 «счёт тем временем изменили».
 *
 * ⚠️ `seen` — версия вида, по которому собран ЧЕРНОВИК, а не «какая сейчас в кэше»: вид под формой
 * обновляется сам, и свежая подпись под прежним черновиком затёрла бы чужую правку. Какую версию
 * называть, решает форма (`useDraftBase`).
 */
export function seenBy(version: string) {
  return { headers: { 'If-Match': version } };
}

/**
 * Отказ 409 — счёт изменили: вид перечитывается сразу, чтобы форма узнала, ЧТО изменилось, и назвала
 * это человеку. Без перечитывания он увидел бы отказ над экраном, на котором всё по-прежнему.
 */
function rereadOnConflict(qc: QueryClient) {
  return (error: unknown, input: { id: string }) => {
    // И 404 тоже (issue #1282): счёт удалили, пока он был открыт. Перечитывание ответит тем же 404, и
    // форма узнает, что счёта больше нет, — а не оставит человека с отказом над живой на вид формой.
    const status = (error as { response?: { status?: number } })?.response?.status;
    if (status === 409 || isNotFound(error)) void qc.invalidateQueries({ queryKey: [QK, input.id] });
  };
}

/** Признак записи счёта в `meta` мутации — по нему её узнаёт `onInvoiceWritten`. */
const INVOICE_WRITE = 'invoiceWrite';

/**
 * Правка счёта, возвращающая его вид: ответ кладётся в кэш, реестр перечитывается, отказ 409 перечитывает
 * счёт. Каждый пишущий хук счёта строится на этом — иначе следующий, написанный по образцу, забыл бы
 * перечитать после отказа, и отказ повис бы над экраном, на котором всё по-прежнему.
 */
export function useInvoiceWrite<T extends { id: string }>(send: (input: T) => Promise<InvoiceView>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: send,
    meta: writeMeta(INVOICE_WRITE),
    // Вид до записи — в контекст мутации: по нему подписчик узнаёт, что изменила именно эта запись.
    onMutate: (input): WriteContext<InvoiceView> => ({ before: qc.getQueryData<InvoiceView>([QK, input.id]) }),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
    onError: rereadOnConflict(qc),
  });
}

/**
 * Подписка на СВОИ удавшиеся записи счёта: прежний вид и новый (issue #1221 — разноска на неизбранную
 * стройку уводит счёт из рейла, и экран говорит об этом). Своя — значит сделанная этой вкладкой через
 * `useInvoiceWrite`; чужая правка, пришедшая перечитыванием, сюда не попадает.
 *
 * <p>Через кэш мутаций, а не через контекст экрана (ревью PR #1334): запись видна, где бы ни был
 * смонтирован пишущий хук — в диалоге-портале, на другом экране, — и слой API не знает о том, кто
 * слушает. Новый пишущий хук «по образцу» попадает сюда сам: признак ставит `useInvoiceWrite`.</p>
 *
 * @returns отписка
 */
export function onInvoiceWritten(
  qc: QueryClient, listener: (before: InvoiceView | undefined, after: InvoiceView) => void,
): () => void {
  return onWritten(qc, INVOICE_WRITE, listener);
}

/**
 * Удалить счёт (issue #1282): только черновик без оплаты, насовсем. Ответа-вида нет, поэтому не
 * `useInvoiceWrite`; кэш после успеха чистит `forgetInvoice` — когда форма уже закрыта.
 */
export function useDeleteInvoice() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, seen }: { id: string; seen: string }) =>
      apiClient.delete(`/costs/invoices/${id}`, seenBy(seen)).then(() => undefined),
    onError: rereadOnConflict(qc),
  });
}

/**
 * Забыть удалённый счёт: выбросить его из кэша вместе с распознаванием и оплатой и перечитать
 * остальное под ключом счетов.
 *
 * ⚠️ `removeQueries`, а не `invalidateQueries`: помеченный устаревшим счёт был бы запрошен заново и
 * ответил бы 404. И звать — ПОСЛЕ закрытия формы: у выброшенного при живом наблюдателе запроса
 * наблюдатель тут же начал бы его заново.
 */
export function forgetInvoice(qc: QueryClient, id: string) {
  qc.removeQueries({ queryKey: [QK, id] });
  // Всё остальное под ключом счетов — устарело: список, числа очередей и виды ДРУГИХ счетов. У
  // оставшегося двойника в кэше лежит оговорка о дубликате со ссылкой на удалённый (ревью PR #1298).
  // Сам удалённый счёт под ключом уже не стоит, и заново его это не запросит.
  void qc.invalidateQueries({ queryKey: INVOICES_KEY });
}

export function useCreateInvoice() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (requisites: InvoiceRequisites) =>
      apiClient.post<InvoiceView>('/costs/invoices', { requisites }).then(r => r.data),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: INVOICES_KEY }); },
  });
}

export function useUpdateInvoice() {
  return useInvoiceWrite(({ id, seen, requisites }: { id: string; seen: string; requisites: InvoiceRequisites }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}`, { requisites }, seenBy(seen)).then(r => r.data));
}

/**
 * «Всё верно» по блоку: снять метки с названных полей.
 *
 * ⚠️ Поля перечисляет КЛИЕНТ, и пустой список сервер отвергает. Это не формальность: «снять все» —
 * самое дорогое прочтение промаха, метки исчезли бы разом и вернуть их было бы нечем.
 */
export function useConfirmInvoiceFields() {
  return useInvoiceWrite(({ id, seen, fields }: { id: string; seen: string; fields: string[] }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/confirmed`, { fields }, seenBy(seen)).then(r => r.data));
}

/** «По правилу» (issue #1080): снять ручную правку срока и подставить его правилом из настройки модуля. */
export function useDueByRule() {
  return useInvoiceWrite(({ id, seen }: { id: string; seen: string }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/due/rule`, null, seenBy(seen)).then(r => r.data));
}

export function useAttachInvoiceScan() {
  return useInvoiceWrite(({ id, seen, file }: { id: string; seen: string; file: File }) => {
    const form = new FormData();
    form.append('file', file);
    return apiClient.post<InvoiceView>(`/costs/invoices/${id}/scan`, form, seenBy(seen)).then(r => r.data);
  });
}

/**
 * Строки счёта — НАБОРОМ (C2, issue #1078): присланное и есть новое состояние.
 *
 * ⚠️ У сохранённых строк обязан уехать их `id`. Тот же набор без `id` означает для сервера «удали эти
 * строки и заведи новые»: на строку будет ссылаться разноска по количеству (F1), и потерянный `id`
 * рвал бы ссылку молча. Собирает набор `toPayload` — там же это правило и записано.
 */
export function useReplaceInvoiceLines() {
  return useInvoiceWrite(({ id, seen, lines, vatMode }: {
    id: string; seen: string; lines: Record<string, unknown>[]; vatMode?: VatMode;
  }) =>
    // Режим едет тем же запросом и с той же версией (issue #1497): он описывает суммы этих строк, и запись
    // «режим есть, строк ещё нет» дала бы счёт, где суммы вдруг стали без НДС. Нет поля — режим не меняется.
    apiClient.put<InvoiceView>(`/costs/invoices/${id}/lines`, { lines, ...(vatMode ? { vatMode } : {}) }, seenBy(seen))
      .then(r => r.data));
}

/**
 * Стройки с разделами — цели разноски. Узким списком модуля, как организации. `enabled: false` — не
 * спрашивать: подсказке отбора таблицы справочник нужен, только когда в таблице есть его колонка.
 */
export function useCostsConstructions(enabled = true) {
  return useQuery({
    queryKey: ['costs-constructions'],
    queryFn: () => apiClient.get<CostsConstruction[]>('/costs/constructions').then(r => r.data),
    enabled,
  });
}

/**
 * Части разноски строки — НАБОРОМ (F1, issue #1085). Часть с `id` правится на месте.
 *
 * ⚠️ У строки с количеством уезжает `quantity`, без количества — `amount`, и никогда оба: сумму части
 * строки с количеством считает сервер, и присланная сумма была бы отвергнута.
 */
export function useReplaceAllocation() {
  return useInvoiceWrite(({ id, seen, lineId, parts }: {
    id: string; seen: string; lineId: string; parts: Record<string, unknown>[];
  }) =>
    apiClient.put<InvoiceView>(`/costs/invoices/${id}/lines/${lineId}/allocation`, { parts }, seenBy(seen))
      .then(r => r.data));
}

/**
 * «Разобран», «вернуть в черновик» и «отклонён» — переходы состояния (ТЗ COST-9).
 *
 * Одним хуком на три адреса: действие у них одно и то же по устройству — послать и перечитать, — а
 * различаются они словом в адресе. Два почти одинаковых хука расходились бы обработкой отказа.
 */
export function useInvoiceState() {
  return useInvoiceWrite(({ id, seen, to }: { id: string; seen: string; to: 'parsed' | 'draft' | 'rejected' }) =>
    apiClient.post<InvoiceView>(`/costs/invoices/${id}/${to}`, null, seenBy(seen)).then(r => r.data));
}

/**
 * Поиск позиции номенклатуры для строки.
 *
 * ⚠️ Списком целиком справочник НЕ отдаётся: он самый большой в системе, и в данных записи лежат
 * картинки. Поэтому здесь поиск, ответ ограничен, а неполноту сервер называет признаком `more` —
 * показать его человеку обязана форма.
 */
export function useNomenclature(query: string) {
  return useQuery({
    queryKey: ['costs-nomenclature', query] as const,
    queryFn: () => apiClient
      .get<NomenclatureSearchResult>('/costs/nomenclature', { params: query ? { query } : {} })
      .then(r => r.data),
    // Прошлый ответ показываем, пока едет новый: иначе список мигает пустотой на каждой набранной
    // букве, и человек читает это как «ничего не нашлось». Кроме ответа на пустой запрос: это голова
    // алфавита, окно её не показывает (issue #1518), и на первой букве она мелькнула бы находками.
    placeholderData: (previous, previousQuery) => (previousQuery?.queryKey[1] ? previous : undefined),
  });
}

/**
 * Скан счёта — объектной ссылкой для показа.
 *
 * ⚠️ Через адрес МОДУЛЯ, а не через общий адрес вложений: у файла в хранилище прав нет, а у этого
 * адреса есть — `costs.invoice.read`. Общий адрес закрыт другим правом (`core.files.use`), и счёт
 * читался бы тем, у кого прав на счета нет вовсе.
 */
export async function loadInvoiceScan(id: string): Promise<{ url: string; mimeType: string }> {
  const response = await apiClient.get(`/costs/invoices/${id}/scan`, { responseType: 'blob' });
  const declared = (response.headers['content-type'] as string | undefined) ?? '';
  const mimeType = declared.split(';')[0].trim().toLowerCase() || 'application/octet-stream';
  return { url: URL.createObjectURL(new Blob([response.data as BlobPart], { type: mimeType })), mimeType };
}
