import { useState } from 'react';
import { CircleCheck, Plus, Save, Trash2, Undo2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import {
  useInvoiceState, useReplaceInvoiceLines, type InvoiceLineView, type InvoiceView,
} from '@/shared/api/invoices';
import { K } from './invoiceFields';
import { formatInputAmount, formatMoney } from '@/shared/format/format';
import { NumberInput } from '@/shared/ui/NumberInput';
import {
  emptyDraft, mismatch, preview, toDrafts, toPayload, totals, type LineDraft,
} from './invoiceLines';
import { InvoiceLinesPaste } from './InvoiceLinesPaste';
import { NomenclaturePicker } from './NomenclaturePicker';
import { AllocationSummary, LineAllocationCell } from './LineAllocation';

/**
 * Строки счёта (задача C2, issue #1078, ТЗ COST-7, COST-7.2, COST-6.2).
 *
 * <p><b>Сверка суммы строк с суммой к оплате — всегда на виду и не запрет.</b> Расхождение показано
 * числом: у поставщика бывает округление, скидка строкой и доставка, не попавшая в таблицу, — и человек
 * знает об этом больше нас. Запрет означал бы, что счёт нельзя завести, пока он не сойдётся, а заводят
 * его как раз затем, чтобы разбираться.</p>
 *
 * <p><b>Строка без позиции номенклатуры сохраняется.</b> Обязательность проверяется на переходе
 * «разобран», а не при сохранении: наименования в бумаге — слова поставщика, и сопоставить их может
 * только человек или таблица соответствий (C3).</p>
 *
 * <p>⚠️ <b>«Разобран» не отключается, даже когда заведомо откажет.</b> Приглушённая кнопка молчит о
 * причине, а причин много (нет строк, ждут позиции, не заполнено обязательное, не сходится разноска,
 * счёт отклонён), и знает их сервер. Поэтому кнопка живая, а отказ приезжает от сервера с перечнем строк и полей.</p>
 *
 * <p>⚠️ Правки строк прежнего счёта сбрасывает ПЕРЕМОНТИРОВАНИЕ (`key={view.id}` у формы) — тем же
 * приёмом, что правки шапки: эффект со сбросом состояния рисует лишний кадр, в котором строки одного
 * счёта стоят в другом.</p>
 */
export function InvoiceLinesTable({ view, locked, readOnly = false }: {
  view: InvoiceView;
  /** Счёт заперт закрытым периодом: строки только читаются, действий над ними нет — убраны, а не
   *  приглушены; причину называет полоса вверху формы. */
  locked: boolean;
  /** Права вводить счета нет: строки читаются так же, но разноска остаётся за своим правом. */
  readOnly?: boolean;
}) {
  const still = locked || readOnly;
  const [drafts, setDrafts] = useState<LineDraft[]>(() => toDrafts(view.lines));
  const [dirty, setDirty] = useState(false);
  const replace = useReplaceInvoiceLines();
  const state = useInvoiceState();
  const toast = useToast();

  const sums = totals(drafts);
  const paper = view.requisites[K.total];
  const difference = mismatch(paper, sums.amount, sums.count);
  const parsed = view.requisites[K.state] === 'Разобран';

  function edit(key: string, patch: Partial<LineDraft>) {
    setDrafts(prev => prev.map(draft => (draft.key === key ? { ...draft, ...patch } : draft)));
    setDirty(true);
  }

  function add(added: LineDraft[]) {
    setDrafts(prev => [...prev, ...added]);
    setDirty(true);
  }

  function remove(key: string) {
    setDrafts(prev => prev.filter(draft => draft.key !== key));
    setDirty(true);
  }

  async function save() {
    try {
      const saved = await replace.mutateAsync({ id: view.id, lines: toPayload(drafts) });
      // Строки перечитываем ИЗ ОТВЕТА: сервер вернул досчитанные суммы и идентификаторы новых строк,
      // а без них следующее сохранение прочиталось бы как «удали эти строки и заведи новые».
      setDrafts(toDrafts(saved.lines));
      setDirty(false);
    } catch (e) {
      toast.apiError(e, 'Строки не сохранены');
    }
  }

  async function move(to: 'parsed' | 'draft') {
    try { await state.mutateAsync({ id: view.id, to }); }
    catch (e) { toast.apiError(e, to === 'parsed' ? 'Счёт не разобран' : 'Счёт не возвращён в черновик'); }
  }

  return (
    <section className="space-y-3">
      <div className="flex items-center gap-2 flex-wrap">
        <h2 className="text-sm font-medium text-fg2">Строки счёта</h2>
        <div className="flex-1" />
        {!still && (
          <>
            <InvoiceLinesPaste onAdd={add} />
            <Button size="sm" variant="outlined" icon={<Plus size={13} />} onClick={() => add([emptyDraft()])}>
              Добавить строку
            </Button>
            <Button size="sm" variant="filled" icon={<Save size={13} />} disabled={!dirty}
              loading={replace.isPending} onClick={save}>
              Сохранить строки
            </Button>
          </>
        )}
      </div>

      {drafts.length === 0
        ? (
          <p className="text-xs text-fg4">
            Строк нет. Черновик живёт и без них — заведите их с клавиатуры или вставьте таблицу из
            счёта. «Разобран» без строк не проходит, и это единственное, чего они стоят.
          </p>
        )
        : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[68rem] text-xs">
              <thead className="text-fg4">
                <tr className="text-left">
                  <th className="w-8 font-normal py-1">№</th>
                  <th className="w-56 font-normal py-1">Позиция номенклатуры</th>
                  <th className="w-56 font-normal py-1">Наименование в счёте</th>
                  <th className="w-24 font-normal py-1">Артикул</th>
                  <th className="w-16 font-normal py-1">Ед.</th>
                  <th className="w-20 font-normal py-1 text-right">Кол-во</th>
                  <th className="w-24 font-normal py-1 text-right">Цена</th>
                  <th className="w-16 font-normal py-1 text-right">НДС&nbsp;%</th>
                  <th className="w-24 font-normal py-1 text-right">Сумма НДС</th>
                  <th className="w-28 font-normal py-1 text-right">Сумма</th>
                  <th className="w-40 font-normal py-1">Примечание</th>
                  <th className="w-32 font-normal py-1">Разноска</th>
                  <th className="w-8 py-1" />
                </tr>
              </thead>
              <tbody>
                {drafts.map((draft, index) => (
                  <Row key={draft.key} draft={draft} number={index + 1} invoiceId={view.id}
                    line={view.lines.find(line => line.id === draft.id)}
                    blocked={dirty ? 'Разносить можно сохранённые строки: сохраните правки строк' : null}
                    locked={still} allocationLocked={locked}
                    onEdit={patch => edit(draft.key, patch)} onRemove={() => remove(draft.key)} />
                ))}
              </tbody>
            </table>
          </div>
        )}

      <Reconciliation sums={sums} paper={paper} difference={difference} unsaved={dirty} />

      <div className="flex items-center gap-2 flex-wrap">
        {still ? null : parsed
          ? (
            <Button size="sm" variant="outlined" icon={<Undo2 size={13} />} loading={state.isPending}
              onClick={() => move('draft')}>
              Вернуть в черновик
            </Button>
          )
          : (
            <Button size="sm" variant="outlined" icon={<CircleCheck size={13} />}
              loading={state.isPending} onClick={() => move('parsed')}>
              Разобран
            </Button>
          )}
        {/* У разобранного — тоже, если разноска не сходится: счёт, разобранный до F1 (#1085), разноски
            не имеет, и фраза «разноска сходится» ниже была бы про него неправдой. */}
        {(!parsed || !view.allocation.allocated) && view.lines.length > 0
          && <AllocationSummary allocation={view.allocation} />}
        <span className="text-xs text-fg4">
          {parsed
            ? view.allocation.allocated
              ? 'Счёт разобран: строки есть, у всех позиция, обязательные поля заполнены, разноска сходится.'
              : 'Счёт разобран до разноски по стройкам. Пока строки не разнесены, его деньги не относятся ни к одной стройке.'
            : 'Разобран — это утверждение человека, что счёт сверен с бумагой. Условия проверит сервер '
              + 'и назовёт, чего не хватает.'}
        </span>
      </div>
    </section>
  );
}

/**
 * Сверка. Показывает ЧЕТЫРЕ числа: сумму строк, НДС по строкам, сумму к оплате из бумаги и
 * расхождение. Ни одно из них не запрет.
 *
 * <p>⚠️ Пока строки не сохранены, числа посчитаны ЗДЕСЬ, и об этом сказано: после сохранения их
 * возвращает сервер, и они могут разойтись с нашими на копейку округления. Молчаливое «сумма 4850»,
 * которое после сохранения стало «4850,01», читалось бы как ошибка ввода.</p>
 */
function Reconciliation({ sums, paper, difference, unsaved }: {
  sums: { count: number; withoutNomenclature: number; amount: number; vat: number };
  paper: unknown;
  difference: number | null;
  unsaved: boolean;
}) {
  return (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1 rounded-lg border border-stroke
      bg-surface2 px-3 py-2 text-xs">
      <span className="text-fg2">Строк: <b>{sums.count}</b></span>

      {sums.withoutNomenclature > 0 && (
        <span className="text-warning">ждут позиции: <b>{sums.withoutNomenclature}</b></span>
      )}

      <span className="text-fg2">Сумма строк: <b>{formatMoney(sums.amount)}</b></span>
      <span className="text-fg4">в том числе НДС: {formatMoney(sums.vat)}</span>

      {typeof paper === 'number' && (
        <span className="text-fg4">к оплате по счёту: {formatMoney(paper)}</span>
      )}

      {difference !== null && (
        <span className="text-warning">
          расхождение: <b>{formatMoney(difference)}</b> — сохранить всё равно можно
        </span>
      )}

      {unsaved && <span className="text-fg4">(посчитано в форме — строки ещё не сохранены)</span>}
    </div>
  );
}

function Row({ draft, number, invoiceId, line, blocked, locked, allocationLocked, onEdit, onRemove }: {
  draft: LineDraft;
  number: number;
  invoiceId: string;
  /** Строка, как её вернул сервер, — с разноской; у новой строки её нет. */
  line: InvoiceLineView | undefined;
  blocked: string | null;
  locked: boolean;
  /** Разноску запирает только закрытый период: без права на счета её по-прежнему решает своё право. */
  allocationLocked: boolean;
  onEdit: (patch: Partial<LineDraft>) => void;
  onRemove: () => void;
}) {
  const shown = preview(draft);

  if (locked)
    return <LockedRow draft={draft} number={number} invoiceId={invoiceId} line={line} allocationLocked={allocationLocked} />;

  return (
    <tr className="border-t border-stroke align-top">
      <td className="py-1 text-fg4">{number}</td>
      <td className="py-1 pr-2">
        {/* Потерю называет СЕРВЕР: у формы на все случаи пустого названия один признак, и выбранная
            только что позиция без имени краснела бы как потерянная. Выбор и снятие потерю снимают —
            ссылка меняется здесь же, и прежний ответ сервера к ней уже не относится. */}
        <NomenclaturePicker chosen={draft.nomenclatureId !== null} name={draft.nomenclatureName}
          lost={draft.nomenclatureLost}
          onPick={(id, name) => onEdit({
            nomenclatureId: id, nomenclatureName: name, nomenclatureLost: false,
          })}
          onClear={() => onEdit({
            nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
          })} />
      </td>
      <Cell value={draft.supplierText} label={`Наименование в счёте, строка ${number}`}
        onChange={value => onEdit({ supplierText: value })} />
      <Cell value={draft.supplierCode} label={`Артикул, строка ${number}`}
        onChange={value => onEdit({ supplierCode: value })} />
      <Cell value={draft.unit} label={`Единица, строка ${number}`}
        onChange={value => onEdit({ unit: value })} />
      <Cell value={draft.quantity} label={`Количество, строка ${number}`} numeric
        onChange={value => onEdit({ quantity: value })} />
      <Cell value={draft.price} label={`Цена, строка ${number}`} numeric
        onChange={value => onEdit({ price: value })} />
      <Cell value={draft.vatRate} label={`Ставка НДС, строка ${number}`} numeric
        onChange={value => onEdit({ vatRate: value })} />
      <Cell value={draft.vatAmount} label={`Сумма НДС, строка ${number}`} numeric
        placeholder={shown.vat === null ? '' : formatInputAmount(shown.vat)}
        onChange={value => onEdit({ vatAmount: value })} />
      <Cell value={draft.amount} label={`Сумма, строка ${number}`} numeric
        placeholder={shown.amount === null ? '' : formatInputAmount(shown.amount)}
        onChange={value => onEdit({ amount: value })} />
      <Cell value={draft.note} label={`Примечание, строка ${number}`}
        onChange={value => onEdit({ note: value })} />
      <LineAllocationCell invoiceId={invoiceId} line={line} number={number} blocked={blocked} locked={false} />
      <td className="py-1">
        <button type="button" onClick={onRemove} title={`Удалить строку ${number}`}
          className="text-fg4 hover:text-danger p-0.5">
          <Trash2 size={13} />
        </button>
      </td>
    </tr>
  );
}

/**
 * Строка запертого счёта — текстом, без полей: править её нельзя, и поле, в которое можно печатать,
 * обещало бы обратное. Разноска открывается — посмотреть.
 */
function LockedRow({ draft, number, invoiceId, line, allocationLocked }: {
  draft: LineDraft; number: number; invoiceId: string; line: InvoiceLineView | undefined; allocationLocked: boolean;
}) {
  const shown = preview(draft);
  const text = (value: string, numeric = false) => (
    <td className={`py-1 pr-2 text-fg1 ${numeric ? 'text-right tabular-nums' : ''}`}>{value || '—'}</td>
  );

  return (
    <tr className="border-t border-stroke align-top">
      <td className="py-1 text-fg4">{number}</td>
      <td className={`py-1 pr-2 ${draft.nomenclatureLost ? 'text-danger' : 'text-fg1'}`}>
        {draft.nomenclatureLost ? 'позиция не найдена' : draft.nomenclatureName ?? '—'}
      </td>
      {text(draft.supplierText)}
      {text(draft.supplierCode)}
      {text(draft.unit)}
      {text(draft.quantity, true)}
      {text(draft.price, true)}
      {text(draft.vatRate, true)}
      {text(draft.vatAmount || (shown.vat === null ? '' : formatInputAmount(shown.vat)), true)}
      {text(draft.amount || (shown.amount === null ? '' : formatInputAmount(shown.amount)), true)}
      {text(draft.note)}
      <LineAllocationCell invoiceId={invoiceId} line={line} number={number} blocked={null} locked={allocationLocked} />
      <td />
    </tr>
  );
}

/**
 * Клетка таблицы — своим полем, а не общим `TextField`: тот рисует подпись над полем, а в таблице
 * подпись стоит в шапке колонки, и повторять её двенадцать раз значит потерять таблицу.
 *
 * <p>Подпись при этом есть — невидимая (`aria-label`): без неё поле не имеет имени ни для чтения с
 * экрана, ни для живого прогона, который ищет клетку по имени.</p>
 *
 * <p>⚠️ Подсказка в пустом числовом поле — это ПОСЧИТАННОЕ значение («48,50»), а не пример. Так видно,
 * что сумму можно не набирать: её досчитают по количеству и цене. Набранное значение подсказку
 * перекрывает, и досчитанного тогда нет — присланное не пересчитывается.</p>
 */
function Cell({ value, label, numeric, placeholder, onChange }: {
  value: string;
  label: string;
  numeric?: boolean;
  placeholder?: string;
  onChange: (value: string) => void;
}) {
  if (numeric) {
    return (
      <td className="py-1 pr-2">
        <NumberInput value={value} label={label} placeholder={placeholder} onChange={onChange} />
      </td>
    );
  }
  return (
    <td className="py-1 pr-2">
      <input value={value} aria-label={label} placeholder={placeholder}
        onChange={e => onChange(e.target.value)}
        className="w-full rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg
          outline-none focus:border-primary placeholder:text-fg4" />
    </td>
  );
}
