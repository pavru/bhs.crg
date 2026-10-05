import { useMemo, useState } from 'react';
import { Calculator, Check, Divide, Percent, Plus, Save, Trash2, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { useFreshInvoice, type InvoiceView } from '@/shared/api/invoices';
import {
  usePreviewAllocation, useReplaceMatrix, type AllocationPreview, type SplitMethod,
} from '@/shared/api/allocationMatrix';
import { formatMoney, formatQuantity } from '@/shared/format/format';
import { NumberInput } from '@/shared/ui/NumberInput';
import { useDraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import {
  allocationsOf, cellText, cellsFromState, cellsOf, estimateRest, newTarget, partAt, restText, rowsOf,
  targetName, targetsOf, toSplitTargets, toState, type MatrixCells, type MatrixRow, type MatrixTarget,
} from './matrix';
import { PlaceSelect } from './PlaceSelect';
import { chosen, placeOfPart, samePlace, usePlaces, type Places } from './places';

/**
 * Матрица разноски «строки × объекты» (задача F2, issue #1086, ТЗ COST-6.2, COST-11, COST-12).
 *
 * <p><b>Раскладку «поровну» и «по %» считает сервер</b>: кнопка шлёт запрос предпросмотра, матрица рисует
 * ответ и подписывает его «ещё не записано»; «Применить» записывает ровно показанное. Числа до и после
 * рисует одна функция ({@link cellText}) из одного вида — поэтому они совпадают посимвольно.</p>
 *
 * <p>⚠️ Закреплены первая колонка (строка) и последняя («не разнесено»): при восьми объектах остаток
 * обязан остаться на виду после горизонтальной прокрутки — ради него матрица и открыта.</p>
 *
 * <p>Без права разноски матрица открывается ТОЛЬКО для чтения: ни полей, ни кнопок «поровну» и «по %» —
 * а не кнопки, которые нажимаются и получают отказ.</p>
 */
export function AllocationMatrix({ view, total, canEdit, initialPreview, onClose }: {
  view: InvoiceView;
  /** Сумма к оплате — ею разносится счёт без строк. */
  total: number | null;
  canEdit: boolean;
  /** Предпросмотр, с которым матрица открывается (выбор объекта в шапке поверх прежней разноски). */
  initialPreview?: { preview: AllocationPreview; targets: MatrixTarget[]; stamp: string };
  onClose: () => void;
}) {
  const places = usePlaces();
  const previewing = usePreviewAllocation();
  const replace = useReplaceMatrix();
  const toast = useToast();

  const rows = useMemo(() => rowsOf(view, total), [view, total]);
  const [targets, setTargets] = useState<MatrixTarget[]>(() => {
    if (initialPreview) return initialPreview.targets;
    const known = targetsOf(view);
    return known.length > 0 ? known : [newTarget()];
  });
  const [preview, setPreview] = useState<AllocationPreview | null>(initialPreview?.preview ?? null);
  const [cells, setCells] = useState<MatrixCells>(() => initialPreview
    ? cellsFromState(rows, initialPreview.targets, initialPreview.preview.apply)
    : cellsOf(rows, targets, allocationsOf(view, null)));
  const [dirty, setDirty] = useState(false);
  // Версия разноски, с которой матрица открыта: запись пошлёт её, и набор по устаревшему виду откажет.
  const [stamp, setStamp] = useState(() => initialPreview?.stamp ?? view.allocation.stamp);

  // Матрица собрана по строкам счёта и его разноске (issue #1176). Отметка разноски (`stamp`) стережёт
  // только части: строку, которой сосед сменил количество, она не видит — её видит версия счёта.
  const touched = dirty || preview !== null;
  const base = useDraftBase(view.version, matrixSignature(view), touched);
  const readFresh = useFreshInvoice();
  if (base.rebuild) reset(view);

  const allocations = allocationsOf(view, preview);
  const editable = canEdit && preview === null;
  const pending = view.allocation.document.pending;

  function reset(next: InvoiceView) {
    const known = targetsOf(next);
    const columns = known.length > 0 ? known : [newTarget()];
    setTargets(columns);
    setCells(cellsOf(rowsOf(next, total), columns, allocationsOf(next, null)));
    setPreview(null);
    setDirty(false);
    setStamp(next.allocation.stamp);
  }

  async function split(method: SplitMethod) {
    try {
      const result = await previewing.mutateAsync({
        id: view.id, method, targets: method === 'document' ? [] : toSplitTargets(targets, method === 'percent'),
      });
      // Пересчёт приносит цели прежней разноски суммой — им нужны колонки.
      const columns = [...targets];
      for (const part of [...result.apply.document, ...result.apply.lines.flatMap(l => l.parts)])
        if (!columns.some(t => samePlace(t, part)))
          columns.push(newTarget({ construction: part.construction, section: part.section, article: part.article }));
      setTargets(columns.filter(chosen));
      setCells(cellsFromState(rows, columns, result.apply));
      setPreview(result);
      setDirty(false);
    } catch (e) {
      toast.apiError(e, 'Раскладка не посчитана');
    }
  }

  async function save() {
    try {
      const state = { ...(preview ? preview.apply : toState(rows, targets, cells)), stamp };
      const fresh = await readFresh(view.id);
      const seen = base.seenAgainst(fresh.version, matrixSignature(fresh));
      reset(await replace.mutateAsync({ id: view.id, seen, state }));
    } catch (e) {
      toast.apiError(e, 'Разноска не записана');
    }
  }

  function edit(row: string, column: string, value: string) {
    setCells(prev => ({ ...prev, [row]: { ...prev[row], [column]: value } }));
    setDirty(true);
  }

  function retarget(column: string, patch: Partial<MatrixTarget>) {
    setTargets(prev => prev.map(t => (t.key === column ? { ...t, ...patch } : t)));
    setDirty(true);
  }

  function remove(column: string) {
    setTargets(prev => prev.filter(t => t.key !== column));
    setCells(prev => Object.fromEntries(Object.entries(prev).map(([row, byColumn]) => {
      const { [column]: _dropped, ...rest } = byColumn;
      return [row, rest];
    })));
    setDirty(true);
  }

  const remainder = (row: MatrixRow, target: MatrixTarget) => preview?.remainders.some(r =>
    r.line === row.lineId && samePlace(r, target)) ?? false;

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Разноска по объектам" fullScreen
      isDirty={dirty || preview !== null}
      footer={(
        <>
          <span className="text-xs text-fg4 mr-auto">
            {preview
              ? 'Предпросмотр — ещё не записано. Числа посчитал сервер; «Применить» запишет ровно их.'
              : dirty
                ? '«Не разнесено» — оценка до записи: копейки окончательно считает сервер.'
                : 'Строка «не разнесено» закреплена справа и видна при любом числе объектов.'}
          </span>
          {preview ? (
            <>
              <Button variant="text" icon={<X size={13} />} onClick={() => reset(view)}>Отменить предпросмотр</Button>
              <Button variant="filled" icon={<Check size={13} />} loading={replace.isPending} onClick={save}>
                Применить
              </Button>
            </>
          ) : (
            <>
              <Button variant="text" onClick={onClose}>Закрыть</Button>
              {canEdit && (
                <Button variant="filled" icon={<Save size={13} />} disabled={!dirty} loading={replace.isPending}
                  onClick={save}>
                  Сохранить разноску
                </Button>
              )}
            </>
          )}
        </>
      )}>
      <div className="space-y-3">
        {base.stale && (
          <StaleInvoiceNotice what="клетки матрицы" onReread={() => { reset(view); base.rebase(); }} />
        )}
        {canEdit && (
          <div className="flex items-center gap-2 flex-wrap">
            <Button size="sm" variant="outlined" icon={<Plus size={13} />} disabled={!editable}
              onClick={() => { setTargets(prev => [...prev, newTarget()]); setDirty(true); }}>
              Добавить объект
            </Button>
            <Button size="sm" variant="outlined" icon={<Divide size={13} />} disabled={!editable}
              loading={previewing.isPending} onClick={() => split('equal')}>
              Поровну
            </Button>
            <Button size="sm" variant="outlined" icon={<Percent size={13} />} disabled={!editable}
              loading={previewing.isPending} onClick={() => split('percent')}>
              По %
            </Button>
            {pending && (
              <Button size="sm" variant="outlined" icon={<Calculator size={13} />} disabled={!editable}
                loading={previewing.isPending} onClick={() => split('document')}>
                Пересчитать по строкам
              </Button>
            )}
          </div>
        )}

        {pending && <PendingNote view={view} places={places} />}

        <div className="overflow-auto max-h-[65vh] border border-stroke rounded">
          <table className="text-xs border-separate border-spacing-0">
            <thead>
              <tr className="text-left text-fg4 align-bottom">
                <th className={`${STICKY_LEFT} font-normal`}>Строка</th>
                {targets.map((target, index) => (
                  <th key={target.key} className="border-b border-stroke px-2 py-1.5 font-normal min-w-44">
                    {editable
                      ? <TargetHeader target={target} number={index + 1} places={places}
                          onChange={patch => retarget(target.key, patch)} onRemove={() => remove(target.key)} />
                      : <span className="text-fg2">{targetName(target, places)}</span>}
                  </th>
                ))}
                <th className={`${STICKY_RIGHT} font-normal`}>Не разнесено</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(row => {
                const allocation = allocations[row.key];
                const rest = dirty && !preview ? estimateRest(row, targets, cells) : null;
                return (
                  <tr key={row.key} className="align-top">
                    <th className={`${STICKY_LEFT} font-normal text-left`}>
                      <div className="text-fg max-w-64 truncate" title={row.title}>{row.title}</div>
                      <div className="text-fg4">
                        {row.mode === 'quantity'
                          ? `${row.whole === null ? '' : formatQuantity(row.whole)} ${row.unit ?? ''}`.trim()
                            + (row.amount === null ? '' : ` · ${formatMoney(row.amount)}`)
                          : row.whole === null ? 'суммы нет' : formatMoney(row.whole)}
                      </div>
                    </th>
                    {targets.map(target => (
                      <td key={target.key} className="border-b border-stroke px-2 py-1 tabular-nums">
                        {editable && row.mode !== 'none' && (
                          <NumberInput value={cells[row.key]?.[target.key] ?? ''} placeholder="0"
                            label={`${row.title}, ${targetName(target, places)}`}
                            onChange={value => edit(row.key, target.key, value)} />
                        )}
                        {partAt(allocation, target)?.mismatched && (
                          <div className="text-right text-warning"
                            title="У строки сменился вид (появилось или пропало количество): такая часть не разносит ничего">
                            часть не того вида — уйдёт при сохранении
                          </div>
                        )}
                        {!dirty && (
                          <div className="text-right text-fg2">
                            {cellText(row, partAt(allocation, target))}
                            {remainder(row, target) && (
                              <span className="ml-1 text-warning"
                                title="Сюда ушёл остаток округления — так части складываются ровно в строку">◆</span>
                            )}
                          </div>
                        )}
                      </td>
                    ))}
                    <td className={`${STICKY_RIGHT} tabular-nums text-right`}>
                      {rest !== null
                        ? <span className="text-fg4">≈ {row.mode === 'quantity' ? `${formatQuantity(rest)} ${row.unit ?? ''}`.trim() : formatMoney(rest)}</span>
                        : restText(row, allocation)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </Modal>
  );
}

/** Заголовок колонки: стройка, раздел, процент для «по %» и удаление колонки. */
function TargetHeader({ target, number, places, onChange, onRemove }: {
  target: MatrixTarget;
  number: number;
  places: Places;
  onChange: (patch: Partial<MatrixTarget>) => void;
  onRemove: () => void;
}) {
  const site = places.sites?.find(s => s.id === target.construction);
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-1">
        <PlaceSelect value={target} places={places} label={`Объект ${number}`} placeholder="— объект —"
          className={FIELD} onChange={onChange} />
        <button type="button" title={`Убрать объект ${number}`} onClick={onRemove}
          className="text-fg4 hover:text-danger p-0.5">
          <Trash2 size={13} />
        </button>
      </div>
      <div className="flex items-center gap-1">
        <select value={target.section ?? ''} aria-label={`Раздел, объект ${number}`} className={FIELD}
          disabled={!site || (site.sections.length === 0 && !target.section)}
          onChange={e => onChange({ section: e.target.value || null })}>
          <option value="">— вся стройка —</option>
          {site && target.section && !site.sections.some(s => s.id === target.section) && (
            <option value={target.section}>раздел удалён</option>
          )}
          {site?.sections.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
        <NumberInput value={target.percent} unit="%" label={`Процент, объект ${number}`}
          onChange={percent => onChange({ percent })} className="w-16 shrink-0" />
      </div>
    </div>
  );
}

/** Разноска суммой, сделанная до строк, — видна, пока ждёт пересчёта (ТЗ COST-11). */
function PendingNote({ view, places }: { view: InvoiceView; places: Places }) {
  const parts = view.allocation.document.parts;
  return (
    <p className="text-xs text-warning">
      Счёт разнесён суммой, пока строк не было:{' '}
      {parts.map(p => `${targetName(placeOfPart(p), places)} —${formatMoney(p.amount ?? 0)}`).join('; ')}.
      Строки появились — пересчитайте разноску по ним: до пересчёта счёт не разнесён.
    </p>
  );
}

const STICKY_LEFT = 'sticky left-0 z-10 bg-surface border-b border-r border-stroke px-2 py-1.5';
/** Из чего собрана матрица: строки счёта и отметка его разноски. */
function matrixSignature(view: InvoiceView): string {
  return JSON.stringify([view.allocation.stamp, view.lines]);
}

const STICKY_RIGHT = 'sticky right-0 z-10 bg-surface border-b border-l border-stroke px-2 py-1.5';

const FIELD = `w-full rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg outline-none
  focus:border-primary disabled:bg-surface2 disabled:text-fg3`;
