import { useState } from 'react';
import { Plus, Save, Trash2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import {
  useCostsConstructions, useReplaceAllocation,
  type AllocationSummaryView, type InvoiceLineView,
} from '@/shared/api/invoices';
import { formatMoney } from './invoiceFields';
import {
  allocationStatus, emptyPart, estimateRemainder, formatPlain, toPartDrafts, toPartsPayload, type PartDraft,
} from './allocation';

/**
 * Разноска строки счёта по стройкам (задача F1, issue #1085, ТЗ COST-10, COST-11, COST-13).
 *
 * <p><b>Разносится строка, а не счёт.</b> Клетка в таблице строк говорит состояние одной фразой и
 * открывает диалог частей. Быстрая разноска всего счёта одной пропорцией и матрица «строки × объекты» —
 * задача F2; здесь — единица, из которой они сложатся.</p>
 *
 * <p>⚠️ <b>Пока строки не сохранены, разносить нельзя</b>, и кнопка говорит почему. Разноска ссылается
 * на СОХРАНЁННУЮ строку: у новой строки нет идентификатора, а у изменённой количество в форме уже не то,
 * что на сервере, — и остаток, посчитанный по одному, разошёлся бы с отказом, посчитанным по другому.</p>
 */
export function LineAllocationCell({ invoiceId, line, number, blocked }: {
  invoiceId: string;
  line: InvoiceLineView | undefined;
  number: number;
  /** Причина, по которой разносить сейчас нельзя (строки не сохранены), либо `null`. */
  blocked: string | null;
}) {
  const [open, setOpen] = useState(false);

  if (!line || blocked)
    return (
      <td className="py-1 pr-2 text-fg4" title={blocked ?? 'Строка не сохранена'}>
        {blocked ? 'сохраните строки' : '—'}
      </td>
    );

  const status = allocationStatus(line.allocation, line.unit);
  const tone = status.tone === 'ok' ? 'text-success' : status.tone === 'warning' ? 'text-warning' : 'text-fg4';

  return (
    <td className="py-1 pr-2">
      <button type="button" onClick={() => setOpen(true)} disabled={line.allocation.mode === 'none'
        && line.allocation.parts.length === 0}
        aria-label={`Разноска, строка ${number}`}
        className={`text-left underline decoration-dotted underline-offset-2 disabled:no-underline ${tone}`}>
        {status.text}
      </button>
      {open && <LineAllocationDialog invoiceId={invoiceId} line={line} number={number}
        onClose={() => setOpen(false)} />}
    </td>
  );
}

/**
 * Диалог частей строки. Монтируется только открытым — черновик частей всегда начинается с того, что
 * вернул сервер, и правки прошлого открытия не всплывают в следующем.
 *
 * <p>⚠️ Строка «не разнесено» стоит ВСЕГДА, в том числе при нуле (ТЗ COST-13): исчезающая строка
 * остатка не отличима от забытой.</p>
 */
function LineAllocationDialog({ invoiceId, line, number, onClose }: {
  invoiceId: string;
  line: InvoiceLineView;
  number: number;
  onClose: () => void;
}) {
  const { mode } = line.allocation;
  const [drafts, setDrafts] = useState<PartDraft[]>(() => {
    const saved = toPartDrafts(line.allocation);
    return saved.length > 0 ? saved : [emptyPart()];
  });
  const [dirty, setDirty] = useState(false);
  const sites = useCostsConstructions();
  const replace = useReplaceAllocation();
  const toast = useToast();

  const byQuantity = mode === 'quantity';
  const unit = line.unit ?? '';
  const rest = dirty
    ? estimateRemainder(drafts, line, mode)
    : { quantity: line.allocation.unallocatedQuantity, amount: line.allocation.unallocatedAmount };

  function edit(key: string, patch: Partial<PartDraft>) {
    setDrafts(prev => prev.map(d => (d.key === key ? { ...d, ...patch } : d)));
    setDirty(true);
  }

  async function save() {
    try {
      await replace.mutateAsync({ id: invoiceId, lineId: line.id, parts: toPartsPayload(drafts, mode) });
      onClose();
    } catch (e) {
      toast.apiError(e, 'Разноска не сохранена');
    }
  }

  const title = `Разноска строки ${number}: ${line.nomenclatureName ?? line.supplierText ?? 'без наименования'}`;

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={title} wide isDirty={dirty}
      footer={(
        <>
          <span className="text-xs text-fg4 mr-auto">
            {byQuantity
              ? `В строке ${formatPlain(line.quantity)} ${unit}`.trim()
                + (line.amount === null ? '' : ` на ${formatMoney(line.amount)}`)
                + '. Делится количество, сумма части считается.'
              : `Количества у строки нет — делится сумма ${formatMoney(line.amount ?? 0)}.`}
          </span>
          <Button variant="text" onClick={onClose}>Отмена</Button>
          <Button variant="filled" icon={<Save size={13} />} loading={replace.isPending} onClick={save}>
            Сохранить разноску
          </Button>
        </>
      )}>
      <table className="w-full text-xs">
        <thead className="text-fg4">
          <tr className="text-left">
            <th className="font-normal py-1">Стройка</th>
            <th className="font-normal py-1">Раздел</th>
            <th className="w-28 font-normal py-1 text-right">{byQuantity ? `Кол-во${unit ? `, ${unit}` : ''}` : 'Сумма'}</th>
            {byQuantity && <th className="w-32 font-normal py-1 text-right">Сумма части</th>}
            <th className="w-8 py-1" />
          </tr>
        </thead>
        <tbody>
          {drafts.map((draft, index) => {
            const saved = line.allocation.parts.find(p => p.id === draft.id);
            const site = sites.data?.find(s => s.id === draft.constructionId);
            return (
              <tr key={draft.key} className="border-t border-stroke align-top">
                <td className="py-1 pr-2">
                  <select value={draft.constructionId} aria-label={`Стройка, часть ${index + 1}`}
                    onChange={e => edit(draft.key, { constructionId: e.target.value, sectionId: '' })}
                    className={FIELD}>
                    <option value="">— выберите —</option>
                    {saved?.targetLost && draft.constructionId === saved.constructionId && !site && (
                      <option value={saved.constructionId}>стройка удалена</option>
                    )}
                    {sites.data?.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                  </select>
                </td>
                <td className="py-1 pr-2">
                  {/* Раздел удалён, стройка на месте: значение черновика не совпадёт ни с одним пунктом,
                      и без своего пункта поле показало бы «вся стройка», а уехал бы удалённый раздел. */}
                  <select value={draft.sectionId} aria-label={`Раздел, часть ${index + 1}`}
                    disabled={!site || (site.sections.length === 0 && !draft.sectionId)}
                    onChange={e => edit(draft.key, { sectionId: e.target.value })} className={FIELD}>
                    <option value="">— вся стройка —</option>
                    {site && draft.sectionId && !site.sections.some(s => s.id === draft.sectionId) && (
                      <option value={draft.sectionId}>раздел удалён</option>
                    )}
                    {site?.sections.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                  </select>
                </td>
                <td className="py-1 pr-2">
                  <input value={draft.value} inputMode="decimal"
                    aria-label={`${byQuantity ? 'Количество' : 'Сумма'}, часть ${index + 1}`}
                    onChange={e => edit(draft.key, { value: e.target.value })}
                    className={`${FIELD} text-right tabular-nums`} />
                </td>
                {byQuantity && (
                  <td className="py-1 pr-2 text-right tabular-nums text-fg2">
                    {/* Сумма части — ответ сервера, и только для несменённой части: у правленой
                        прежнее число уже неверно, а своё форма не считает. */}
                    {!dirty && saved?.amount != null ? formatMoney(saved.amount) : '—'}
                    {!dirty && saved && saved.rounding !== 0 && (
                      <div className="text-fg4" title="Копейки округления уходят в последнюю часть — так сумма частей равна сумме строки">
                        копейки округления: {formatMoney(saved.rounding)}
                      </div>
                    )}
                    {!dirty && saved && saved.discrepancy !== 0 && (
                      <div className="text-fg4" title="Расхождение суммы строк с суммой к оплате в пределах допуска уходит в последнюю часть счёта">
                        расхождение со счётом: {formatMoney(saved.discrepancy)}
                      </div>
                    )}
                  </td>
                )}
                <td className="py-1">
                  <button type="button" title={`Удалить часть ${index + 1}`}
                    onClick={() => { setDrafts(prev => prev.filter(d => d.key !== draft.key)); setDirty(true); }}
                    className="text-fg4 hover:text-danger p-0.5">
                    <Trash2 size={13} />
                  </button>
                </td>
              </tr>
            );
          })}
          <tr className="border-t border-stroke">
            <td colSpan={2} className="py-1.5 text-fg2">
              Не разнесено{dirty && <span className="text-fg4"> (оценка до сохранения)</span>}
            </td>
            <td className={`py-1.5 pr-2 text-right tabular-nums ${remains(rest, byQuantity) ? 'text-warning' : 'text-fg2'}`}>
              {byQuantity ? formatPlain(rest.quantity) : rest.amount === null ? '—' : formatMoney(rest.amount)}
            </td>
            {byQuantity && (
              <td className="py-1.5 pr-2 text-right tabular-nums text-fg2">
                {rest.amount === null ? '—' : formatMoney(rest.amount)}
              </td>
            )}
            <td />
          </tr>
        </tbody>
      </table>

      <div className="mt-2">
        <Button size="sm" variant="outlined" icon={<Plus size={13} />}
          onClick={() => { setDrafts(prev => [...prev, emptyPart()]); setDirty(true); }}>
          Добавить стройку
        </Button>
      </div>
    </Modal>
  );
}

/**
 * «Разнесён» у счёта целиком — рядом с кнопкой «Разобран», потому что это её условие. Называет всё,
 * чего не хватает, теми же словами, что отказ сервера.
 */
export function AllocationSummary({ allocation }: { allocation: AllocationSummaryView }) {
  if (allocation.allocated)
    return (
      <span className="text-xs text-success">
        Разноска сходится
        {allocation.discrepancy ? ` (расхождение ${formatMoney(allocation.discrepancy)} ушло в последнюю часть)` : ''}
      </span>
    );

  const problems: string[] = [];
  if (allocation.unbalanced.length > 0)
    problems.push(allocation.unbalanced.length === 1
      ? `строка ${allocation.unbalanced[0]} разнесена не полностью`
      : `строки ${allocation.unbalanced.join(', ')} разнесены не полностью`);
  if (allocation.lost > 0) problems.push(`частей на удалённую стройку: ${allocation.lost}`);
  if (!allocation.withinTolerance && allocation.discrepancy !== null)
    problems.push(`расхождение с суммой к оплате ${formatMoney(allocation.discrepancy)} больше допуска ${formatMoney(allocation.tolerance)}`);

  return <span className="text-xs text-warning">Разноска: {problems.join('; ')}</span>;
}

function remains(rest: { quantity: number | null; amount: number | null }, byQuantity: boolean): boolean {
  return (byQuantity ? rest.quantity : rest.amount) !== 0;
}

const FIELD = `w-full rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg outline-none
  focus:border-primary disabled:text-fg4`;
