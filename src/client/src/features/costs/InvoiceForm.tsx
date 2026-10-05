import { useMemo, useState, type ReactNode } from 'react';
import { CheckCheck, Copy, Save, Sparkles, TriangleAlert } from 'lucide-react';
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
  BLOCKS, K, UNCONFIRMED_HINT, asInput, catalogRef, duplicateLabel, fromInput,
  isMarked, moneyInput, refEntryId, toRequisites, unconfirmedInBlock, unconfirmedOutsideBlocks,
  type InvoiceBlock,
} from './invoiceFields';
import { formatDate } from '@/shared/format/format';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import { InvoiceObject } from './InvoiceObject';
import { InvoiceLockNote, InvoicePayment } from './InvoicePayment';
import { ScanUploadButton } from './InvoiceScanPanel';

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
export function InvoiceForm({ view, organizations, organizationsError, onOpenInvoice, scanSlot }: {
  view: InvoiceView;
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

  const value = (key: string): unknown => (key in edits ? edits[key] : view.requisites[key]);
  const set = (key: string, next: unknown) => setEdits(prev => ({ ...prev, [key]: next }));
  const dirty = Object.keys(edits).length > 0;

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

  // Заперт — слово сервера (`lockedBy`), а не «оплачен»: оплаченный счёт открытого периода правится.
  const closed = view.payment.lockedBy !== null;
  // Без права вводить счета (бухгалтер) форма читается так же, как запертая: действий записи нет вовсе,
  // а не «есть и откажут» — каждое из них ответило бы 403 (N1, issue #1102). Разноска и оплата — свои
  // права, их это не касается.
  const canEdit = useCan().permission('costs.invoice.edit');
  const locked = closed || !canEdit;

  const scan = useMemo(() => scanOf(view), [view]);
  const orphanMarks = unconfirmedOutsideBlocks(view.unconfirmed);

  // Уход из раздела с несохранёнными правками спрашивает (G4, issue #1097). Открытый счёт назван в
  // адресе, и пункт «Счета» в навигации ведёт на адрес БЕЗ счёта: форма закрылась бы вместе с
  // правками — молча. Пока выбор жил в состоянии экрана, тот же щелчок не делал ничего.
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(dirty, proceed => setLeave(() => proceed));

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

  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <LeaveGuardDialog open={leave !== null} saving={update.isPending}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const go = leave; setLeave(null); go?.(); }}
        onSave={async () => { const go = leave; setLeave(null); if (await save()) go?.(); }} />
      {/* ── Шапка: без прокрутки ─────────────────────────────────────────────── */}
      <div className="shrink-0 border-b border-stroke bg-surface px-5 py-3 space-y-3">
        {base.stale && <StaleInvoiceNotice what="поля счёта" onReread={reread} />}
        <div className="flex items-center gap-2 flex-wrap">
          <StateChip text={asInput(view.requisites[K.state])} />
          <StateChip text={view.payment.paid && view.payment.paidOn
            ? `Оплачен ${formatDate(view.payment.paidOn)}` : asInput(view.requisites[K.payment])} />
          {closed && <StateChip text="Заперт" />}
          {view.unconfirmed.length > 0 && (
            <span className="inline-flex items-center gap-1 text-xs text-warning">
              <Sparkles size={12} /> распознано, не подтверждено: {view.unconfirmed.length}
            </span>
          )}
          <div className="flex-1" />
          {/* К запертому счёту скан приложить можно, заменить — нельзя: замена удалила бы документ
              закрытого периода. Кнопки замены нет вовсе, а не «есть и откажет». */}
          {!canEdit && <span className="text-xs text-fg3">Только чтение: права вводить счета нет</span>}
          {canEdit && closed && scan !== null && (
            <span className="text-xs text-fg3">Скан заменить нельзя: документ закрытого периода</span>
          )}
          {canEdit && !(closed && scan !== null) && (
            <ScanUploadButton hasScan={scan !== null} busy={attach.isPending}
              onPick={file => {
                attach.mutateAsync({ id: view.id, seen: view.version, file }).catch(e => toast.apiError(e, 'Скан не приложен'));
              }} />
          )}
          {!locked && (
            <Button variant="filled" size="sm" icon={<Save size={14} />} disabled={!dirty}
              loading={update.isPending} onClick={save}>
              Сохранить
            </Button>
          )}
        </div>

        {view.payment.lockedBy !== null && <InvoiceLockNote lockedBy={view.payment.lockedBy} />}
        <InvoicePayment view={view} dirty={dirty} />

        {organizationsError != null && (
          <div className="flex items-start gap-2 rounded-lg border border-danger-border bg-danger-subtle
            px-3 py-2 text-xs text-danger">
            <TriangleAlert size={14} className="shrink-0 mt-0.5" />
            <span>
              Справочник организаций не прочитан: {apiError(organizationsError, 'сервер отказал')}.
              Поставщика и плательщика выбрать не из чего — остальные поля правятся и сохраняются.
              ⚠️ Это НЕ «организаций нет»: их список сюда не доехал.
            </span>
          </div>
        )}

        {view.duplicates.length > 0 && <DuplicateNote view={view} onOpenInvoice={onOpenInvoice} />}
        {scanSlot}

        <BlockFields block={BLOCKS[0]} columns="sm:grid-cols-2 lg:grid-cols-4"
          view={view} edits={edits} organizations={organizations} locked={locked}
          organizationsUnread={organizationsError != null} value={value} set={set}
          onConfirm={confirmBlock} confirming={confirm.isPending} />

        {/* Объект — в шапке, без прокрутки (ТЗ COST-6.2): для большинства счетов разноска на нём и
            заканчивается. */}
        <InvoiceObject view={view} locked={closed} />
      </div>

      {/* ── Остальное: прокручивается ────────────────────────────────────────── */}
      <div className="flex-1 min-h-0 overflow-y-auto px-5 py-4 space-y-6">
        {BLOCKS.slice(1).map(block => (
          <section key={block.id} className="space-y-3">
            <BlockFields block={block} columns="sm:grid-cols-2" titled
              view={view} edits={edits} organizations={organizations} locked={locked}
              organizationsUnread={organizationsError != null} value={value} set={set}
              onConfirm={confirmBlock} confirming={confirm.isPending} />
          </section>
        ))}

        {orphanMarks.length > 0 && (
          <p className="text-xs text-warning">
            Метки есть и у полей, которых нет в блоках формы ({orphanMarks.join(', ')}) — это поля,
            дописанные в тип. Снять метку с них можно правкой значения.
          </p>
        )}

        <InvoiceLinesTable view={view} locked={closed} readOnly={!canEdit} />

        <p className="text-xs text-fg4">
          Весь счёт на один объект — поле «Объект» в шапке; на несколько — матрица оттуда же, а строку
          по отдельности — колонка «Разноска». Сохранение их не ждёт: черновик уже в реестре.
        </p>
      </div>
    </div>
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

  return (
    <>
      {(titled || marks.length > 0) && (
        <div className="flex items-center gap-3">
          {titled && <h2 className="text-sm font-medium text-fg2">{block.title}</h2>}
          <div className="flex-1" />
          {/* Кнопки нет, когда снимать нечего: пустой перечень сервер отвергает, и предлагать
              действие, которое заведомо откажет, — обман. */}
          {marks.length > 0 && !locked && (
            <Button size="sm" variant="outlined" icon={<CheckCheck size={13} />} loading={confirming}
              onClick={() => onConfirm(block)}>
              Всё верно ({marks.length})
            </Button>
          )}
        </div>
      )}
      <div className={`grid grid-cols-1 ${columns} gap-3`}>
        {block.fields.map(key => (
          <Field key={key} fieldKey={key} view={view} edits={edits} organizations={organizations}
            organizationsUnread={organizationsUnread} value={value} set={set} locked={locked} />
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
  const current = value(fieldKey);

  // Рамкой предупреждения, а не ошибки: распознанное значение не ошибка, его просто никто пока не
  // подтвердил. Красная рамка звала бы чинить то, что, скорее всего, верно.
  const frame = marked ? 'rounded-md ring-1 ring-warning-border' : '';

  switch (fieldKey) {
    case K.supplier:
    case K.payer: {
      const entryId = refEntryId(current);

      // Ссылка есть, а записи нет — организацию удалили. Radix показал бы такое значение
      // ПЛЕЙСХОЛДЕРОМ «Выберите организацию», то есть соврал бы: поле выглядело бы незаполненным, и
      // человек, ничего не трогая, сохранил бы счёт со ссылкой в пустоту. Реестр в том же случае
      // честно пишет «организация не найдена» — форма обязана говорить то же самое.
      const lost = entryId !== null && !organizations.some(o => o.id === entryId);
      const title = fieldKey === K.supplier ? 'Поставщик' : 'Плательщик';

      // Запертый счёт: выбор заменён полем для чтения, как у остальных, — отключённый выбор приглушён
      // и читается как «здесь ничего нет».
      if (locked)
        return (
          <TextField label={title} readOnly onChange={() => {}}
            value={organizationsUnread ? 'справочник не прочитан'
              : entryId === null ? '' : organizations.find(o => o.id === entryId)?.name ?? 'организация не найдена'} />
        );

      return (
        <div className={lost ? 'rounded-md ring-1 ring-danger-border' : frame}>
          <Select label={title}
            disabled={organizationsUnread}
            hint={organizationsUnread ? 'Справочник не прочитан — выбор недоступен'
              : lost ? 'Ссылка есть, а записи нет: организацию удалили' : hint}
            value={entryId ?? NOT_CHOSEN} placeholder="Выберите организацию"
            onValueChange={id => set(fieldKey, id === NOT_CHOSEN ? null : catalogRef(id))}>
            {/* Пункт «не выбрано» — единственный способ СНЯТЬ ссылку: пустое значение Radix не
                отдаёт, и без него ошибочно распознанный плательщик оставался бы в записи навсегда. */}
            <SelectItem value={NOT_CHOSEN}>— не выбрано —</SelectItem>
            {lost && <SelectItem value={entryId}>организация не найдена ({entryId.slice(0, 8)}…)</SelectItem>}
            {organizations.map(o => <SelectItem key={o.id} value={o.id}>{o.name}</SelectItem>)}
          </Select>
        </div>
      );
    }

    case K.date:
    case K.shippedOn:
    case K.dueDate:
      return (
        <div className={frame}>
          <DateField label={LABELS[fieldKey]} hint={hint} value={asInput(current).slice(0, 10)}
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
          <TextField label={LABELS[fieldKey] ?? fieldKey} hint={hint} readOnly={locked}
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

/**
 * Оговорка о дубликате — СО ССЫЛКОЙ и БЕЗ запрета (ТЗ COST-6.2).
 *
 * У поставщика бывает два счёта с одним номером в один день, и человек знает об этом больше нас.
 * Поэтому здесь ссылка «посмотреть тот счёт», а не отказ сохранения.
 */
function DuplicateNote({ view, onOpenInvoice }: { view: InvoiceView; onOpenInvoice: (id: string) => void }) {
  return (
    <div className="flex items-start gap-2 rounded-lg border border-warning-border bg-warning-subtle px-3 py-2">
      <Copy size={14} className="text-warning shrink-0 mt-0.5" />
      <div className="min-w-0 text-xs text-warning">
        <span>Похоже на дубликат — у этого поставщика уже есть счёт с тем же номером и датой: </span>
        {view.duplicates.map((d, i) => (
          <span key={d.id}>
            {i > 0 && ', '}
            <button type="button" className="underline hover:no-underline font-medium"
              onClick={() => onOpenInvoice(d.id)}>
              {duplicateLabel(d)}
            </button>
          </span>
        ))}
        <span>. Сохранить всё равно можно: одинаковые номера у поставщика бывают.</span>
      </div>
    </div>
  );
}

function StateChip({ text }: { text: string }) {
  if (!text) return null;
  return <span className="text-xs px-2 py-0.5 rounded-full bg-surface2 text-fg2">{text}</span>;
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

