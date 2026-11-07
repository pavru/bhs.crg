import type { InvoiceView } from '@/shared/api/invoices';
import type { InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import type { StatusTone } from '@/shared/ui/StatusText';
import { formatDate, formatDateTime, formatMoney } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { FIELD_LABEL, K, REJECTED, asInput, duplicateLabel, sameFileLabel, twinsOf } from './invoiceFields';
import { DRAFT, type DeleteReason } from './invoiceDelete';
import { readinessItems, type ReadinessTarget } from './invoiceReadiness';
import { PART_ORDER, partOfTarget } from './invoiceParts';
import { lostSummary } from './lostReferences';
import { failureNote, hasRecognitionNotes, shortReason, unusedLinesNote } from './recognition';
import { NOT_IN_RAIL } from './invoiceSites';

/**
 * Индикатор состояния счёта (costs-invoice-screen-11, D-11.1, ТЗ COST-6.4): все сообщения о счёте — пунктами
 * одного перечня, с уровнем, местом и одним действием. Чистая функция: React и запросов здесь нет, всё
 * приходит аргументом.
 *
 * <p>⚠️ <b>Своих правил здесь нет.</b> Готовность, «мешает чему», «просрочен» и «кто изменил» — слова сервера
 * (`readiness`, `blocks`, `due`, `updatedBy`); «изменили, пока вы правили», «справочник не прочитан» и
 * подсказки запомненного — состояние формы. Здесь только раскладка: какой пункт какого уровня, куда он
 * ведёт и какой фразой индикатор говорит о худшем.</p>
 *
 * <p>Отказ чтения — пункт-отказ, а не пропуск: «состояние распознавания не пришло», «запомненное не
 * проверено», «схема типа не прочитана». Вид пункта готовности, которого форма не знает, приходит пунктом со
 * словами сервера.</p>
 */

/**
 * Уровень пункта (D3 зонтика): `blocking` — «Мешает работе», `gate` — «Мешает «Разобран» / оплате»,
 * `check` — «Проверьте», `info` — «К сведению» (в число не идёт).
 */
export type StatusLevel = 'blocking' | 'gate' | 'check' | 'info';

export const LEVELS: readonly StatusLevel[] = ['blocking', 'gate', 'check', 'info'];

export const LEVEL_TITLE: Record<StatusLevel, string> = {
  blocking: 'Мешает работе',
  gate: 'Мешает «Разобран» и оплате',
  check: 'Проверьте',
  info: 'К сведению',
};

/** Тон пометки по уровню (выбор владельца — макет А, D-11.3). */
export const LEVEL_TONE: Record<StatusLevel, StatusTone> = {
  blocking: 'error', gate: 'warning', check: 'warning', info: 'neutral',
};

/** Место формы: поле, строки (или названная строка), разноска либо часть целиком. */
export type StatusPlace = ReadinessTarget;

/**
 * Что делает действие пункта. Исполняет его форма: здесь — только что и с какой подписью. `short` — подпись в
 * перечне, где действие стоит справа от текста в листе шириной 400: длинная оставляла тексту пункта по слову
 * в строке (снимки «после»); полная — подсказкой кнопки и в banner.
 */
export type StatusAction = { label: string; short?: string } & (
  | { kind: 'goto'; place: StatusPlace }
  | { kind: 'open'; invoiceId: string }
  | { kind: 'delete'; reason: DeleteReason }
  | {
    kind: 'close' | 'reread' | 'retry-view' | 'retry-orgs' | 'recognize' | 'retry-recognition'
      | 'confirm-marks' | 'apply-matches' | 'retry-matches' | 'retry-extras' | 'show-all' | 'periods';
  });

/** Чему пункт мешает — пометкой «мешает: Разобран · оплата». */
export type StatusBlock = 'parsed' | 'pay';

export interface StatusItem {
  /** Устойчивый ключ — для списка и тестов. */
  key: string;
  level: StatusLevel;
  /** Пункт одной строкой. */
  text: string;
  /** Пояснение — второй строкой перечня и продолжением в banner. */
  detail?: string;
  /** Имя пункта во фразе индикатора, когда он худший «Мешает работе»: «Не распознан», «Счёт удалён». */
  name?: string;
  blocks?: StatusBlock[];
  /** Первое — действие пункта в перечне; banner берёт до двух. Пусто — пункт не ведёт. */
  actions: StatusAction[];
  /** Почему действия нет — подсказкой у пункта (нет права). */
  why?: string;
  /** Тон banner, если он отличен от тона уровня: «изменили» — предупреждение, а не ошибка. */
  bannerTone?: 'error' | 'warning';
}

/** Что таблица строк знает о запомненном (C3): только о сохранённом поставщике. */
export interface MatchFacts {
  hasSupplier: boolean;
  /** Строк без позиции. */
  waiting: number;
  /** Скольким запомненное можно подставить. */
  ready: number;
  /** У скольких запомненная позиция в архиве или удалена. */
  blocked: number;
  /** Вопрос о запомненном не прошёл — словами отказа. */
  failed: string | null;
}

/** Чья часть набрана поверх прежнего вида счёта. */
export interface StaleParts {
  header: boolean;
  lines: boolean;
  correction: boolean;
}

export interface StatusInput {
  view: InvoiceView;
  recognition: InvoiceRecognition | undefined;
  /** Состояние распознавания не пришло. ⚠️ Это не «не распознан». */
  recognitionUnread: boolean;
  form: {
    gone: boolean;
    stale: StaleParts;
    /** Справочник организаций не прочитан — словами отказа. */
    orgsFailed: string | null;
    /** Открытый счёт не перечитался — словами отказа. */
    rereadFailed: string | null;
    /** Счёт скрыт отбором списка («Избранные стройки»). */
    hiddenByFilter: boolean;
    /** Схема типа «Счёт» не прочитана — словами отказа. */
    extrasFailed: string | null;
    /** Запомненное по строкам; `null` — сказать нечего (строк нет, строки не правятся). */
    matches: MatchFacts | null;
    /** Метки «со скана» у полей формы — то, что снимет «Всё верно». */
    marks: readonly string[];
    /** Подписи дописанных в тип полей с метками; поля модуля подписаны `FIELD_LABEL`. */
    markTitles?: Readonly<Record<string, string>>;
    /** Исправление закрытого периода идёт. */
    correcting: boolean;
    can: {
      /** Право вводить счета (без поправки на удалённый счёт). */
      edit: boolean;
      pay: boolean;
      allocate: boolean;
      correct: boolean;
      closePeriod: boolean;
      /** У этого счёта есть «Исправить» (закрытый период, право). */
      correctable: boolean;
      /** Удалить счёт можно (черновик, право, не заперт). */
      delete: boolean;
    };
  };
}

/** Во что индикатор сводит перечень: тон, фраза, число. */
export type StatusMood = StatusLevel | 'running' | 'ready' | 'none';

export interface InvoiceStatus {
  /** Худшее: уровень худшего пункта либо «распознаётся», «готов», «сказать нечего». Его смену объявляет диктор. */
  mood: StatusMood;
  tone: StatusTone;
  /** Пунктов вне «К сведению». */
  count: number;
  /** Фраза индикатора; пустая — только пометка тона (разобранный без пунктов). */
  phrase: string;
  /** Доля распознавания, если оно идёт. */
  running: boolean;
  items: StatusItem[];
}

const capital = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);

/** «Кто и когда изменил» — словами сервера (`updatedBy`); автора нет — только время. */
export function authorOf(view: InvoiceView): string {
  const when = formatDateTime(view.updatedAt);
  const by = view.updatedBy;
  if (!by) return when;
  const who = by.mine ? 'вы, в другом окне'
    : by.via === 'recognition' ? 'распознавание'
      : by.via === 'system' ? 'система'
        : by.name ?? 'другой пользователь';
  return `${who}, ${when}`;
}

/** Подпись перехода по месту. */
function gotoLabel(place: StatusPlace, many = false): string {
  switch (place.place) {
    case 'field': return 'К полю';
    case 'lines': return place.line != null && !many ? 'К строке' : 'К строкам';
    case 'allocation': return 'К разноске';
    case 'part': return 'Показать';
  }
}

/** Пункт готовности одной строкой: у поля — «Не заполнено: плательщик», остальное — словами сервера. */
function gapText(kind: string, label: string): string {
  if (kind === 'field') return `Не заполнено: ${label}`;
  if (kind === 'lines' && label === 'строки') return 'Строк нет';
  if (kind === 'allocation' && label === 'разноска') return 'Строки не разнесены по стройкам';
  return capital(label);
}

/**
 * Где первое удалённое место счёта — туда ведёт пункт удалённых записей. Порядок — порядок формы: шапка,
 * строки, разноска.
 */
function lostPlace(view: InvoiceView): StatusPlace {
  const refs = view.references;
  if (refs?.supplier === 'lost') return { place: 'field', key: K.supplier };
  if (refs?.payer === 'lost') return { place: 'field', key: K.payer };
  const line = view.lines.find(l => (l.nomenclatureLost && l.nomenclatureIssue !== 'moved')
    || l.allocation.parts.some(p => p.targetLost || (p.targetIssue?.endsWith('-lost') ?? false)));
  if (line) return { place: 'lines', line: line.ordinal };
  return { place: 'allocation' };
}

/** Порядок внутри уровня — по месту в форме (часть, затем строка); пункт без места — о счёте целиком, первым. */
function rankOf(item: StatusItem): number {
  const place = item.actions.find(a => a.kind === 'goto')?.place;
  if (!place) return -1;
  const part = partOfTarget(place);
  const index = part ? PART_ORDER.indexOf(part) : PART_ORDER.length;
  const line = place.place === 'lines' ? place.line ?? 0 : 0;
  return index * 100_000 + line;
}

const marksText = (n: number) =>
  `${n} ${ruPlural(n, 'значение', 'значения', 'значений')} со скана не ${ruPlural(n, 'проверено', 'проверены', 'проверены')}`;

/**
 * Перечень и фраза индикатора по сохранённому счёту и состоянию формы. Число — пункты вне «К сведению».
 *
 * <p>⚠️ `readiness == null` (разобран, отклонён, заперт) и `[]` у черновика — разные вещи: первое молчит о
 * готовности, второе говорит «Готов к «Разобран»».</p>
 */
export function statusOf({ view, recognition, recognitionUnread, form }: StatusInput): InvoiceStatus {
  const items: StatusItem[] = [];
  const add = (item: StatusItem) => items.push(item);
  const { can, gone } = form;
  const closed = view.payment.lockedBy !== null;
  const editable = can.edit && !gone;
  const state = asInput(view.requisites[K.state]);
  const running = !gone && recognition?.state === 'running';

  // ── Мешает работе ──────────────────────────────────────────────────────────────────────────────────
  if (gone)
    add({
      key: 'gone', level: 'blocking', name: 'Счёт удалён', text: 'Счёт удалён, пока был открыт у вас',
      detail: 'Набранное осталось на экране: перенесите нужное себе. Сохранить его уже некуда.',
      actions: [{ kind: 'close', label: 'Закрыть счёт' }],
    });

  const stale = [form.stale.header && 'поля счёта', form.stale.lines && 'строки', form.stale.correction && 'исправление']
    .filter((s): s is string => typeof s === 'string');
  if (!gone && stale.length > 0)
    add({
      key: 'stale', level: 'blocking', name: 'Счёт изменили', bannerTone: 'warning',
      text: `Счёт тем временем изменили — ${authorOf(view)}`,
      detail: `${capital(stale.join(', '))} на экране собраны по прежнему виду и поверх чужой правки не сохранятся. `
        + 'Набранное остаётся: перенесите нужное себе, затем перечитайте счёт — несохранённые правки заменятся сохранёнными.',
      actions: [{ kind: 'reread', label: 'Перечитать счёт' }],
    });

  if (form.rereadFailed !== null)
    add({
      key: 'reread-failed', level: 'blocking', name: 'Не перечитался', text: `Счёт не перечитался: ${form.rereadFailed}`,
      detail: 'На экране — последний прочитанный вид счёта.',
      actions: [{ kind: 'retry-view', label: 'Повторить' }],
    });

  if (form.orgsFailed !== null)
    add({
      key: 'orgs', level: 'blocking', name: 'Справочник не прочитан',
      text: `Справочник организаций не прочитан: ${form.orgsFailed}`,
      detail: 'Поставщика и плательщика выбрать не из чего — остальные поля правятся и сохраняются. Это не «организаций '
        + 'нет»: их список сюда не доехал.',
      actions: [{ kind: 'retry-orgs', label: 'Повторить' }],
    });

  // Распознавание (issue #1077). Запертому и удалённому — не о чем: ни повтора, ни правки.
  if (!closed && !gone && recognition && !form.correcting) recognitionItems(recognition, editable, can.delete).forEach(add);
  if (!closed && !gone && recognitionUnread)
    add({
      key: 'recognition-unread', level: 'check', text: 'Состояние распознавания не пришло',
      detail: 'Это не «не распознан»: ответ о распознавании сюда не доехал.',
      actions: [{ kind: 'retry-recognition', label: 'Повторить' }],
    });

  // Удалённые записи справочников (issue #1184): сводкой «в N местах», ведёт к первому.
  const lost = lostSummary(view);
  if (lost) {
    // Без права правки удалённую запись не заменить: работы, которой она «мешает», у человека нет (ревью PR #1792).
    const locked = gone ? 'gone' : closed && !form.correcting ? (can.correctable ? 'correct' : 'closed') : !editable ? 'read' : null;
    const place = lostPlace(view);
    if (lost.count > 0)
      add({
        key: 'lost', level: locked === null ? 'blocking' : 'info', name: 'Удалённые записи',
        text: `Удалённые записи справочников — в ${lost.count} ${lost.count === 1 ? 'месте' : 'местах'}: ${lost.places}`,
        detail: locked === 'correct'
          ? 'Счёт закрытого периода: запись заменяют исправлением с причиной — «Исправить».'
          : locked === 'closed' ? 'Счёт закрытого периода, здесь это не исправить.'
            : locked === 'read' ? 'Заменить запись может тот, у кого есть право вводить счета.'
            : locked === 'gone' ? 'Счёт удалён, исправить это нельзя.'
              : 'Счёт сохраняется и так; исправить — выбрать в поле другую запись. Дополнительные поля типа счёта на '
                + 'удалённые записи не проверяются.',
        actions: locked === null ? [{ kind: 'goto', label: gotoLabel(place), place }] : [],
      });
    if (lost.others.length > 0)
      add({ key: 'lost-others', level: 'check', text: `Запись на месте, но не та: ${lost.others.join('; ')}`, actions: [] });
    if (lost.unchecked.length > 0)
      add({
        key: 'lost-unchecked', level: 'info', text: `Не проверено: ${lost.unchecked.join('; ')}`,
        detail: 'Есть ли эти записи, сказать нельзя — справочник не прочитан.', actions: [],
      });
  }

  // ── Мешает «Разобран» / оплате ──────────────────────────────────────────────────────────────────────
  // Перечень готовности — сервера (#1291); запертому и удалённому его нет: «Разобран» откажет замок.
  const gaps = closed || gone ? null : view.readiness?.gaps ?? null;
  const readiness = gaps === null ? null : readinessItems(view, { edit: editable, allocate: can.allocate }) ?? [];
  let payNamed = false;
  gaps?.forEach((gap, index) => {
    const item = readiness![index];
    // Старый сервер `blocks` не присылает — пункт готовности мешает «Разобран» всегда.
    const blocks: StatusBlock[] = (gap.blocks ?? ['parsed']).map(b => (b === 'payment' ? 'pay' : 'parsed'));
    if (blocks.includes('pay')) payNamed = true;
    add({
      key: `gap:${item.id}`, level: 'gate', text: gapText(gap.kind, gap.label), blocks,
      actions: item.target ? [{ kind: 'goto', label: gotoLabel(item.target, (gap.lines?.length ?? 0) > 1), place: item.target }] : [],
      why: item.why,
    });
  });

  // Отказ оплаты, который не назван пунктом готовности (сервер пометил бы его `payment`), — своим пунктом.
  // Пока сумма к оплате не обязательна для «Разобран», отказ «нет суммы» пунктом готовности не покрыт.
  const payment = view.payment;
  // Без права оплаты отказ оплаты ничему не мешает — его не показывала и прежняя часть «Сроки и оплата».
  const payItem = can.pay && !payment.paid && !!payment.refusal && payment.refusalKind !== 'rejected' && !payNamed && !gone;
  if (payItem)
    add({
      key: 'pay-refusal', level: 'gate', text: `Оплатить нельзя: ${payment.refusal}`, blocks: ['pay'],
      actions: payment.refusalKind === 'amount' && !closed
        ? [{ kind: 'goto', label: 'К полю', place: { place: 'field', key: K.total } }] : [],
    });

  // Расхождение сверх допуска у счёта, о котором готовность молчит (разобран — после понижения допуска или
  // правки суммы; оплачен): прежде его называла сводка разноски, теперь — этот пункт, если не назвал другой.
  const alloc = view.allocation;
  const discrepancyNamed = (gaps?.some(gap => gap.kind === 'total') ?? false)
    || (payItem && payment.refusalKind === 'discrepancy');
  if (!gone && !discrepancyNamed && !alloc.withinTolerance && alloc.discrepancy !== null && view.lines.length > 0
    && state !== REJECTED)
    add({
      key: 'discrepancy', level: 'check',
      text: `Сумма строк расходится с суммой к оплате на ${formatMoney(Math.abs(alloc.discrepancy))} — больше допуска ${formatMoney(alloc.tolerance)}`,
      detail: 'Сверх допуска счёт не считается разнесённым: суммы по стройкам не сойдутся со счётом.',
      actions: editable && !closed ? [{ kind: 'goto', label: 'К полю', place: { place: 'field', key: K.total } }] : [],
    });

  // ── Проверьте ───────────────────────────────────────────────────────────────────────────────────────
  twinItems(view, can.delete).forEach(add);

  if (form.marks.length > 0) {
    const names = form.marks.map(key => `«${FIELD_LABEL[key] ?? form.markTitles?.[key] ?? key}»`).join(', ');
    add({
      key: 'marks', level: 'check', text: `${marksText(form.marks.length)}: ${names}`,
      detail: 'Сверьте их с файлом: правка поля снимает метку с него, «Всё верно» — со всех.',
      actions: editable && !closed && !form.correcting
        ? [{ kind: 'confirm-marks', label: `Всё верно (${form.marks.length})` }] : [],
    });
  }

  if (view.due?.overdue && !payment.paid) {
    const days = view.due.daysLeft != null && view.due.daysLeft < 0 ? -view.due.daysLeft : null;
    const due = asInput(view.requisites[K.dueDate]).slice(0, 10);
    const late = days !== null ? `просрочен на ${days} дн.` : 'просрочен';
    add({
      key: 'overdue', level: 'check', name: capital(late),
      text: `Срок оплаты${due ? ` ${formatDate(due)}` : ''} прошёл — ${late}`,
      actions: [{ kind: 'goto', label: 'К полю', place: { place: 'field', key: K.dueDate } }],
    });
  }

  if (form.extrasFailed !== null)
    add({
      key: 'extras', level: 'check', text: `Схема типа «Счёт» не прочитана: ${form.extrasFailed}`,
      detail: 'Поля, дописанные в тип, не показаны — их значения в счёте сохраняются как есть.',
      actions: [{ kind: 'retry-extras', label: 'Повторить' }],
    });

  const matches = form.matches;
  if (matches) {
    if (!matches.hasSupplier)
      add({
        key: 'matches-supplier', level: 'info', text: 'Поставщик не выбран — запомненное подставить не по чему',
        detail: 'Выбор позиций в этом счёте не запомнится. Выберите поставщика и сохраните счёт.',
        actions: [{ kind: 'goto', label: 'К полю', place: { place: 'field', key: K.supplier } }],
      });
    else if (matches.failed !== null)
      add({
        key: 'matches-failed', level: 'info', text: `Запомненное не проверено: ${matches.failed}`,
        detail: 'Это не «ничего не запомнено» — позиции можно выбрать руками.',
        actions: [{ kind: 'retry-matches', label: 'Повторить' }],
      });
    else {
      if (matches.ready > 0)
        add({
          key: 'matches-ready', level: 'info',
          text: `У поставщика запомнено для ${matches.ready} из ${matches.waiting} ${ruPlural(matches.waiting, 'строки', 'строк', 'строк')} без позиции`,
          actions: [{ kind: 'apply-matches', label: `Подставить запомненное (${matches.ready})`, short: `Подставить (${matches.ready})` }],
        });
      if (matches.blocked > 0)
        add({
          key: 'matches-blocked', level: 'check',
          text: `Запомненная позиция в архиве или удалена — у ${matches.blocked} ${ruPlural(matches.blocked, 'строки', 'строк', 'строк')}`,
          detail: 'Такая позиция не подставляется: выберите её в строке.',
          actions: [{ kind: 'goto', label: 'К строкам', place: { place: 'lines' } }],
        });
    }
  }

  // ── К сведению ──────────────────────────────────────────────────────────────────────────────────────
  if (form.hiddenByFilter)
    add({ key: 'hidden', level: 'info', text: NOT_IN_RAIL, actions: [{ kind: 'show-all', label: 'Показать все' }] });

  if (closed)
    add({
      key: 'locked', level: 'info', text: `Счёт заперт: ${payment.lockedBy}`,
      detail: [
        'Поля, строки и разноску обычной правкой не изменить.',
        can.edit && can.correct && 'Изменить их можно исправлением — «Исправить»: оно ляжет в книгу, закрытый месяц не изменится.',
        can.pay && can.correct && 'Оплату отменить можно: отмена ляжет в книгу исправлений.',
      ].filter(Boolean).join(' '),
      actions: can.closePeriod ? [{ kind: 'periods', label: 'Учётный период' }] : [],
    });

  if (!can.edit)
    add({ key: 'read-only', level: 'info', text: 'Только чтение: права вводить счета нет', actions: [] });

  // ── Сортировка и фраза ──────────────────────────────────────────────────────────────────────────────
  const sorted = LEVELS.flatMap(level => items
    .map((item, order) => ({ item, order }))
    .filter(({ item }) => item.level === level)
    .sort((a, b) => rankOf(a.item) - rankOf(b.item) || a.order - b.order)
    .map(({ item }) => item));

  const counted = sorted.filter(item => item.level !== 'info');
  const worst = sorted[0];
  const count = counted.length;
  const progress = recognition?.progress ? ` ${recognition.progress}` : '';

  if (worst?.level === 'blocking')
    return { mood: 'blocking', tone: 'error', count, phrase: worst.name ?? worst.text, running, items: sorted };
  if (count > 0) {
    // Единственное замечание — просрочка: её имя говорит больше, чем «1 замечание».
    const only = count === 1 && counted[0].key === 'overdue' ? counted[0].name : undefined;
    return {
      mood: counted[0].level, tone: 'warning', count, running, items: sorted,
      phrase: only ?? `${count} ${ruPlural(count, 'замечание', 'замечания', 'замечаний')}`,
    };
  }
  if (running) return { mood: 'running', tone: 'primary', count, phrase: `Распознаётся…${progress}`, running, items: sorted };
  if (readiness !== null && readiness.length === 0 && state === DRAFT)
    return { mood: 'ready', tone: 'success', count, phrase: 'Готов к «Разобран»', running, items: sorted };
  if (sorted.length > 0) return { mood: 'info', tone: 'neutral', count, phrase: 'К сведению', running, items: sorted };
  return { mood: 'none', tone: 'neutral', count, phrase: '', running, items: sorted };
}

/** Пункты распознавания: идёт, сбой, «по прежнему виду», замечания. */
function recognitionItems(recognition: InvoiceRecognition, editable: boolean, canDelete: boolean): StatusItem[] {
  const again = editable && recognition.canStart;
  const cannot = editable && !recognition.canStart && recognition.whyNot ? ` Распознать ещё раз нельзя: ${recognition.whyNot}.` : '';
  switch (recognition.state) {
    case 'running':
      return [{
        key: 'recognition-running', level: 'info',
        text: `Распознаётся…${recognition.progress ? ` ${recognition.progress}` : ''}`,
        detail: 'Форму можно заполнять и пока файл читается.', actions: [],
      }];
    case 'failed': {
      const note = failureNote(recognition);
      // Файл не прочитан вовсе или сам не читается (#1343) — чаще всего приложили не тот файл.
      const wrongFile = canDelete && (recognition.reason === 'NoAnswer' || recognition.reason === 'BadFile');
      const actions: StatusAction[] = [
        // Сам файл не читается: повтор на нём даст то же — путь только другой файл.
        ...(again && recognition.reason !== 'BadFile' ? [{ kind: 'recognize' as const, label: 'Распознать ещё раз' }] : []),
        ...(wrongFile ? [{ kind: 'delete' as const, label: 'Не тот файл — удалить черновик…', short: 'Удалить черновик…', reason: 'wrong-file' as const }] : []),
      ];
      // «Не настроено» — тихо нарочно: это состояние установки, а не счёта.
      return [{
        key: 'recognition-failed', level: note.quiet ? 'info' : 'blocking', name: 'Не распознан',
        // Пояснение — без первой фразы: её говорит сам пункт.
        text: `Файл не распознан: ${shortReason(recognition.reason)}`,
        detail: `${note.text.replace(/^[^.]*\.\s*/, '')}${cannot}`.trim() || undefined, actions,
      }];
    }
    case 'done': {
      const found: StatusItem[] = [];
      if (recognition.byFormerImage)
        found.push({
          key: 'recognition-former', level: 'info', text: 'Распознано по прежнему виду файла',
          detail: `Вид для чтения с тех пор построен заново. Прочитанное осталось в счёте как было.${cannot}`,
          actions: again ? [{ kind: 'recognize', label: 'Распознать ещё раз' }] : [],
        });
      // Замечания форма рисует только тому, кто правит: без права «Показать» вела бы в никуда (ревью PR #1792).
      if (editable && hasRecognitionNotes(recognition)) {
        const n = recognition.notes.length + (unusedLinesNote(recognition) ? 1 : 0) + (recognition.unplaced?.length ?? 0);
        found.push({
          key: 'recognition-notes', level: 'info', text: `Замечания распознавания и не легшее в счёт: ${n}`,
          actions: [{ kind: 'goto', label: 'Показать', place: { place: 'part', part: 'who' } }],
        });
      }
      return found;
    }
    default:
      return [];
  }
}

/**
 * Двойники (ТЗ COST-6.2): тот же файл и те же поставщик, номер и дата. Один «другой» счёт — один раз. Удаление
 * (короткий путь #1282) — действием пункта, где удалить можно; «Открыть» тогда — сама строка перечня.
 */
function twinItems(view: InvoiceView, canDelete: boolean): StatusItem[] {
  const { sameFile, both, duplicates } = twinsOf(view);
  const found: StatusItem[] = [];
  const openLabel = (number: string | null) => (number ? `Открыть ${number}` : 'Открыть счёт');
  if (sameFile.length > 0) {
    // У разобранного пара с одним файлом скорее законная (в файле несколько счетов) — если не совпали ещё и
    // поставщик, номер и дата: это самый сильный признак дубля.
    const loud = view.requisites[K.state] === DRAFT || both.size > 0;
    const first = sameFile[0];
    found.push({
      key: 'same-file', level: loud ? 'check' : 'info',
      text: `${sameFile.length > 1 ? `Тот же файл приложен ещё к ${sameFile.length} счетам` : 'Тот же файл приложен ещё к счёту'}: `
        + sameFile.map(sameFileLabel).join('; '),
      detail: [
        both.size > 0 && 'Поставщик, номер и дата тоже совпадают.',
        loud && 'Если в файле несколько счетов — так и оставьте.',
      ].filter(Boolean).join(' ') || undefined,
      actions: [
        ...(loud && canDelete ? [{ kind: 'delete' as const, label: 'Загружен дважды — удалить этот черновик…', short: 'Удалить черновик…', reason: 'same-file' as const }] : []),
        { kind: 'open', label: openLabel(first.number), invoiceId: first.id },
      ],
    });
  }
  if (duplicates.length > 0) {
    const first = duplicates[0];
    found.push({
      key: 'duplicate', level: 'check',
      text: `Похоже на дубликат: ${duplicates.map(duplicateLabel).join(', ')}`,
      detail: 'У этого поставщика уже есть счёт с тем же номером и датой. Сохранить всё равно можно: одинаковые номера '
        + 'у поставщика бывают.',
      actions: [
        ...(canDelete && sameFile.length === 0 ? [{ kind: 'delete' as const, label: 'Это дубль — удалить этот счёт…', short: 'Удалить счёт…', reason: 'duplicate' as const }] : []),
        { kind: 'open', label: openLabel(first.number), invoiceId: first.id },
      ],
    });
  }
  return found;
}

/** Banner над частями: худший пункт «Мешает работе» и сколько их ещё. `null` — такого пункта нет. */
export function bannerOf(status: InvoiceStatus): { item: StatusItem; rest: number } | null {
  const blocking = status.items.filter(item => item.level === 'blocking');
  return blocking.length === 0 ? null : { item: blocking[0], rest: blocking.length - 1 };
}

/** Кратко для подсказки индикатора: фраза и первые пункты. */
export function briefOf(status: InvoiceStatus): string {
  const named = status.items.filter(item => item.level !== 'info').slice(0, 2).map(item => item.text);
  const head = status.phrase || 'Состояние счёта';
  const rest = status.count - named.length;
  const list = named.length > 0 ? ` — ${named.join('; ')}${rest > 0 ? ` и ещё ${rest}` : ''}` : '';
  return `${head}${list}. Щёлкните — весь перечень`;
}

/** Пометка «мешает: Разобран · оплата». */
export function blocksText(blocks: readonly StatusBlock[]): string {
  return `мешает: ${blocks.map(b => (b === 'parsed' ? 'Разобран' : 'оплата')).join(' · ')}`;
}
