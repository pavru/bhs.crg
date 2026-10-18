import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { CheckCheck, PencilLine, Save, Sparkles, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { apiError } from '@/shared/utils/apiError';
import { useCan } from '@/shared/api/access';
import {
  useAttachInvoiceScan, useConfirmInvoiceFields, useUpdateInvoice,
  type CostsOrganization, type InvoiceView,
} from '@/shared/api/invoices';
import {
  BLOCKS, K, UNCONFIRMED_HINT, asInput, catalogRef, fromInput,
  isMarked, moneyInput, refEntryId, toRequisites, unconfirmedInBlock, unconfirmedOutsideBlocks,
  type InvoiceBlock,
} from './invoiceFields';
import { formatDate } from '@/shared/format/format';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { READINESS_ATTR, isRequired, readinessAnchor } from './invoiceReadiness';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import { InvoiceObject } from './InvoiceObject';
import { InvoiceLockNote, InvoicePayment } from './InvoicePayment';
import type { QueueExit } from './invoiceQueues';
import { InvoiceMoreMenu } from './InvoiceMoreMenu';
import { InvoiceDeleteProvider, InvoiceGoneNotice } from './InvoiceDeleteDialog';
import { InvoiceTwinNotes } from './InvoiceTwinNotes';
import { LostReferencesNote } from './LostReferencesNote';
import { LOST } from './lostReferences';
import { withArchiveWord } from '@/shared/ui/archive';
import { ScanUploadButton } from './InvoiceScanPanel';
import { InvoiceFieldScan } from './InvoiceFieldScan';
import { InvoiceDueField } from './InvoiceDueField';
import { deferralNote, hints, shippedBeforeIssued } from './invoiceDue';
import { RecognitionBanner, RecognitionChip, RecognitionNotes, RecognitionStart } from './InvoiceRecognitionNote';
import { RecognitionContext, useRecognitionWatch } from './recognitionWatch';
import { explainedByRecognition } from './recognition';
import { SiteRevealScope } from '@/shared/siteFilter/SiteOptions';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { CorrectionBar } from './CorrectionBar';
import { CorrectionHistory } from './CorrectionHistory';
import { CorrectionContext, useCorrecting, useCorrectionDraft } from './useCorrectionDraft';
import { mayCorrect } from './correction';
import { ExtraField, UnshownExtras } from './InvoiceExtraFields';
import { InvoiceExtrasProvider, useExtraField, useInvoiceExtras } from './invoiceExtras';

/**
 * Форма ввода счёта (задача C1, второй PR, issue #1076, ТЗ COST-6.2).
 *
 * <p><b>Шапка не прокручивается</b>: поставщик, номер, дата и сумма к оплате видны всегда, что бы
 * человек ни листал ниже. Это набросок заказчика почти дословно.</p>
 *
 * <p><b>Сохранение не блокируется ничем.</b> Ни пустых обязательных полей, ни строк: черновик обязан
 * сохраняться и попадать в реестр со сроком, а строки, ждущие номенклатуры, — это счётчик и отбор
 * «Разобрать», а не препятствие. Поэтому у кнопки нет условия, кроме «есть что сохранять».</p>
 *
 * <p><b>Метки «распознано, не подтверждено»</b> приходят с сервера и переживают повторное открытие
 * черновика — в этом весь их смысл: черновик создаёт фоновая задача, а человек открывает его потом.
 * Конец у метки двойной: правка поля снимает её с этого поля, «Всё верно» — со всего блока.</p>
 */
/**
 * ⚠️ Правки прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ: страница передаёт `key={view.id}`. Не эффект
 * со сбросом состояния — он рисует лишний кадр с чужими правками, и в этом кадре номер одного счёта
 * стоит в форме другого.
 */
export function InvoiceForm({
  view, organizations, organizationsError, onOpenInvoice, scanSlot, gone = false, onClose, onExit, onUnsaved, focusHeader = false,
}: {
  view: InvoiceView;
  /** «Разобран» и «Отклонить» идут через страницу — она знает отбор и список (issue #1293): запирает
   *  форму на время записи и выбора следующего счёта и говорит о переходе. `otherwise` зовётся, когда
   *  сама страница ничего не сказала. Обещание кончается ответом на запись. */
  onExit?: <T>(how: QueueExit, send: () => Promise<T>, otherwise: (result: T) => void) => Promise<T>;
  /** Сюда привёл переход от соседнего счёта очереди: кнопка, на которой стоял фокус, исчезла вместе
   *  с прежней формой, и без переноса он падает в начало страницы. */
  focusHeader?: boolean;
  /** Есть ли в форме несохранённое — в шапке или в строках. Страница спрашивает об этом в тот миг,
   *  когда собирается сменить счёт, а не в миг нажатия (ревью PR #1303). Обязана быть стабильной. */
  onUnsaved?: (unsaved: boolean) => void;
  /** Счёт удалили, пока он был открыт (issue #1282): набранное остаётся, действий записи нет. */
  gone?: boolean;
  /** Закрыть форму: после удаления и по «Закрыть счёт» у счёта, которого больше нет. */
  onClose: () => void;
  organizations: CostsOrganization[];
  /** Отказ чтения справочника организаций, если он был. ⚠️ Пустой список и «справочник не
   *  прочитан» — разные вещи: сервер их различает нарочно, и терять это различие на клиенте нельзя. */
  organizationsError?: unknown;
  onOpenInvoice: (id: string) => void;
  /** Замена панели скана на узком экране — рисует страница, она знает ширину. */
  scanSlot?: ReactNode;
}) {
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const update = useUpdateInvoice();
  const confirm = useConfirmInvoiceFields();
  const attach = useAttachInvoiceScan();
  const toast = useToast();
  const correction = useCorrectionDraft();

  const value = (key: string): unknown => (key in edits ? edits[key] : view.requisites[key]);
  const set = (key: string, next: unknown) => setEdits(prev => ({ ...prev, [key]: next }));
  const dirty = Object.keys(edits).length > 0;
  const [linesDirty, setLinesDirty] = useState(false);
  const unsaved = dirty || linesDirty || correction.dirty;
  const header = useRef<HTMLDivElement>(null);
  // Один раз, при открытии: форма перемонтируется на каждый счёт.
  useEffect(() => { if (focusHeader) header.current?.focus(); }, [focusHeader]);
  // Закрытая форма несохранённого не держит: без снятия страница помнила бы правки счёта, которого
  // на экране уже нет.
  useEffect(() => {
    onUnsaved?.(unsaved);
    return () => onUnsaved?.(false);
  }, [unsaved, onUnsaved]);

  // Правки шапки лежат поверх вида (issue #1176). Вид обновился, а поля, которые человек правит,
  // остались прежними — правки в силе. Изменили именно их — поле на экране показывает набранное, а не
  // чужое значение, и сохранение затёрло бы то, чего человек не видел: это и есть «устарело».
  //
  // ⚠️ Подпись — реквизиты ЦЕЛИКОМ, а «моя часть» — сравнение: только по полям, которые человек правит.
  // Подпись из одних правленых полей менялась бы с первой же правкой, без смены версии, — основа её не
  // запоминала, и следующая чужая правка строк читалась бы как правка шапки (ревью PR #1208).
  const base = useDraftBase(view, of => of.requisites, dirty, {
    same: (a, b) => Object.keys(edits).every(key => JSON.stringify(a[key]) === JSON.stringify(b[key])),
  });

  function reread() {
    setEdits({});
    base.rebase();
  }

  const scan = useMemo(() => scanOf(view), [view]);
  // Распознавание скана (issue #1077). Вид, пришедший перечитыванием после исхода, — правка
  // распознавания, а не чужая: набранное остаётся и переносится на него без полосы «устарело»,
  // прочитанное для этих полей предлагается под ними.
  //
  // ⚠️ Принимается не «всё, что пришло этим перечитыванием», а только объяснимое распознаванием: поле
  // было пустым и легло с меткой. Скан читается десятки секунд, и сохранённое за это время соседом
  // приходит тем же перечитыванием — его правка по-прежнему даёт «устарело».
  const watch = useRecognitionWatch(view.id, scan !== null && !gone);
  const byScan = base.stale && watch.accepted === view.version
    && explainedByRecognition(edits, base.signature, view);
  if (byScan) base.rebase();

  // Заперт — слово сервера (`lockedBy`), а не «оплачен»: оплаченный счёт открытого периода правится.
  const closed = view.payment.lockedBy !== null;
  // Без права вводить счета (бухгалтер) форма читается так же, как запертая: действий записи нет вовсе,
  // а не «есть и откажут» — каждое из них ответило бы 403 (N1, issue #1102). Разноска и оплата — свои
  // права, их это не касается.
  // Счёта больше нет — то же чтение: сохранять некуда, и каждое действие ответило бы «не найден».
  const can = useCan();
  const mayEdit = can.permission('costs.invoice.edit');
  const canEdit = mayEdit && !gone;
  // Запертый счёт правится ИСПРАВЛЕНИЕМ (E2, issue #1100, PR 6b-1): та же форма, переключённая на месте, —
  // причина, версия и книга, всё одним телом. Кнопка — тому, у кого и правка, и право исправления;
  // остальным о режиме ни слова.
  const correctable = mayCorrect(view, can.permission, gone);
  const correcting = correction.active && !gone;
  const locked = (closed && !correcting) || !canEdit;
  // Всё, что пишет само, в исправлении убрано: запись у него одна (скан, распознавание, «Ещё», «Сохранить»).
  const quiet = locked || correcting;

  // Поля, дописанные в тип «Счёт» (issue #1482), — блок «Дополнительно».
  const extras = useInvoiceExtras(view.documentTypeId);
  const orphanMarks = unconfirmedOutsideBlocks(view.unconfirmed, extras.block?.fields);

  // Уход из раздела с несохранёнными правками спрашивает (G4, issue #1097). Открытый счёт назван в
  // адресе, и пункт «Счета» в навигации ведёт на адрес БЕЗ счёта: форма закрылась бы вместе с
  // правками — молча. Пока выбор жил в состоянии экрана, тот же щелчок не делал ничего.
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard((dirty || correction.dirty) && !gone, proceed => setLeave(() => proceed));

  /** @returns сохранилось ли: уходить со страницы после отказа нельзя — правки бы пропали. */
  async function save(): Promise<boolean> {
    try {
      // На повторе правки ложатся поверх СВЕЖЕГО вида: поле, которое человек не трогал, уезжает
      // таким, каким оно лежит сейчас, а не каким было на экране минуту назад.
      await base.save((seen, fresh) => update.mutateAsync({
        id: view.id, seen, requisites: toRequisites((fresh ?? view).requisites, edits),
      }));
      setEdits({});
      return true;
    } catch (e) {
      // Текст отказа — ОТ СЕРВЕРА (`apiError` и заведён для этого): он называет поле и причину
      // («поле такое-то заперто», «не число»), а своя формулировка была бы пересказом, который
      // разойдётся с сервером на первом же новом отказе.
      toast.apiError(e, 'Счёт не сохранён');
      return false;
    }
  }

  async function confirmBlock(block: InvoiceBlock) {
    const fields = unconfirmedInBlock(block, view.unconfirmed);
    if (fields.length === 0) return;
    try { await confirm.mutateAsync({ id: view.id, seen: view.version, fields }); }
    catch (e) { toast.apiError(e, 'Метки не сняты'); }
  }

  // Граница формы для «Показать все стройки» (issue #1219): объект счёта, разноска строк и матрица —
  // одна форма; раскрыли в одном списке — раскрыты все, до ухода к другому счёту (форма с ключом).
  return (
    <SiteRevealScope>
    <CorrectionContext.Provider value={correction}>
    <RecognitionContext.Provider value={quiet ? undefined : watch.recognition}>
    <InvoiceDeleteProvider view={view} canEdit={canEdit && !closed} dirty={dirty || linesDirty} reading={watch.running}
      organizations={organizations} onDeleted={onClose}>
    <InvoiceExtrasProvider value={extras.byKey}>
    <div className="flex-1 min-w-0 min-h-0 flex flex-col">
      <LeaveGuardDialog open={leave !== null} saving={update.isPending}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
        onSave={async () => {
          const go = leave;
          setLeave(null);
          // Исправление обычным сохранением не пишется: сервер откажет «заперт». Путь у него один.
          if (correcting) toast.info('Исправление записывается кнопкой «Проверить исправление…» — после проверки.');
          else if (await save()) go?.();
        }} />
      {/* ── Шапка: без прокрутки ─────────────────────────────────────────────── */}
      <div className="shrink-0 border-b border-stroke bg-surface px-5 py-3 space-y-3 outline-none"
        ref={header} tabIndex={-1} role="group" data-testid="invoice-header"
        aria-label={`Счёт № ${asInput(view.requisites[K.number]) || 'без номера'}`}>
        {gone && <InvoiceGoneNotice onClose={onClose} />}
        {!gone && base.stale && !watch.settling && !byScan && !correcting
          && <StaleInvoiceNotice what="поля счёта" onReread={reread} />}
        <div className="flex items-center gap-2 flex-wrap">
          <StateChip text={asInput(view.requisites[K.state])} />
          <StateChip text={view.payment.paid && view.payment.paidOn
            ? `Оплачен ${formatDate(view.payment.paidOn)}` : asInput(view.requisites[K.payment])} />
          {closed && <StateChip text="Заперт" />}
          <RecognitionChip recognition={watch.recognition} />
          {view.unconfirmed.length > 0 && (
            <span className="inline-flex items-center gap-1 text-xs text-warning">
              <Sparkles size={12} /> распознано, не подтверждено: {view.unconfirmed.length}
            </span>
          )}
          <div className="flex-1" />
          {/* К запертому счёту скан приложить можно, заменить — нельзя: замена удалила бы документ
              закрытого периода. Кнопки замены нет вовсе, а не «есть и откажет». */}
          {!mayEdit && <span className="text-xs text-fg3">Только чтение: права вводить счета нет</span>}
          {canEdit && closed && scan !== null && (
            <span className="text-xs text-fg3">Файл заменить нельзя: документ закрытого периода</span>
          )}
          {/* Пока скан читается, заменить его нельзя (сервер откажет): прочитанное легло бы в счёт с
              другой бумагой. Кнопки нет, причина названа. */}
          {canEdit && watch.running && (
            <span className="text-xs text-fg3">Файл распознаётся — заменить можно после исхода</span>
          )}
          {!quiet && <RecognitionStart invoiceId={view.id} recognition={watch.recognition} explain={view.lines.length === 0} />}
          {canEdit && !correcting && !watch.running && !(closed && scan !== null) && (
            <ScanUploadButton hasScan={scan !== null} busy={attach.isPending}
              onPick={file => {
                attach.mutateAsync({ id: view.id, seen: view.version, file }).catch(e => toast.apiError(e, 'Файл не приложен'));
              }} />
          )}
          {correctable && !correcting && (
            <Button variant="outlined" size="sm" icon={<PencilLine size={14} />} onClick={correction.start}>
              Исправить
            </Button>
          )}
          {!quiet && (
            <Button variant="filled" size="sm" icon={<Save size={14} />} disabled={!dirty}
              loading={update.isPending} onClick={save}>
              Сохранить
            </Button>
          )}
          {!quiet && <InvoiceMoreMenu view={view} onRejected={onExit && ((send, say) => onExit('rejected', send, say))} />}
        </div>

        {view.payment.lockedBy !== null && <InvoiceLockNote lockedBy={view.payment.lockedBy} />}
        {correcting && <CorrectionBar view={view} draft={correction} edits={edits} onResetHeader={reread} />}
        {!gone && <InvoicePayment view={view} dirty={dirty} />}

        {organizationsError != null && (
          <div className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger-subtle
            px-3 py-2 text-xs text-danger">
            <TriangleAlert size={14} className="shrink-0 mt-0.5" />
            <span>
              Справочник организаций не прочитан: {apiError(organizationsError, 'сервер отказал')}.
              Поставщика и плательщика выбрать не из чего — остальные поля правятся и сохраняются.
              ⚠️ Это НЕ «организаций нет»: их список сюда не доехал.
            </span>
          </div>
        )}

        {!closed && !gone && (
          <RecognitionBanner invoiceId={view.id} recognition={watch.recognition} unread={watch.failed}
            onRetryRead={watch.retry} canEdit={canEdit} />
        )}
        <LostReferencesNote view={view} locked={gone ? 'gone' : closed && !correcting ? (correctable ? 'correct' : 'closed') : null} />
        <InvoiceTwinNotes view={view} onOpenInvoice={onOpenInvoice} />
        {scanSlot}

        <BlockFields block={BLOCKS[0]} columns="sm:grid-cols-2 lg:grid-cols-4"
          view={view} edits={edits} organizations={organizations} locked={locked}
          organizationsUnread={organizationsError != null} value={value} set={set}
          onConfirm={confirmBlock} confirming={confirm.isPending} />

        {/* Объект — в шапке, без прокрутки (ТЗ COST-6.2): для большинства счетов разноска на нём и
            заканчивается. */}
        <InvoiceObject view={view} locked={closed || gone} />
      </div>

      {/* ── Остальное: прокручивается ────────────────────────────────────────── */}
      <div className="flex-1 min-h-0 overflow-y-auto px-5 py-4 space-y-6">
        {!quiet && <RecognitionNotes recognition={watch.recognition} />}
        {[...BLOCKS.slice(1), ...(extras.block ? [extras.block] : [])].map(block => (
          <section key={block.id} className="space-y-3">
            <BlockFields block={block} columns="sm:grid-cols-2" titled
              view={view} edits={edits} organizations={organizations} locked={locked}
              organizationsUnread={organizationsError != null} value={value} set={set}
              onConfirm={confirmBlock} confirming={confirm.isPending} />
          </section>
        ))}

        <UnshownExtras extras={extras} />
        {/* Метки у полей, которых форма не показывает (вид без поля ввода, схема не прочитана, поля нет в
            типе): править их здесь нечем — снимает только «Всё верно» этой строки. */}
        {orphanMarks.length > 0 && (
          <div className="flex items-center gap-3">
            <p className="flex-1 text-xs text-warning">
              Распознано, не подтверждено — у полей, которых форма не показывает:{' '}
              {orphanMarks.map(key => `«${extras.unshown.find(u => u.field.key === key)?.field.title || key}»`).join(', ')}.
              Сверьте их по скану.
            </p>
            {!quiet && (
              <Button size="sm" variant="outlined" icon={<CheckCheck size={13} />} loading={confirm.isPending}
                onClick={() => confirmBlock({ id: 'unshown', title: '', fields: orphanMarks })}>
                Всё верно ({orphanMarks.length})
              </Button>
            )}
          </div>
        )}

        <InvoiceLinesTable key={correction.generation} view={view} locked={closed || gone} readOnly={!canEdit} onDirty={setLinesDirty}
          headerDirty={dirty} onParsed={onExit && (send => onExit('parsed', send, () => {}))} />

        <p className="text-xs text-fg4">
          Весь счёт на один объект — поле «Объект» в шапке; на несколько — матрица оттуда же, а строку
          по отдельности — колонка «Разноска». Сохранение их не ждёт: черновик уже в реестре.
        </p>

        <CorrectionHistory view={view} gone={gone} />
      </div>
    </div>
    </InvoiceExtrasProvider>
    </InvoiceDeleteProvider>
    </RecognitionContext.Provider>
    </CorrectionContext.Provider>
    </SiteRevealScope>
  );
}

function BlockFields({
  block, columns, titled, view, edits, organizations, organizationsUnread, value, set,
  onConfirm, confirming, locked,
}: {
  block: InvoiceBlock; columns: string; titled?: boolean; locked: boolean;
  view: InvoiceView; edits: Record<string, unknown>; organizations: CostsOrganization[];
  organizationsUnread: boolean;
  value: (key: string) => unknown; set: (key: string, next: unknown) => void;
  onConfirm: (block: InvoiceBlock) => void; confirming: boolean;
}) {
  const marks = unconfirmedInBlock(block, view.unconfirmed);
  // В исправлении поля открыты, но то, что пишет само («Всё верно», прочитанное со скана), — нет.
  const correcting = useCorrecting() !== null;
  const quiet = locked || correcting;

  return (
    <>
      {(titled || marks.length > 0) && (
        <div className="flex items-center gap-3">
          {titled && <h2 className="text-sm font-medium text-fg2">{block.title}</h2>}
          <div className="flex-1" />
          {/* Кнопки нет, когда снимать нечего: пустой перечень сервер отвергает, и предлагать
              действие, которое заведомо откажет, — обман. */}
          {marks.length > 0 && !quiet && (
            <Button size="sm" variant="outlined" icon={<CheckCheck size={13} />} loading={confirming}
              onClick={() => onConfirm(block)}>
              Всё верно ({marks.length})
            </Button>
          )}
        </div>
      )}
      <div className={`grid grid-cols-1 ${columns} gap-3`}>
        {block.fields.map(key => (
          <div key={key} className="min-w-0" {...{ [READINESS_ATTR]: readinessAnchor({ place: 'field', key }) }}>
            <Field fieldKey={key} view={view} edits={edits} organizations={organizations}
              organizationsUnread={organizationsUnread} value={value} set={set} locked={locked} />
            {!quiet && (
              <InvoiceFieldScan fieldKey={key} view={view} edits={edits} set={set} organizations={organizations} />
            )}
          </div>
        ))}
      </div>
    </>
  );
}

function Field({ fieldKey, view, edits, organizations, organizationsUnread, value, set, locked }: {
  fieldKey: string; view: InvoiceView; edits: Record<string, unknown>;
  /** Счёт заперт закрытым периодом: поля только читаются. Читаются, а не отключены — приглушённое
   *  поле выглядит пустым, а причину называет полоса вверху. */
  locked: boolean;
  organizations: CostsOrganization[]; organizationsUnread: boolean;
  value: (key: string) => unknown; set: (key: string, next: unknown) => void;
}) {
  const marked = isMarked(fieldKey, view.unconfirmed, edits);
  const hint = marked ? UNCONFIRMED_HINT : undefined;
  // Обязательное для «Разобран» — пометкой у подписи (issue #1291). Перечень присылает сервер.
  const required = isRequired(view, fieldKey);
  const current = value(fieldKey);
  const extra = useExtraField(fieldKey);

  // Рамкой предупреждения, а не ошибки: распознанное значение не ошибка, его просто никто пока не
  // подтвердил. Красная рамка звала бы чинить то, что, скорее всего, верно.
  const frame = marked ? 'rounded-md ring-1 ring-warning-border' : '';

  if (extra) return <ExtraField field={extra} value={current} set={set} locked={locked} hint={hint} frame={frame} />;

  switch (fieldKey) {
    case K.supplier:
    case K.payer: {
      const entryId = refEntryId(current);

      // Ссылка есть, а записи нет — организацию удалили. Radix показал бы такое значение
      // ПЛЕЙСХОЛДЕРОМ «Выберите организацию», то есть соврал бы: поле выглядело бы незаполненным, и
      // человек, ничего не трогая, сохранил бы счёт со ссылкой в пустоту. Реестр в том же случае
      // честно пишет «организация удалена» — форма обязана говорить то же самое.
      //
      // Потерю называет СЕРВЕР (issue #1184), и только у значения, которое лежит в счёте: сравнение со
      // списком организаций показывало «удалена» у живого поставщика, пока список грузится. Старый
      // сервер состояния не присылает — тогда судим по списку, как раньше.
      const side = fieldKey === K.supplier ? 'supplier' : 'payer';
      const state = view.references?.[side];
      const storedId = refEntryId(view.requisites[fieldKey]);
      const stored = entryId !== null && entryId === storedId;
      // Организация, стоящая в счёте, в архиве (issue #1185). В списке на выбор её нет, название
      // едет с самим счётом. Пункт держится за СОХРАНЁННЫМ значением, а не за текущим: заменил,
      // передумал — вернул; сервер стоявшую ссылку принимает. После сохранения с другой он исчезнет.
      const kept = state === 'archived' && storedId !== null
        ? { id: storedId, name: withArchiveWord(view.references?.[`${side}Name`] ?? 'организация', true) }
        : null;
      const archived = kept !== null && entryId === kept.id && !organizations.some(o => o.id === kept.id);
      const lost = entryId !== null && (view.references
        ? stored && state === 'lost'
        : !organizationsUnread && !organizations.some(o => o.id === entryId));
      // Запись есть, но в списке организаций её нет — перевели в другой вид. Не потеря, но и молчать
      // нельзя: без своего пункта Radix показал бы плейсхолдер, и поле со ссылкой выглядело бы пустым
      // (ревью PR #1211). Пока список не пришёл (он пуст), судить не о чем.
      const foreign = entryId !== null && !lost && !archived
        && organizations.length > 0 && !organizations.some(o => o.id === entryId);
      const title = fieldKey === K.supplier ? 'Поставщик' : 'Плательщик';

      // Запертый счёт: выбор заменён полем для чтения, как у остальных, — отключённый выбор приглушён
      // и читается как «здесь ничего нет».
      if (locked)
        return (
          <TextField label={title} readOnly onChange={() => {}}
            value={organizationsUnread ? 'справочник не прочитан'
              : entryId === null ? '' : archived ? kept.name
              : organizations.find(o => o.id === entryId)?.name ?? (lost ? LOST.organization : foreign ? LOST.movedOrganization : '')} />
        );

      return (
        <div className={frame}>
          <Select label={title} required={required}
            disabled={organizationsUnread}
            hint={organizationsUnread ? 'Справочник не прочитан — выбор недоступен'
              : lost ? 'Запись справочника удалена. Счёт сохраняется и так; исправить — выбрать другую организацию'
              : foreign ? 'Запись есть, но она больше не организация. Выберите организацию заново'
              : archived ? 'Запись в архиве. Счёт сохраняется как есть; после замены выбрать её заново будет нельзя'
              : hint}
            value={entryId ?? NOT_CHOSEN} placeholder="Выберите организацию"
            onValueChange={id => set(fieldKey, id === NOT_CHOSEN ? null : catalogRef(id))}>
            {/* Пункт «не выбрано» — единственный способ СНЯТЬ ссылку: пустое значение Radix не
                отдаёт, и без него ошибочно распознанный плательщик оставался бы в записи навсегда. */}
            <SelectItem value={NOT_CHOSEN}>— не выбрано —</SelectItem>
            {lost && <SelectItem value={entryId}>{LOST.organization}</SelectItem>}
            {foreign && <SelectItem value={entryId}>{LOST.movedOrganization}</SelectItem>}
            {/* Список и счёт читаются порознь и могут разойтись на миг (организацию только что
                вернули): два пункта с одним значением Radix отметил бы выбранными оба. */}
            {kept && !organizations.some(o => o.id === kept.id) && <SelectItem value={kept.id}>{kept.name}</SelectItem>}
            {organizations.map(o => <SelectItem key={o.id} value={o.id}>{o.name}</SelectItem>)}
          </Select>
        </div>
      );
    }

    case K.dueDate:
      return <InvoiceDueField view={view} edits={edits} current={current} set={set} locked={locked}
        hint={hint} required={required} frame={frame} />;

    case K.date:
    case K.shippedOn:
      return (
        <div className={frame}>
          <DateField label={LABELS[fieldKey]} required={required} value={asInput(current).slice(0, 10)}
            hint={hints(hint, fieldKey === K.shippedOn && shippedBeforeIssued(current, value(K.date))
              ? 'Отгрузка раньше даты счёта — проверьте дату: от неё считается срок' : null)}
            readOnly={locked}
            onChange={iso => set(fieldKey, iso ? iso.slice(0, 10) : null)} />
        </div>
      );

    case K.state:
    case K.payment:
      // Показано, но не правится: состояния двигают действия, а не форма. Отключённым полем НЕ
      // делаем — приглушённое поле читается как «здесь ничего нет».
      return (
        <div className={frame}>
          <TextField label={LABELS[fieldKey]} readOnly value={asInput(current)}
            hint="Двигают действия, не форма" onChange={() => {}} />
        </div>
      );

    default: {
      const money = fieldKey === K.total || fieldKey === K.vat;
      const numeric = money || fieldKey === K.deferral;
      return (
        <div className={frame}>
          {/* У отсрочки — взята ли она из условий поставщика (ТЗ COST-9.2): иначе подставленное число
              выглядело бы вписанным руками, а расхождение с условиями — незаметным. */}
          <TextField label={LABELS[fieldKey] ?? fieldKey} readOnly={locked} required={required}
            hint={fieldKey === K.deferral ? hints(hint, deferralNote(view, edits)) : hint}
            value={money ? moneyInput(current) : asInput(current)}
            inputMode={numeric ? 'decimal' : undefined}
            onChange={e => set(fieldKey, fromInput(e.target.value))} />
        </div>
      );
    }
  }
}

/** Значение пункта «не выбрано»: пустую строку Radix не принимает, а ссылку снимать чем-то надо. */
const NOT_CHOSEN = 'не-выбрано';

const LABELS: Record<string, string> = {
  [K.number]: 'Номер',
  [K.date]: 'Дата',
  [K.supplier]: 'Поставщик',
  [K.payer]: 'Плательщик',
  [K.basis]: 'Основание',
  [K.purpose]: 'Назначение',
  [K.total]: 'Сумма к оплате',
  [K.vat]: 'В том числе НДС',
  [K.shippedOn]: 'Дата отгрузки',
  [K.deferral]: 'Отсрочка, дней',
  [K.dueDate]: 'Оплатить до',
  [K.state]: 'Состояние',
  [K.payment]: 'Оплата',
};

function StateChip({ text }: { text: string }) {
  if (!text) return null;
  return <StatusBadge>{text}</StatusBadge>;
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

