import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';

import { EditNote, LockIcon, StarShine, UploadFile, Visibility } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Banner } from '@/shared/ui/Banner';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import type { RowAction } from '@/shared/ui/RowActionsMenu';
import { apiError } from '@/shared/utils/apiError';
import { useCan } from '@/shared/api/access';
import { formatDate, formatDateTime } from '@/shared/format/format';
import {
  useAttachInvoiceScan, useConfirmInvoiceFields, useInvoiceState, useUpdateInvoice,
  type CostsOrganization, type InvoiceView,
} from '@/shared/api/invoices';
import {
  BLOCKS, K, PARSED, REJECTED, asInput, refEntryId, toRequisites, unconfirmedOutsideBlocks, type InvoiceBlock,
} from './invoiceFields';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { goTo, readinessItems, type ReadinessTarget } from './invoiceReadiness';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import { InvoiceObject } from './InvoiceObject';
import { InvoiceLockNote, InvoicePayButton, InvoicePayment, PayDialog } from './InvoicePayment';
import type { QueueExit } from './invoiceQueues';
import { InvoiceMoreMenu } from './InvoiceMoreMenu';
import { InvoiceDeleteProvider, InvoiceGoneNotice } from './InvoiceDeleteDialog';
import { InvoiceTwinNotes } from './InvoiceTwinNotes';
import { LostReferencesNote } from './LostReferencesNote';
import { ScanFileInput } from './InvoiceScanPanel';
import { RecognitionBanner, RecognitionChip, RecognitionNotes } from './InvoiceRecognitionNote';
import { useRecognitionAction } from './recognitionAction';
import { RecognitionContext, useRecognitionWatch } from './recognitionWatch';
import { explainedByRecognition } from './recognition';
import { SiteRevealScope } from '@/shared/siteFilter/SiteOptions';
import { CorrectionBar } from './CorrectionBar';
import { CorrectionHistory } from './CorrectionHistory';
import { CorrectionContext, useCorrectionDraft } from './useCorrectionDraft';
import { mayCorrect } from './correction';
import { UnshownExtras } from './InvoiceExtraFields';
import { InvoiceExtrasProvider, useInvoiceExtras } from './invoiceExtras';
import { AllocationSummary } from './LineAllocation';
import { BlockFields, ConfirmBlock } from './InvoiceFormFields';
import { InvoicePart } from './InvoicePart';
import { PART_TITLE, PartRevealContext, initiallyOpen, partLine, partOfTarget, type PartId } from './invoiceParts';
import { InvoiceSummary, type SummaryPrimary } from './InvoiceSummary';
import { parsedHold, payHold, primaryOf, summaryMeta } from './invoicePrimary';
import { useInvoiceSave } from './invoiceSave';

/**
 * Форма ввода счёта (задача C1, issue #1076; раскладка — costs-invoice-screen-10-layout, вариант Г, ТЗ
 * COST-6.2).
 *
 * <p><b>Неподвижна только сводка</b> (≤ 64): номер, пометки, поставщик · дата · сумма, «Сохранить», одно
 * основное действие и «⋮». Всё остальное — одной прокруткой частей в порядке работы: «Кто и сколько»,
 * сообщения, «Строки», «Разноска», «Сроки и оплата», «Ещё», «История». Заполненная часть открывается
 * свёрнутой в строку-итог (`invoiceParts`).</p>
 *
 * <p><b>Сохранение не блокируется ничем.</b> Ни пустых обязательных полей, ни строк: черновик обязан
 * сохраняться и попадать в реестр со сроком. Шапку и строки пишет одна кнопка, шапка раньше строк
 * (`invoiceSave`, D-10.7).</p>
 *
 * <p><b>Метки «распознано, не подтверждено»</b> приходят с сервера и переживают повторное открытие
 * черновика. Конец у метки двойной: правка поля снимает её с этого поля, «Всё верно» — со всей части.</p>
 *
 * <p>⚠️ Правки прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ: страница передаёт `key={view.id}`. Не эффект
 * со сбросом состояния — он рисует лишний кадр с чужими правками.</p>
 */
export function InvoiceForm({
  view, organizations, organizationsError, onOpenInvoice, scanSlot, gone = false, onClose, onExit, onUnsaved,
  focusHeader = false, nav, fromFile, file, compactLines = false,
}: {
  view: InvoiceView;
  /** «Разобран» и «Отклонить» идут через страницу — она знает отбор и список (issue #1293): запирает
   *  форму на время записи и выбора следующего счёта и говорит о переходе. `otherwise` зовётся, когда
   *  сама страница ничего не сказала. Обещание кончается ответом на запись. */
  onExit?: <T>(how: QueueExit, send: () => Promise<T>, otherwise: (result: T) => void) => Promise<T>;
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
  onOpenInvoice: (id: string) => void;
  /** Замена панели файла, когда рядом ей места нет, — рисует страница, она знает место. */
  scanSlot?: ReactNode;
  /** Слоты сводки — их наполняет страница (D-10.3, D-10.4): «‹ N из M ›», «Счёт из файла», «Файл». */
  nav?: ReactNode;
  fromFile?: ReactNode;
  file?: ReactNode;
  /** Рядом список счетов — таблица строк в плотном виде (D-10.10). */
  compactLines?: boolean;
}) {
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const update = useUpdateInvoice();
  const confirm = useConfirmInvoiceFields();
  const attach = useAttachInvoiceScan();
  const state = useInvoiceState();
  const toast = useSnackbar();
  const correction = useCorrectionDraft();

  const value = (key: string): unknown => (key in edits ? edits[key] : view.requisites[key]);
  const set = (key: string, next: unknown) => setEdits(prev => ({ ...prev, [key]: next }));
  const dirty = Object.keys(edits).length > 0;
  const [linesDirty, setLinesDirty] = useState(false);
  // Несохранённое ОДНО (D-10.7): шапка или строки. Им гаснут «Разобран» и оплата, его спрашивает уход.
  const unsaved = dirty || linesDirty;
  const held = unsaved || correction.dirty;
  const header = useRef<HTMLDivElement>(null);
  // Один раз, при открытии: форма перемонтируется на каждый счёт.
  useEffect(() => { if (focusHeader) header.current?.focus(); }, [focusHeader]);
  // Закрытая форма несохранённого не держит: без снятия страница помнила бы правки счёта, которого
  // на экране уже нет.
  useEffect(() => {
    onUnsaved?.(held);
    return () => onUnsaved?.(false);
  }, [held, onUnsaved]);

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

  // Поля, дописанные в тип «Счёт» (issue #1482), — в части «Ещё».
  const extras = useInvoiceExtras(view.documentTypeId);
  const orphanMarks = extras.loading ? [] : unconfirmedOutsideBlocks(view.unconfirmed, extras.block?.fields);

  // Уход с несохранённым спрашивает (G4, issue #1097) — и о строках тоже (D-10.7): прежде охрана видела
  // только шапку, и уход молча уносил набранные строки (#1653).
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(held && !gone, proceed => setLeave(() => proceed));

  /** Шаг шапки в очереди записи: вид из ответа или `null` — отказ, о нём уже сказано. */
  async function saveHeader(): Promise<InvoiceView | null> {
    try {
      // На повторе правки ложатся поверх СВЕЖЕГО вида: поле, которое человек не трогал, уезжает таким,
      // каким оно лежит сейчас.
      const saved = await base.save((seen, fresh) => update.mutateAsync({
        id: view.id, seen, requisites: toRequisites((fresh ?? view).requisites, edits),
      }));
      setEdits({});
      return saved;
    } catch (e) {
      // Текст отказа — ОТ СЕРВЕРА: он называет поле и причину.
      toast.apiError(e, 'Счёт не сохранён');
      return null;
    }
  }
  const queue = useInvoiceSave({ headerDirty: dirty, linesDirty, saveHeader });

  // Части: раскрыты незаполненные — решено при открытии счёта, дальше сворачивает только человек.
  const [open, setOpen] = useState(() => initiallyOpen(view, { edits: {}, linesDirty: false }));
  const toggle = (part: PartId) => setOpen(prev => ({ ...prev, [part]: !prev[part] }));
  // Строка готовности ведёт к месту в свёрнутой части: часть раскрывается, затем — к месту.
  const reveal = useCallback((target: ReadinessTarget) => {
    const part = partOfTarget(target);
    setOpen(prev => (part && !prev[part] ? { ...prev, [part]: true } : prev));
    requestAnimationFrame(() => goTo(target));
  }, []);
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
    ? { kind, hold: parsedHold(readiness, unsaved), loading: state.isPending, onClick: () => void parse() }
    : kind === 'pay'
      ? { kind, hold: payHold(unsaved), onClick: () => setPaying(true) }
      : kind === 'check'
        ? { kind, hold: checkable?.ready ? null : 'Назовите причину и измените счёт — тогда исправление можно проверить', onClick: () => checkable?.check() }
        : null;

  // ── «⋮» сводки ────────────────────────────────────────────────────────────────────────────────────
  const fileInput = useRef<HTMLInputElement>(null);
  const recognize = useRecognitionAction(view.id, watch.recognition, view.lines.length === 0);
  // К запертому счёту файл приложить можно, заменить — нельзя: замена удалила бы документ закрытого
  // периода. Пока файл читается, заменить его нельзя (сервер откажет). Причина — подсказкой у пункта.
  const fileAction: RowAction | null = !canEdit || correcting ? null
    : closed && scan !== null
      ? { key: 'file', label: 'Заменить файл…', icon: UploadFile, disabled: true, hint: 'Нельзя: документ закрытого периода' }
      : watch.running
        ? { key: 'file', label: 'Заменить файл…', icon: UploadFile, disabled: true, hint: 'Файл распознаётся — заменить можно после исхода' }
        : { key: 'file', label: scan !== null ? 'Заменить файл…' : 'Приложить файл…', icon: UploadFile, loading: attach.isPending,
          onSelect: () => fileInput.current?.click() };

  const who = BLOCKS[0];
  const terms = BLOCKS[1];
  const more: InvoiceBlock[] = [BLOCKS[2], ...(extras.block ? [extras.block] : [])];
  const fieldProps = {
    view, edits, organizations, locked, organizationsUnread: organizationsError != null, value, set,
  };
  const parsed = view.requisites[K.state] === PARSED;
  const rejected = view.requisites[K.state] === REJECTED;

  return (
    <SiteRevealScope>
    <CorrectionContext.Provider value={correction}>
    <RecognitionContext.Provider value={quiet ? undefined : watch.recognition}>
    <InvoiceDeleteProvider view={view} canEdit={canEdit && !closed} dirty={unsaved} reading={watch.running}
      organizations={organizations} onDeleted={onClose}>
    <InvoiceExtrasProvider value={extras.byKey}>
    <PartRevealContext.Provider value={reveal}>
    <div className="flex-1 min-w-0 min-h-0 flex flex-col">
      <LeaveGuardDialog open={leave !== null} saving={queue.saving}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
        onSave={async () => {
          const go = leave;
          setLeave(null);
          // Исправление обычным сохранением не пишется: сервер откажет «заперт». Путь у него один.
          if (correcting) toast.info('Исправление записывается кнопкой «Проверить исправление…» — после проверки.');
          else if (await queue.save()) go?.();
        }} />
      <ScanFileInput ref={fileInput} onPick={picked => {
        attach.mutateAsync({ id: view.id, seen: view.version, file: picked }).catch(e => toast.apiError(e, 'Файл не приложен'));
      }} />
      {paying && <PayDialog view={view} onDone={() => setPaying(false)} />}

      {/* ── Сводка: единственная неподвижная часть ──────────────────────────────────────────────── */}
      <InvoiceSummary ref={header} number={asInput(view.requisites[K.number])}
        meta={summaryMeta(view, savedSupplierName(view, organizations))}
        states={<>
          <StateText text={asInput(view.requisites[K.state])} />
          <StateText text={view.payment.paid && view.payment.paidOn
            ? `Оплачен ${formatDate(view.payment.paidOn)}` : asInput(view.requisites[K.payment])} />
          {closed && <StatusText tone="quiet" icon={LockIcon}>Заперт</StatusText>}
          <RecognitionChip recognition={watch.recognition} />
          {view.unconfirmed.length > 0 && (
            <StatusText tone="warning" icon={StarShine} title="Распознано, не подтверждено — сверьте с файлом">
              со скана: {view.unconfirmed.length}
            </StatusText>
          )}
          {!mayEdit && <StatusText tone="quiet" icon={Visibility} title="Права вводить счета нет">Только чтение</StatusText>}
        </>}
        nav={nav} fromFile={fromFile} file={file}
        save={quiet ? null : { unsaved, saving: queue.saving, savedAt: formatDateTime(view.updatedAt), onSave: () => void queue.save() }}
        primary={primary}
        menu={(
          <InvoiceMoreMenu view={view} canChange={!quiet} file={fileAction} recognize={quiet ? null : recognize}
            correct={correctable && !correcting ? { key: 'correct', label: 'Исправить', icon: EditNote, onSelect: correction.start } : null}
            onRejected={onExit && ((send, say) => onExit('rejected', send, say))} />
        )} />

      {/* ── Части: одна прокрутка ───────────────────────────────────────────────────────────────── */}
      <div className="flex-1 min-h-0 overflow-y-auto bg-surface-container-low" data-testid="invoice-scroll">
        <div className="px-4 py-4 space-y-3">
          {gone && <InvoiceGoneNotice onClose={onClose} />}
          {correcting && <CorrectionBar view={view} draft={correction} edits={edits} onResetHeader={reread} onCheckable={onCheckable} />}

          <InvoicePart title={PART_TITLE.who.title} step={1} open={open.who} onToggle={() => toggle('who')} line={lineOf('who')}
            actions={<ConfirmBlock blocks={[who]} view={view} locked={locked} confirming={confirm.isPending} onConfirm={f => void confirmFields(f)} />}>
            <BlockFields block={who} {...fieldProps} />
            {!quiet && <RecognitionNotes recognition={watch.recognition} />}
            {/* Метки у полей, которых форма не показывает: править их здесь нечем — снимает только «Всё верно». */}
            {orphanMarks.length > 0 && (
              <div className="flex items-center gap-3">
                <p className="flex-1 text-body-small text-warning">
                  Распознано, не подтверждено — у полей, которых форма не показывает:{' '}
                  {orphanMarks.map(key => `«${extras.unshown.find(u => u.field.key === key)?.field.title || key}»`).join(', ')}.
                  Сверьте их по скану.
                </p>
                {!quiet && (
                  <Button variant="text" loading={confirm.isPending} onClick={() => void confirmFields(orphanMarks)}>
                    Всё верно ({orphanMarks.length})
                  </Button>
                )}
              </div>
            )}
          </InvoicePart>

          {/* Сообщения — между первой частью и строками (D-10.9): первый этап без прокрутки при любом их
              числе. Сводит их в индикатор ребёнок 11. */}
          {!gone && base.stale && !watch.settling && !byScan && !correcting
            && <StaleInvoiceNotice what="поля счёта" onReread={reread} />}
          {organizationsError != null && (
            <Banner tone="error">
              Справочник организаций не прочитан: {apiError(organizationsError, 'сервер отказал')}. Поставщика и
              плательщика выбрать не из чего — остальные поля правятся и сохраняются. Это не «организаций нет»:
              их список сюда не доехал.
            </Banner>
          )}
          {!closed && !gone && (
            <RecognitionBanner invoiceId={view.id} recognition={watch.recognition} unread={watch.failed}
              onRetryRead={watch.retry} canEdit={canEdit} />
          )}
          <LostReferencesNote view={view} locked={gone ? 'gone' : closed && !correcting ? (correctable ? 'correct' : 'closed') : null} />
          <InvoiceTwinNotes view={view} onOpenInvoice={onOpenInvoice} />
          {closed && <InvoiceLockNote lockedBy={view.payment.lockedBy ?? ''} />}
          {scanSlot}

          <InvoicePart title={PART_TITLE.lines.title} step={2} open={open.lines} onToggle={() => toggle('lines')} line={lineOf('lines')}>
            <InvoiceLinesTable key={correction.generation} view={view} locked={closed || gone} readOnly={!canEdit} onDirty={setLinesDirty}
              supplierName={savedSupplierName(view, organizations)} headerDirty={dirty} saverRef={queue.lines} compact={compactLines} />
          </InvoicePart>

          {/* «Объект» — здесь, после строк (решение владельца Э1): весь счёт на объект — одним выбором. */}
          <InvoicePart title={PART_TITLE.allocation.title} step={3} open={open.allocation} onToggle={() => toggle('allocation')}
            line={lineOf('allocation')}>
            <InvoiceObject view={view} locked={closed || gone} />
            {(!parsed || !view.allocation.allocated) && view.lines.length > 0 && !rejected
              && <AllocationSummary allocation={view.allocation} />}
            <p className="text-body-small text-on-surface-variant">
              Объект той же стройки пишется сразу; несколько объектов или другая стройка — матрица с предпросмотром.
              Строку по отдельности — колонка «Разноска» в строках.
            </p>
          </InvoicePart>

          <InvoicePart title={PART_TITLE.terms.title} step={4} open={open.terms} onToggle={() => toggle('terms')} line={lineOf('terms')}
            actions={<>
              <ConfirmBlock blocks={[terms]} view={view} locked={locked} confirming={confirm.isPending} onConfirm={f => void confirmFields(f)} />
              {!gone && <InvoicePayButton view={view} dirty={unsaved} primary={kind === 'pay'} />}
            </>}>
            <BlockFields block={terms} {...fieldProps} />
            {!gone && <InvoicePayment view={view} dirty={unsaved} />}
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
