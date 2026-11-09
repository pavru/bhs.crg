import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';

import { EditNote, LockIcon, UploadFile } from '@/shared/ui/icons';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { useSnackbar } from '@/shared/ui/Snackbar';
import type { RowAction } from '@/shared/ui/RowActionsMenu';
import { apiError } from '@/shared/utils/apiError';
import { useCan } from '@/shared/api/access';
import { formatDate, formatDateTime } from '@/shared/format/format';
import {
  useConfirmInvoiceFields, useInvoiceState, useUpdateInvoice,
  type CostsOrganization, type InvoiceView,
} from '@/shared/api/invoices';
import {
  BLOCKS, FIELD_LABEL, K, PARSED, REJECTED, VAT_MODE_MARK, asInput, refEntryId, toRequisites, unconfirmedOutsideBlocks, type InvoiceBlock,
} from './invoiceFields';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { supplierChangeOf } from './supplierMatches';
import { goTo, readinessItems, type ReadinessTarget } from './invoiceReadiness';
import { useDraftBase } from './draftBase';
import { InvoiceObject } from './InvoiceObject';
import { InvoicePayButton, InvoicePayment, PayDialog } from './InvoicePayment';
import type { QueueExit } from './invoiceQueues';
import { InvoiceMoreMenu } from './InvoiceMoreMenu';
import { InvoiceDeleteProvider } from './InvoiceDeleteDialog';
import { canDelete, useFresh } from './invoiceDelete';
import { undoVersion } from './invoiceReject';
import { InvoiceFileDialog } from './InvoiceFileDialog';
import { RecognitionNotes } from './InvoiceRecognitionNote';
import { useRecognitionAction } from './recognitionAction';
import { RecognitionContext, useRecognitionWatch } from './recognitionWatch';
import { explainedByRecognition, hasRecognitionNotes } from './recognition';
import { SiteRevealScope } from '@/shared/siteFilter/SiteOptions';
import { CorrectionBar } from './CorrectionBar';
import { CorrectionHistory } from './CorrectionHistory';
import { CorrectionContext, useCorrectionDraft } from './useCorrectionDraft';
import { mayCorrect } from './correction';
import { UnshownExtras } from './InvoiceExtraFields';
import { InvoiceExtrasProvider, useInvoiceExtras } from './invoiceExtras';
import { shippedBeforeIssued } from './invoiceDue';
import { AllocationSummary } from './LineAllocation';
import { BlockFields, ConfirmBlock } from './InvoiceFormFields';
import { InvoicePart } from './InvoicePart';
import {
  PART_ORDER, PART_TITLE, PartRevealContext, attentionOf, initiallyOpen, partLine, partOfTarget, type PartExtra, type PartId,
} from './invoiceParts';
import { InvoiceSummary, type SummaryPrimary } from './InvoiceSummary';
import { parsedHold, payHold, primaryOf, summaryMeta } from './invoicePrimary';
import { editsAfterSave, saveModel, useInvoiceSave } from './invoiceSave';
import { invoiceLeave } from './invoiceLeave';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { InvoiceStatusIndicator } from './InvoiceStatusIndicator';
import { InvoiceStatusBanner, InvoiceStatusSheet } from './InvoiceStatusSheet';
import { useInvoiceStatus } from './useInvoiceStatus';

/**
 * Форма ввода счёта (задача C1, issue #1076; раскладка — costs-invoice-screen-10-layout, вариант Г, ТЗ
 * COST-6.2).
 *
 * <p><b>Неподвижна только сводка</b> (≤ 64): номер, пометки, поставщик · дата · сумма, «Сохранить», одно
 * основное действие и «⋮». Всё остальное — одной прокруткой частей в порядке работы: «Кто и сколько»,
 * «Строки», «Разноска», «Сроки и оплата», «Ещё», «История». Заполненная часть открывается свёрнутой в
 * строку-итог (`invoiceParts`).</p>
 *
 * <p><b>Сообщения о счёте — индикатор состояния в сводке</b> (costs-invoice-screen-11, ТЗ COST-6.4): пунктами
 * одного перечня, и над частями — не больше одного banner, только при «Мешает работе». Плашек в форме нет.</p>
 *
 * <p><b>Сохранение не блокируется ничем.</b> Ни пустых обязательных полей, ни строк: черновик обязан
 * сохраняться и попадать в реестр со сроком. Шапку и строки пишет одна очередь, шапка раньше строк
 * (`invoiceSave`, D-12.1). Черновик пишется сам — по уходу с поля и из строки (costs-invoice-screen-12-autosave,
 * ТЗ COST-6.5); где автосохранения нет (D-12.3), пишет кнопка «Сохранить» той же очередью.</p>
 *
 * <p><b>Метки «распознано, не подтверждено»</b> приходят с сервера и переживают повторное открытие
 * черновика. Конец у метки двойной: правка поля снимает её с этого поля, «Всё верно» — со всей части.</p>
 *
 * <p>⚠️ Правки прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ: страница передаёт `key={view.id}`. Не эффект
 * со сбросом состояния — он рисует лишний кадр с чужими правками.</p>
 */
export function InvoiceForm({
  view, organizations, organizationsError, onRetryOrganizations, onOpenInvoice, gone = false, onClose, onExit, onUnsaved, onDeleteLater,
  focusHeader = false, nav, fromFile, file, onShowFile, pageActions, compactLines = false, hiddenByFilter, rereadFailed, onFlush,
}: {
  /**
   * Дописать форму перед сменой счёта (D-12.7): форма, что пишет сама, отдаёт сюда свою запись, иначе — `null`, и
   * смена счёта спрашивает, как уход. Обязана быть стабильной.
   */
  onFlush?: (flush: (() => Promise<boolean>) | null) => void;
  view: InvoiceView;
  /** «Разобран» и «Отклонить» идут через страницу — она знает отбор и список (issue #1293): запирает
   *  форму на время записи и выбора следующего счёта и говорит о переходе. `otherwise` зовётся, когда
   *  сама страница ничего не сказала. Обещание кончается ответом на запись. `back` — версия, с которой
   *  «Вернуть» возвращает вышедший счёт в черновик (D-13.1); нет её — «Вернуть» не строится. */
  onExit?: <T>(
    how: QueueExit, send: () => Promise<T>, otherwise: (result: T, undo?: () => void) => void,
    back?: (result: T) => string | null,
  ) => Promise<T>;
  /** Удалить новый черновик без окна — страница прячет его и показывает «Вернуть» (D-13.4). Нет — окном всегда. */
  onDeleteLater?: (view: InvoiceView) => void;
  /** Сюда привёл переход от соседнего счёта очереди: фокус — на сводку, а не в начало страницы. */
  focusHeader?: boolean;
  /** Есть ли в форме несохранённое — в шапке, в строках или в исправлении. Страница спрашивает об этом
   *  в тот миг, когда собирается сменить счёт (ревью PR #1303). Обязана быть стабильной. */
  onUnsaved?: (unsaved: boolean) => void;
  /** Счёт удалили, пока он был открыт (issue #1282): набранное остаётся, действий записи нет. */
  gone?: boolean;
  /** Закрыть форму: после удаления и по «Закрыть счёт» у счёта, которого больше нет. */
  onClose: () => void;
  organizations: CostsOrganization[];
  /** Отказ чтения справочника организаций, если он был. ⚠️ Пустой список и «справочник не
   *  прочитан» — разные вещи: сервер их различает нарочно. */
  organizationsError?: unknown;
  onRetryOrganizations?: () => void;
  /** Счёт скрыт отбором списка — «Показать все» (пункт «К сведению»). */
  hiddenByFilter?: () => void;
  /** Открытый счёт не перечитался — пункт «Мешает работе» с «Повторить». */
  rereadFailed?: { error: string; retrying: boolean; onRetry: () => void };
  onOpenInvoice: (id: string) => void;
  /** Слоты сводки — их наполняет страница (D-10.3, D-10.4): «‹ N из M ›», «Счёт из файла», «Файл». */
  nav?: ReactNode;
  fromFile?: ReactNode;
  file?: ReactNode;
  /** Показать файл счёта — «Показать на скане» у пунктов перечня о метках (11, D-11.4). Нет — файла нет. */
  onShowFile?: () => void;
  /** Действия страницы в «⋮» сводки, когда списка нет (D-10.8). */
  pageActions?: RowAction[];
  /** Рядом список счетов — таблица строк в плотном виде (D-10.10). */
  compactLines?: boolean;
}) {
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const update = useUpdateInvoice();
  const confirm = useConfirmInvoiceFields();
  const state = useInvoiceState();
  const toast = useSnackbar();
  const correction = useCorrectionDraft();

  const value = (key: string): unknown => (key in edits ? edits[key] : view.requisites[key]);
  const set = (key: string, next: unknown) => setEdits(prev => ({ ...prev, [key]: next }));
  const dirty = Object.keys(edits).length > 0;
  // Поставщик в шапке изменён и не сохранён — таблице строк (#1657, D3): запомненное прежнего не про этот счёт.
  const supplierChange = useMemo(() => supplierChangeOf(edits, view.requisites), [edits, view.requisites]);
  const [linesDirty, setLinesDirty] = useState(false);

  // Правки шапки лежат поверх вида (issue #1176): «моя часть» — только поля, которые человек правит
  // (ревью PR #1208), подпись — реквизиты целиком.
  const base = useDraftBase(view, of => of.requisites, dirty, {
    same: (a, b) => Object.keys(edits).every(key => JSON.stringify(a[key]) === JSON.stringify(b[key])),
  });

  function reread() {
    setEdits({});
    base.rebase();
  }

  const scan = useMemo(() => scanOf(view), [view]);
  // Распознавание скана (issue #1077): вид, пришедший перечитыванием после исхода, — правка
  // распознавания, а не чужая, если её объясняет распознавание (поле было пустым и легло с меткой).
  const watch = useRecognitionWatch(view.id, scan !== null && !gone);
  const byScan = base.stale && watch.accepted === view.version
    && explainedByRecognition(edits, base.signature, view);
  if (byScan) base.rebase();

  const parsed = view.requisites[K.state] === PARSED;
  const rejected = view.requisites[K.state] === REJECTED;
  // Заперт — слово сервера (`lockedBy`), а не «оплачен»: оплаченный счёт открытого периода правится.
  const closed = view.payment.lockedBy !== null;
  // Без права вводить счета форма читается так же, как запертая (N1, issue #1102). Счёта больше нет —
  // то же чтение: сохранять некуда.
  const can = useCan();
  const mayEdit = can.permission('costs.invoice.edit');
  const canEdit = mayEdit && !gone;
  const canPay = can.permission('costs.invoice.pay');
  const allocate = can.permission('costs.allocation.edit');
  // Запертый счёт правится ИСПРАВЛЕНИЕМ (E2, issue #1100): та же форма, переключённая на месте.
  const correctable = mayCorrect(view, can.permission, gone);
  const correcting = correction.active && !gone;
  const locked = (closed && !correcting) || !canEdit;
  // Всё, что пишет само, в исправлении убрано: запись у него одна.
  const quiet = locked || correcting;

  // Как форма пишется (D-12.3): сама — черновик и отклонённый без оплаты; кнопкой — разобранный и оплаченный.
  const model = saveModel(view, !quiet, parsed);
  const auto = model === 'auto';
  const queue = useInvoiceSave({ headerDirty: dirty, linesDirty, saveHeader, auto });
  // Несохранённое ОДНО (D-10.7): шапка или строки; у черновика — непустая очередь записи. Им гаснут «Разобран» и
  // оплата, его держит уход.
  const unsaved = auto ? !queue.empty : dirty || linesDirty;
  // Уход с несохранённым (G4, issue #1097; строки — #1653): у черновика — дописывает, иначе спрашивает.
  const leave = invoiceLeave({ header: dirty, lines: linesDirty, correction, gone });
  const held = auto ? unsaved || correction.dirty : leave.held;
  const header = useRef<HTMLDivElement>(null);
  // Один раз, при открытии: форма перемонтируется на каждый счёт.
  useEffect(() => { if (focusHeader) header.current?.focus(); }, [focusHeader]);
  // Закрытая форма несохранённого не держит: без снятия страница помнила бы правки счёта, которого
  // на экране уже нет.
  useEffect(() => {
    onUnsaved?.(held);
    return () => onUnsaved?.(false);
  }, [held, onUnsaved]);
  // Смена счёта сначала дописывает (D-12.7) — запись берётся свежей, слойным эффектом, как шаг строк.
  const flushNow = useRef(queue.flush);
  useLayoutEffect(() => { flushNow.current = queue.flush; });
  useEffect(() => {
    onFlush?.(auto && !gone ? () => flushNow.current() : null);
    return () => onFlush?.(null);
  }, [auto, gone, onFlush]);
  /** Значение выбрано (организация, дата) — запись сразу (D-12.2). */
  const pick = (key: string, next: unknown) => { set(key, next); queue.mark('header'); };

  // Поля, дописанные в тип «Счёт» (issue #1482), — в части «Ещё».
  const extras = useInvoiceExtras(view.documentTypeId);
  // Схема типа не прочитана — поля, дописанные в тип, неизвестны, а не «не показаны»: их метки не сироты, и снимать
  // их «Всё верно» вслепую нельзя (ревью PR #1794). Об отказе говорит пункт «Схема типа не прочитана».
  const extrasKnown = !extras.loading && extras.error == null;
  const orphanMarks = extrasKnown ? unconfirmedOutsideBlocks(view.unconfirmed, extras.block?.fields) : [];
  const extraKeys = extras.block?.fields;
  // Что части знают от формы: поля типа — в «Ещё», замечания распознавания — в конце «Кто и сколько». Метки вне
  // блоков — пункт перечня (46): в части их больше нет, и раскрывать её ради них незачем.
  const partMore: PartExtra = {
    extra: extraKeys ?? [],
    attention: !quiet && hasRecognitionNotes(watch.recognition),
  };

  /**
   * Шаг шапки в очереди записи: вид из ответа; отказ — исключением, его называет очередь словами сервера. Ответ
   * своей записи — основа шапки (#1656), а поле, правленное, пока запись шла, остаётся несохранённым.
   */
  async function saveHeader(): Promise<InvoiceView> {
    const sent = edits;
    // На повторе правки ложатся поверх СВЕЖЕГО вида: поле, которое человек не трогал, уезжает таким,
    // каким оно лежит сейчас.
    const saved = await base.save((seen, fresh) => update.mutateAsync({
      id: view.id, seen, requisites: toRequisites((fresh ?? view).requisites, sent),
    }));
    base.adopt(saved);
    setEdits(now => editsAfterSave(sent, now));
    return saved;
  }

  // Части: раскрыты незаполненные — решено при открытии счёта, дальше сворачивает только человек.
  const [open, setOpen] = useState(() => initiallyOpen(view, { edits: {}, linesDirty: false }, partMore));
  const toggle = (part: PartId) => setOpen(prev => ({ ...prev, [part]: !prev[part] }));
  // Метка, пришедшая после открытия (распознавание кончилось, поля типа дочитались), часть РАСКРЫВАЕТ: «Всё
  // верно (N)» в шапке свёрнутой части подтверждало бы значения, которых человек не видел (ревью PR #1786).
  // Только раскрывает: свернул человек часть с уже стоявшей меткой — это его решение.
  const attention = attentionOf(view, { edits, linesDirty }, partMore);
  const attended = useRef(attention);
  useEffect(() => {
    const fresh = PART_ORDER.filter(part => attention[part] && !attended.current[part]);
    attended.current = attention;
    if (fresh.length > 0) setOpen(prev => ({ ...prev, ...Object.fromEntries(fresh.map(part => [part, true])) }));
  });
  // Строка готовности ведёт к месту в свёрнутой части: часть раскрывается, затем — к месту.
  const reveal = useCallback((target: ReadinessTarget) => {
    const part = partOfTarget(target, extraKeys);
    setOpen(prev => (part && !prev[part] ? { ...prev, [part]: true } : prev));
    requestAnimationFrame(() => goTo(target));
  }, [extraKeys]);
  const nameOf = (key: string) => {
    const id = refEntryId(value(key));
    if (id === null) return null;
    return organizations.find(o => o.id === id)?.name
      ?? (id === refEntryId(view.requisites[key]) ? view.references?.[key === K.supplier ? 'supplierName' : 'payerName'] ?? null : null);
  };
  const lineOf = (part: PartId) => partLine(part, view, { edits, linesDirty }, nameOf);

  async function confirmFields(fields: string[]) {
    if (fields.length === 0) return;
    try { await confirm.mutateAsync({ id: view.id, seen: view.version, fields }); }
    catch (e) { toast.apiError(e, 'Метки не сняты'); }
  }

  // ── Основное действие сводки (D-10.6) ─────────────────────────────────────────────────────────────
  const readiness = closed ? null : readinessItems(view, { edit: canEdit, allocate });
  const kind = primaryOf(view, { edit: canEdit, pay: canPay }, { correcting, gone });
  const [paying, setPaying] = useState(false);
  const [checkable, setCheckable] = useState<{ ready: boolean; check: () => void } | null>(null);
  const onCheckable = useCallback((next: { ready: boolean; check: () => void } | null) => setCheckable(next), []);

  async function parse() {
    try {
      const send = () => state.mutateAsync({ id: view.id, seen: view.version, to: 'parsed' as const });
      await (onExit ? onExit('parsed', send, () => {}) : send());
    } catch (e) { toast.apiError(e, 'Счёт не разобран'); }
  }

  const primary: SummaryPrimary | null = kind === 'parsed'
    ? { kind, hold: parsedHold(readiness, unsaved, queue.saving), loading: state.isPending, onClick: () => void parse() }
    : kind === 'pay'
      ? { kind, hold: payHold(unsaved, view.payment.refusal, queue.saving), onClick: () => setPaying(true) }
      : kind === 'check'
        ? { kind, hold: checkable?.ready ? null : 'Назовите причину и измените счёт — тогда исправление можно проверить', onClick: () => checkable?.check() }
        : null;

  // ── Индикатор состояния (11): все сообщения о счёте — пунктами перечня ──────────────────────────────
  // Пока схема типа читается (или не прочиталась), неизвестно, какие поля форма покажет: «Всё верно» снимает метки только с
  // полей модуля — иначе снялась бы и метка поля, которого человек не видел (ревью PR #1792, #1794).
  const marks = view.unconfirmed.filter(key => key !== VAT_MODE_MARK && !orphanMarks.includes(key)
    && (extrasKnown || key in FIELD_LABEL));
  const markTitles = Object.fromEntries([...marks, ...orphanMarks].flatMap(key => {
    const title = extras.byKey.get(key)?.title ?? extras.unshown.find(u => u.field.key === key)?.field.title;
    return title ? [[key, title]] : [];
  }));
  // Новый черновик из файла этой вкладки, без записей и без несохранённого, удаляется без окна (D-13.3). Пока
  // файл распознаётся — окном: исход сдвинет версию за срок «Вернуть», отложенный запрос упрётся в 409, а окно
  // говорит, что прочитанное пропадёт (ревью PR #1808).
  const fresh = useFresh(view.id);
  const deleteNow = !!onDeleteLater && fresh && !unsaved && !watch.running && canDelete(view, canEdit && !closed);
  const indicator = useInvoiceStatus({
    view, recognition: watch.recognition, recognitionUnread: watch.failed, onRetryRecognition: watch.retry,
    headerStale: !gone && base.stale && !watch.settling && !byScan && !correcting, onRereadHeader: reread,
    onClose, onOpenInvoice, onRetryOrganizations, rereadFailed, hiddenByFilter,
    onRetryExtras: extras.retry, onConfirmMarks: () => void confirmFields(marks), onShowScan: onShowFile,
    onConfirmHiddenMarks: () => void confirmFields(orphanMarks), confirming: confirm.isPending, reveal,
    form: {
      gone, correcting, marks, hiddenMarks: orphanMarks, markTitles, canShowScan: onShowFile !== undefined,
      shippedEarly: shippedBeforeIssued(value(K.shippedOn), value(K.date)),
      orgsFailed: organizationsError != null ? apiError(organizationsError, 'сервер отказал') : null,
      extrasFailed: extras.error != null ? apiError(extras.error, 'сервер отказал') : null,
      can: {
        edit: mayEdit, pay: canPay, allocate, correct: can.permission('costs.invoice.correct'),
        closePeriod: can.permission('core.period.close'), correctable, delete: canDelete(view, canEdit && !closed),
        deleteNow,
      },
    },
  });
  const [sheet, setSheet] = useState(false);

  // ── «⋮» сводки ────────────────────────────────────────────────────────────────────────────────────
  // «Приложить / Заменить файл…» — окно с площадкой (D6 у 14): файл к счёту только явным действием.
  const [fileDialog, setFileDialog] = useState(false);
  const recognize = useRecognitionAction(view.id, watch.recognition, view.lines.length === 0);
  // К запертому счёту файл приложить можно, заменить — нельзя: замена удалила бы документ закрытого
  // периода. Пока файл читается, заменить его нельзя (сервер откажет). Причина — подсказкой у пункта.
  const fileAction: RowAction | null = !canEdit || correcting ? null
    : closed && scan !== null
      ? { key: 'file', label: 'Заменить файл…', icon: UploadFile, disabled: true, hint: 'Нельзя: документ закрытого периода' }
      : watch.running
        ? { key: 'file', label: 'Заменить файл…', icon: UploadFile, disabled: true, hint: 'Файл распознаётся — заменить можно после исхода' }
        : { key: 'file', label: scan !== null ? 'Заменить файл…' : 'Приложить файл…', icon: UploadFile,
          onSelect: () => setFileDialog(true) };

  const who = BLOCKS[0];
  const terms = BLOCKS[1];
  const more: InvoiceBlock[] = [BLOCKS[2], ...(extras.block ? [extras.block] : [])];
  const fieldProps = {
    view, edits, organizations, locked, organizationsUnread: organizationsError != null, value, set,
    ...(auto ? { pick, onLeave: () => queue.mark('header') } : {}),
  };

  return (
    <SiteRevealScope>
    <CorrectionContext.Provider value={correction}>
    <RecognitionContext.Provider value={quiet ? undefined : watch.recognition}>
    <InvoiceDeleteProvider view={view} canEdit={canEdit && !closed} dirty={unsaved} reading={watch.running}
      organizations={organizations} onDeleted={onClose} now={deleteNow} onDeleteLater={() => onDeleteLater?.(view)}>
    <InvoiceExtrasProvider value={extras.byKey}>
    <PartRevealContext.Provider value={reveal}>
    <div className="flex-1 min-w-0 min-h-0 flex flex-col">
      {auto
        ? <LeaveGuard active={held && !gone} flush={queue.flush} failedMessage={LEAVE_FAILED} />
        : <LeaveGuard active={leave.guard} message={leave.question} save={leave.savable ? queue.save : undefined}
          saving={queue.saving} />}
      {fileDialog && (
        <InvoiceFileDialog invoiceId={view.id} version={view.version} number={asInput(view.requisites[K.number]) || null}
          current={scan === null ? null : scan.fileName ?? 'файл без имени'} onClose={() => setFileDialog(false)} />
      )}
      {paying && <PayDialog view={view} onDone={() => setPaying(false)} />}
      <InvoiceStatusSheet open={sheet} onClose={() => setSheet(false)} status={indicator.status} saving={queue.saving}
        savedAt={formatDateTime(view.updatedAt)} onAction={indicator.run} />

      {/* ── Сводка: единственная неподвижная часть ──────────────────────────────────────────────── */}
      <InvoiceSummary ref={header} number={asInput(view.requisites[K.number])}
        meta={summaryMeta(view, savedSupplierName(view, organizations))}
        states={<>
          <StateText text={asInput(view.requisites[K.state])} />
          <StateText text={view.payment.paid && view.payment.paidOn
            ? `Оплачен ${formatDate(view.payment.paidOn)}` : asInput(view.requisites[K.payment])} />
          {closed && <StatusText tone="quiet" icon={LockIcon}>Заперт</StatusText>}
        </>}
        indicator={<InvoiceStatusIndicator status={indicator.status} open={sheet} onOpen={() => setSheet(true)} />}
        nav={nav} fromFile={fromFile} file={file}
        save={model === 'auto' ? { kind: 'auto', status: queue.status, savedAt: savedTime(view.updatedAt), onRetry: queue.retry }
          : model === 'button' ? { kind: 'button', unsaved, saving: queue.saving, savedAt: formatDateTime(view.updatedAt), onSave: () => void queue.save() }
          : null}
        primary={primary}
        menu={(
          <InvoiceMoreMenu view={view} canChange={!quiet} file={fileAction} recognize={quiet ? null : recognize} page={pageActions}
            correct={correctable && !correcting ? { key: 'correct', label: 'Исправить', icon: EditNote, onSelect: correction.start } : null}
            onRejected={onExit && ((send, say) => onExit('rejected', send, say, undoVersion))} />
        )} />

      {/* ── Части: одна прокрутка ───────────────────────────────────────────────────────────────── */}
      <div className="flex-1 min-h-0 overflow-y-auto bg-surface-container-low" data-testid="invoice-scroll">
        <div className="px-4 py-4 space-y-3">
          {correcting && (
            <CorrectionBar view={view} draft={correction} edits={edits} onResetHeader={reread} onCheckable={onCheckable}
              onStale={indicator.onCorrectionStale} />
          )}
          {/* Один banner — худший пункт «Мешает работе» (D-11.3); полоса «Исправление» над ним, в число не идёт (Э12). */}
          <InvoiceStatusBanner status={indicator.status} onAction={indicator.run} busy={indicator.busy} />

          <InvoicePart title={PART_TITLE.who.title} step={1} open={open.who} onToggle={() => toggle('who')} line={lineOf('who')}
            actions={<ConfirmBlock blocks={[who]} view={view} locked={locked} confirming={confirm.isPending} onConfirm={f => void confirmFields(f)} />}>
            <BlockFields block={who} {...fieldProps} />
            {!quiet && <RecognitionNotes recognition={watch.recognition} />}
          </InvoicePart>

          <InvoicePart title={PART_TITLE.lines.title} step={2} open={open.lines} onToggle={() => toggle('lines')} line={lineOf('lines')}>
            <InvoiceLinesTable key={correction.generation} view={view} locked={closed || gone} readOnly={!canEdit} onDirty={setLinesDirty}
              supplierName={nameOf(K.supplier)} onReport={indicator.onLinesReport} saverRef={queue.lines} compact={compactLines}
              supplierChange={supplierChange} autosave={auto} onCommit={auto ? () => queue.mark('lines') : undefined} />
          </InvoicePart>

          {/* «Объект» — здесь, после строк (решение владельца Э1): весь счёт на объект — одним выбором. */}
          <InvoicePart title={PART_TITLE.allocation.title} step={3} open={open.allocation} onToggle={() => toggle('allocation')}
            line={lineOf('allocation')}>
            <InvoiceObject view={view} locked={closed || gone} />
            {(!parsed || !view.allocation.allocated) && view.lines.length > 0 && !rejected
              && <AllocationSummary allocation={view.allocation} />}
            {/* Что значит состояние для денег — словами (ревью PR #1787): строки-итога «не разнесён» мало,
                а сводка говорит только пометкой. */}
            {rejected && (
              <p className="text-body-small text-on-surface-variant">
                Счёт отклонён: по нему не платим, в затраты и «К оплате» он не входит. Строки и разноска сохранены.
              </p>
            )}
            {parsed && !view.allocation.allocated && view.lines.length > 0 && (
              <p className="text-body-small text-warning">
                Счёт разобран до разноски по стройкам. Пока строки не разнесены, его деньги не относятся ни к одной стройке.
              </p>
            )}
            <p className="text-body-small text-on-surface-variant">
              Объект той же стройки пишется сразу; несколько объектов или другая стройка — матрица с предпросмотром.
              Строку по отдельности — колонка «Разноска» в строках.
            </p>
          </InvoicePart>

          <InvoicePart title={PART_TITLE.terms.title} step={4} open={open.terms} onToggle={() => toggle('terms')} line={lineOf('terms')}
            actions={<>
              <ConfirmBlock blocks={[terms]} view={view} locked={locked} confirming={confirm.isPending} onConfirm={f => void confirmFields(f)} />
              {!gone && kind !== 'pay' && <InvoicePayButton view={view} dirty={unsaved} onPay={() => setPaying(true)} />}
            </>}>
            <BlockFields block={terms} {...fieldProps} />
            {!gone && <InvoicePayment view={view} />}
          </InvoicePart>

          <InvoicePart title={PART_TITLE.more.title} open={open.more} onToggle={() => toggle('more')} line={lineOf('more')}
            actions={<ConfirmBlock blocks={more} view={view} locked={locked} confirming={confirm.isPending} onConfirm={f => void confirmFields(f)} />}>
            <BlockFields block={BLOCKS[2]} {...fieldProps} />
            {/* Поля, дописанные в тип «Счёт» (issue #1482), — своим подразделом: их ведёт заказчик, не модуль. */}
            {extras.block && (
              <section aria-labelledby={`${view.id}-extra`} className="space-y-2">
                <h4 id={`${view.id}-extra`} className="text-title-small text-on-surface-variant">{extras.block.title}</h4>
                <BlockFields block={extras.block} {...fieldProps} />
              </section>
            )}
            <UnshownExtras extras={extras} />
          </InvoicePart>

          {(view.book ?? 0) > 0 && !gone && (
            <InvoicePart title={PART_TITLE.history.title} open={open.history} onToggle={() => toggle('history')} line={lineOf('history')}>
              <CorrectionHistory view={view} gone={gone} />
            </InvoicePart>
          )}
        </div>
      </div>
    </div>
    </PartRevealContext.Provider>
    </InvoiceExtrasProvider>
    </InvoiceDeleteProvider>
    </RecognitionContext.Provider>
    </CorrectionContext.Provider>
    </SiteRevealScope>
  );
}

/** Запись перед уходом не дошла — что теряется и где причина (D-12.7). */
const LEAVE_FAILED = 'Набранное в счёте не записано: уйдя, вы его потеряете. Останьтесь — в сводке счёта названа причина, и запись можно повторить.';

/** «Сохранено · 12:04» — сегодня временем, раньше — датой со временем. */
function savedTime(iso: string): string {
  const when = new Date(iso);
  return when.toDateString() === new Date().toDateString()
    ? when.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })
    : formatDateTime(iso);
}

const STATE_TONE: Record<string, StatusTone> = { [PARSED]: 'success', [REJECTED]: 'error', 'Оплачен': 'success' };

function StateText({ text }: { text: string }) {
  if (!text) return null;
  return <StatusText tone={STATE_TONE[text] ?? (text.startsWith('Оплачен') ? 'success' : 'neutral')}>{text}</StatusText>;
}

/** Скан из реквизитов — значение файлового поля общего контракта. */
function scanOf(view: InvoiceView): { fileName: string | null; mimeType: string | null } | null {
  const node = view.requisites[K.scan];
  if (node == null || typeof node !== 'object') return null;
  const file = node as Record<string, unknown>;
  return {
    fileName: typeof file.fileName === 'string' ? file.fileName : null,
    mimeType: typeof file.mimeType === 'string' ? file.mimeType : null,
  };
}

/**
 * Поставщик, СОХРАНЁННЫЙ в шапке, — названием: для сводки и плашки окна выбора позиции (issue #1517).
 * Запомненное сверяется с лежащим поставщиком, а не с набранным.
 */
function savedSupplierName(view: InvoiceView, organizations: CostsOrganization[]): string | null {
  const id = refEntryId(view.requisites[K.supplier]);
  return id === null ? null : organizations.find(o => o.id === id)?.name ?? view.references?.supplierName ?? null;
}
