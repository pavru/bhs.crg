import { useState } from 'react';
import { Plus, Save, Trash2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import {
  useReplaceAllocation, type AllocationSummaryView, type InvoiceLineView, type InvoiceView,
} from '@/shared/api/invoices';
import { formatMoney, formatQuantity } from '@/shared/format/format';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import { NumberInput } from '@/shared/ui/NumberInput';
import {
  allocationStatus, emptyPart, estimateRemainder, toPartDrafts, toPartsPayload, type PartDraft,
} from './allocation';
import { PlaceSelect } from './PlaceSelect';
import { usePlaces } from './places';

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
export function LineAllocationCell({ view, line, number, blocked, locked, onOpenChange }: {
  /** Счёт целиком: правка разноски называет его версию (issue #1176). */
  view: InvoiceView;
  line: InvoiceLineView | undefined;
  number: number;
  /** Причина, по которой разносить сейчас нельзя (строки не сохранены), либо `null`. */
  blocked: string | null;
  /** Счёт заперт закрытым периодом: разноска строки открывается только на чтение. */
  locked: boolean;
  /** Диалог открыт или закрыт: пока он открыт, таблица строк под ним не пересобирается. */
  onOpenChange: (open: boolean) => void;
}) {
  // Открытый диалог держит строку, С КОТОРОЙ открыт: сосед удалил её — в виде счёта строки больше
  // нет, а набранные части есть, и пропасть молча они не вправе (ревью PR #1208).
  const [opened, setOpened] = useState<InvoiceLineView | null>(null);
  const { data: access = NO_ACCESS } = useAccess();
  const canEdit = hasPermission(access, 'costs.allocation.edit') && !locked;

  function toggle(next: InvoiceLineView | null) {
    setOpened(next);
    onOpenChange(next !== null);
  }

  const dialog = opened && (
    <LineAllocationDialog view={view} line={line ?? opened} gone={!line} number={number} canEdit={canEdit}
      onClose={() => toggle(null)} />
  );

  if (!line || blocked)
    return (
      <td className="py-1 pr-2 text-fg4" title={blocked ?? 'Строка не сохранена'}>
        {blocked ? 'сохраните строки' : '—'}
        {dialog}
      </td>
    );

  const status = allocationStatus(line.allocation, line.unit);
  const tone = status.tone === 'ok' ? 'text-success' : status.tone === 'warning' ? 'text-warning' : 'text-fg4';

  return (
    <td className="py-1 pr-2">
      <button type="button" onClick={() => toggle(line)} disabled={line.allocation.mode === 'none'
        && line.allocation.parts.length === 0}
        aria-label={`Разноска, строка ${number}`}
        className={`text-left underline decoration-dotted underline-offset-2 disabled:no-underline ${tone}`}>
        {status.text}
      </button>
      {dialog}
    </td>
  );
}

/** Из чего собран черновик частей: строка и её разноска. Строки не стало — подпись своя, ни с чем не совпадёт. */
function partsSignature(line: InvoiceLineView | undefined): string {
  return line === undefined ? 'строки нет'
    : JSON.stringify([line.quantity, line.amount, line.unit, line.allocation.mode, line.allocation.parts]);
}

/**
 * Диалог частей строки. Монтируется только открытым — черновик частей всегда начинается с того, что
 * вернул сервер, и правки прошлого открытия не всплывают в следующем.
 *
 * <p>⚠️ Строка «не разнесено» стоит ВСЕГДА, в том числе при нуле (ТЗ COST-13): исчезающая строка
 * остатка не отличима от забытой.</p>
 */
function LineAllocationDialog({ view, line, gone, number, canEdit, onClose }: {
  view: InvoiceView;
  /** Строка, как её знает счёт, — либо, если её удалили, какой она была при открытии. */
  line: InvoiceLineView;
  /** Строки в счёте больше нет: сохранять некуда, остаётся закрыть. */
  gone: boolean;
  number: number;
  /** Без права разноски диалог — только для чтения: ни полей, ни «Сохранить», а не кнопка с отказом. */
  canEdit: boolean;
  onClose: () => void;
}) {
  const { mode } = line.allocation;
  const [drafts, setDrafts] = useState<PartDraft[]>(() => {
    const saved = toPartDrafts(line.allocation);
    return saved.length > 0 ? saved : [emptyPart()];
  });
  const [dirty, setDirty] = useState(false);

  // Черновик частей собран по строке и её разноске: изменили их — он устарел (issue #1176).
  const savedDrafts = () => {
    const saved = toPartDrafts(line.allocation);
    return saved.length > 0 ? saved : [emptyPart()];
  };
  const base = useDraftBase(view, of => partsSignature(of.lines.find(l => l.id === line.id)), dirty);
  if (base.rebuild && !gone) setDrafts(savedDrafts());

  function reread() {
    // Строки нет — перечитывать нечего: диалог закрывается, таблица под ним покажет счёт как есть.
    if (gone) return onClose();
    setDrafts(savedDrafts());
    setDirty(false);
    base.rebase();
  }

  const places = usePlaces();
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
      await base.save(seen => replace.mutateAsync({
        id: view.id, seen, lineId: line.id, parts: toPartsPayload(drafts, mode),
      }));
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
              ? `В строке ${line.quantity === null ? '' : formatQuantity(line.quantity)} ${unit}`.trim()
                + (line.amount === null ? '' : ` на ${formatMoney(line.amount)}`)
                + '. Делится количество, сумма части считается.'
              : `Количества у строки нет — делится сумма ${formatMoney(line.amount ?? 0)}.`}
          </span>
          <Button variant="text" onClick={onClose}>{canEdit ? 'Отмена' : 'Закрыть'}</Button>
          {canEdit && !gone && (
            <Button variant="filled" icon={<Save size={13} />} loading={replace.isPending} onClick={save}>
              Сохранить разноску
            </Button>
          )}
        </>
      )}>
      {(base.stale || gone) && (
        <div className="mb-3">
          <StaleInvoiceNotice what={gone ? 'строку: её больше нет в счёте' : 'части разноски'} onReread={reread} />
        </div>
      )}
      <table className="w-full text-xs">
        <thead className="text-fg4">
          <tr className="text-left">
            <th className="font-normal py-1">Куда</th>
            <th className="font-normal py-1">Раздел</th>
            <th className="w-28 font-normal py-1 text-right">{byQuantity ? `Кол-во${unit ? `, ${unit}` : ''}` : 'Сумма'}</th>
            {byQuantity && <th className="w-32 font-normal py-1 text-right">Сумма части</th>}
            <th className="w-8 py-1" />
          </tr>
        </thead>
        <tbody>
          {drafts.map((draft, index) => {
            const saved = line.allocation.parts.find(p => p.id === draft.id);
            const site = places.sites?.find(s => s.id === draft.constructionId);
            return (
              <tr key={draft.key} className="border-t border-stroke align-top">
                <td className="py-1 pr-2">
                  {/* Стройка или статья вне строек (F3) — одним выбором двумя группами. */}
                  <PlaceSelect value={{ construction: draft.constructionId || null, section: null, article: draft.articleId || null }}
                    places={places} label={`Куда, часть ${index + 1}`} disabled={!canEdit} className={FIELD}
                    onChange={place => edit(draft.key, {
                      constructionId: place.construction ?? '', articleId: place.article ?? '', sectionId: '',
                    })} />
                </td>
                <td className="py-1 pr-2">
                  {/* Раздел удалён, стройка на месте: значение черновика не совпадёт ни с одним пунктом,
                      и без своего пункта поле показало бы «вся стройка», а уехал бы удалённый раздел. */}
                  <select value={draft.sectionId} aria-label={`Раздел, часть ${index + 1}`}
                    disabled={!canEdit || !site || (site.sections.length === 0 && !draft.sectionId)}
                    onChange={e => edit(draft.key, { sectionId: e.target.value })} className={FIELD}>
                    <option value="">— вся стройка —</option>
                    {site && draft.sectionId && !site.sections.some(s => s.id === draft.sectionId) && (
                      <option value={draft.sectionId}>раздел удалён</option>
                    )}
                    {site?.sections.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                  </select>
                </td>
                <td className="py-1 pr-2">
                  <NumberInput value={draft.value} disabled={!canEdit}
                    label={`${byQuantity ? 'Количество' : 'Сумма'}, часть ${index + 1}`}
                    onChange={value => edit(draft.key, { value })} />
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
                  {canEdit && <button type="button" title={`Удалить часть ${index + 1}`}
                    onClick={() => { setDrafts(prev => prev.filter(d => d.key !== draft.key)); setDirty(true); }}
                    className="text-fg4 hover:text-danger p-0.5">
                    <Trash2 size={13} />
                  </button>}
                </td>
              </tr>
            );
          })}
          <tr className="border-t border-stroke">
            <td colSpan={2} className="py-1.5 text-fg2">
              Не разнесено{dirty && <span className="text-fg4"> (оценка до сохранения)</span>}
            </td>
            <td className={`py-1.5 pr-2 text-right tabular-nums ${remains(rest, byQuantity) ? 'text-warning' : 'text-fg2'}`}>
              {byQuantity ? (rest.quantity === null ? '' : formatQuantity(rest.quantity)) : rest.amount === null ? '—' : formatMoney(rest.amount)}
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

      {canEdit && <div className="mt-2">
        <Button size="sm" variant="outlined" icon={<Plus size={13} />}
          onClick={() => { setDrafts(prev => [...prev, emptyPart()]); setDirty(true); }}>
          Добавить часть
        </Button>
      </div>}
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
  if (allocation.document.pending)
    problems.push('разноска суммой, сделанная до строк, ждёт пересчёта по строкам («Объект» → матрица)');
  if (allocation.lost > 0) problems.push(`частей на удалённую стройку: ${allocation.lost}`);
  if (!allocation.withinTolerance && allocation.discrepancy !== null)
    problems.push(`расхождение с суммой к оплате ${formatMoney(allocation.discrepancy)} больше допуска ${formatMoney(allocation.tolerance)}`);

  return <span className="text-xs text-warning">Разноска: {problems.join('; ')}</span>;
}

function remains(rest: { quantity: number | null; amount: number | null }, byQuantity: boolean): boolean {
  return (byQuantity ? rest.quantity : rest.amount) !== 0;
}

const FIELD = `w-full rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg outline-none
  focus:border-primary disabled:bg-surface2 disabled:text-fg3`;
