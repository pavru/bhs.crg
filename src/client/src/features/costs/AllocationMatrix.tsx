import { useMemo, useState } from 'react';
import { Calculator, Check, Divide, Percent, Plus, Save, Trash2, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import type { InvoiceView } from '@/shared/api/invoices';
import type { MatrixState } from '@/shared/api/allocationMatrix';
import {
  usePreviewAllocation, useReplaceMatrix, type AllocationPreview, type SplitMethod,
} from '@/shared/api/allocationMatrix';
import { formatMoney, formatQuantity } from '@/shared/format/format';
import { NumberInput } from '@/shared/ui/NumberInput';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { useDraftBase, type DraftBase } from './draftBase';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import {
  allocationsOf, cellText, cellsFromState, cellsOf, draftRows, estimateRest, matrixSignature, newTarget, partAt, restText,
  rowsOf, targetName, targetsOf, targetsOfState, toSplitTargets, toState, type MatrixCells, type MatrixRow, type MatrixTarget,
} from './matrix';
import { toDrafts, type LineDraft } from './invoiceLines';
import { PlaceSelect } from './PlaceSelect';
import { chosen, missingSection, placeOfPart, samePlace, usePlaces, type Places } from './places';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

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
 *
 * <p><b>Черновой режим</b> (`draft`, исправление запертого счёта — E2, issue #1100, PR 6b-1): матрица ничего
 * не записывает, «Готово» отдаёт набор черновику исправления — он запишется одним телом с шапкой и
 * строками. Строки матрицы — строки ЧЕРНОВИКА, и новую строку можно разнести, не записывая её отдельно.
 * «Поровну» и «по %» считает сервер по ЗАПИСАННЫМ строкам — поэтому только пока строки не тронуты.</p>
 */
export function AllocationMatrix({ view, total, canEdit, initialPreview, draft, onClose }: {
  view: InvoiceView;
  /** Сумма к оплате — ею разносится счёт без строк. */
  total: number | null;
  canEdit: boolean;
  /**
   * Предпросмотр, с которым матрица открывается (выбор объекта в шапке поверх прежней разноски).
   * `base` — вид, по которому он СПРОШЕН: пока шёл запрос, вид мог смениться, и основой матрицы
   * обязан быть тот, а не пришедший к монтированию.
   */
  initialPreview?: { preview: AllocationPreview; targets: MatrixTarget[]; stamp: string; base: DraftBase };
  /** Черновой режим исправления: строки черновика (`null` — не тронуты), набранная разноска и куда её отдать. */
  draft?: { lines: LineDraft[] | null; state: MatrixState | null; onApply: (state: MatrixState) => void };
  onClose: () => void;
}) {
  const places = usePlaces();
  const previewing = usePreviewAllocation();
  const replace = useReplaceMatrix();
  const toast = useToast();

  const drafting = draft !== undefined;
  const draftLines = draft?.lines ?? null;
  const rowsFor = (of: InvoiceView) => (drafting ? draftRows(draftLines ?? toDrafts(of.lines), total) : rowsOf(of, total));
  const rows = useMemo(() => (drafting ? draftRows(draftLines ?? toDrafts(view.lines), total) : rowsOf(view, total)),
    [drafting, draftLines, view, total]);
  // Набор, уже лежащий в черновике исправления: матрица открывается им, а не записанной разноской.
  const [opened] = useState(() => (draft?.state ? { state: draft.state, targets: targetsOfState(draft.state) } : null));
  const [targets, setTargets] = useState<MatrixTarget[]>(() => {
    if (initialPreview) return initialPreview.targets;
    if (opened && opened.targets.length > 0) return opened.targets;
    const known = targetsOf(view);
    return known.length > 0 ? known : [newTarget()];
  });
  const [preview, setPreview] = useState<AllocationPreview | null>(initialPreview?.preview ?? null);
  const [cells, setCells] = useState<MatrixCells>(() => initialPreview
    ? cellsFromState(rows, initialPreview.targets, initialPreview.preview.apply)
    : opened ? cellsFromState(rows, opened.targets, opened.state)
    : cellsOf(rows, targets, allocationsOf(view, null)));
  // Набор черновика — уже правка: под клетками показывается оценка, а не записанные числа.
  const [dirty, setDirty] = useState(opened !== null);
  // Версия разноски, с которой матрица открыта: запись пошлёт её, и набор по устаревшему виду откажет.
  const [stamp, setStamp] = useState(() => initialPreview?.stamp ?? view.allocation.stamp);

  // Матрица собрана по строкам счёта и его разноске (issue #1176). Отметка разноски (`stamp`) стережёт
  // только части: строку, которой сосед сменил количество, она не видит — её видит версия счёта.
  const touched = dirty || preview !== null;
  const base = useDraftBase(view, matrixSignature, touched, { initial: initialPreview?.base });
  if (base.rebuild) reset(view);

  const allocations = allocationsOf(view, preview);
  const editable = canEdit && preview === null;
  // Сервер раскладывает записанные строки: по тронутым черновым его ответ был бы про другой счёт.
  const splitOff = draftLines !== null;
  const pending = view.allocation.document.pending;

  function reset(next: InvoiceView) {
    const known = targetsOf(next);
    const columns = known.length > 0 ? known : [newTarget()];
    setTargets(columns);
    setCells(cellsOf(rowsFor(next), columns, allocationsOf(next, null)));
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
    if (draft) {
      draft.onApply(preview ? preview.apply : toState(rows, targets, cells));
      onClose();
      return;
    }
    try {
      const state = { ...(preview ? preview.apply : toState(rows, targets, cells)), stamp };
      reset(await base.save(seen => replace.mutateAsync({ id: view.id, seen, state })));
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
      isDirty={dirty || preview !== null} busy={replace.isPending}
      footer={(
        <>
          <span className="text-xs text-fg4 mr-auto">
            {drafting
              ? 'Разноска войдёт в исправление: запишется вместе с ним, после проверки. «Не разнесено» — оценка.'
              : preview
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
              <ModalExit>Закрыть</ModalExit>
              {canEdit && (
                <Button variant="filled" icon={drafting ? <Check size={13} /> : <Save size={13} />} disabled={!dirty}
                  loading={replace.isPending} onClick={save}>
                  {drafting ? 'Готово' : 'Сохранить разноску'}
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
            <Button size="sm" variant="outlined" icon={<Divide size={13} />} disabled={!editable || splitOff}
              loading={previewing.isPending} onClick={() => split('equal')}>
              Поровну
            </Button>
            <Button size="sm" variant="outlined" icon={<Percent size={13} />} disabled={!editable || splitOff}
              loading={previewing.isPending} onClick={() => split('percent')}>
              По %
            </Button>
            {pending && (
              <Button size="sm" variant="outlined" icon={<Calculator size={13} />} disabled={!editable || splitOff}
                loading={previewing.isPending} onClick={() => split('document')}>
                Пересчитать по строкам
              </Button>
            )}
          </div>
        )}

        {canEdit && splitOff && (
          <p className="text-xs text-fg4">
            «Поровну» и «по %» считает сервер по записанным строкам, а строки в исправлении изменены — клетки
            набирайте руками.
          </p>
        )}
        {pending && <PendingNote view={view} places={places} />}

        {/* Закреплены первая колонка (строка) и последняя («не разнесено»): при восьми объектах
            остаток обязан остаться на виду после прокрутки вбок — ради него матрица и открыта. */}
        <EditGrid label="Матрица разноски" className="max-h-[65vh]" rows={rows} rowKey={row => row.key}
          columns={[
            {
              key: 'row', label: 'Строка', rowHeader: true, pin: 'start',
              cell: row => (
                <>
                  <div className="text-fg1 max-w-64 truncate" title={row.title}>{row.title}</div>
                  <div className="text-fg4">
                    {row.mode === 'quantity'
                      ? `${row.whole === null ? '' : formatQuantity(row.whole)} ${row.unit ?? ''}`.trim()
                        + (row.amount === null ? '' : ` · ${formatMoney(row.amount)}`)
                      : row.whole === null ? 'суммы нет' : formatMoney(row.whole)}
                  </div>
                </>
              ),
            },
            ...targets.map((target, index): EditGridColumn<MatrixRow> => ({
              key: target.key, label: targetName(target, places), width: 'min-w-44',
              header: editable
                ? <TargetHeader target={target} number={index + 1} places={places}
                    onChange={patch => retarget(target.key, patch)} onRemove={() => remove(target.key)} />
                : <span className="text-fg2">{targetName(target, places)}</span>,
              cell: row => {
                const allocation = allocations[row.key];
                return (
                  <div className="tabular-nums">
                    {editable && row.mode !== 'none' && (
                      <NumberInput value={cells[row.key]?.[target.key] ?? ''} placeholder="0"
                        label={`${row.title}, ${targetName(target, places)}`}
                        onChange={value => edit(row.key, target.key, value)} />
                    )}
                    {partAt(allocation, target)?.mismatched && (
                      <Tooltip content="У строки сменился вид (появилось или пропало количество): такая часть не разносит ничего">
                        <div className="text-right text-warning">
                          часть не того вида — уйдёт при сохранении
                        </div>
                      </Tooltip>
                    )}
                    {!dirty && (
                      <div className="text-right text-fg2">
                        {cellText(row, partAt(allocation, target))}
                        {remainder(row, target) && (
                          <Tooltip content="Сюда ушёл остаток округления — так части складываются ровно в строку">
                            <span className="ml-1 text-warning">◆</span>
                          </Tooltip>
                        )}
                      </div>
                    )}
                  </div>
                );
              },
            })),
            {
              key: 'rest', label: 'Не разнесено', align: 'right', pin: 'end',
              cell: row => {
                const rest = dirty && !preview ? estimateRest(row, targets, cells) : null;
                return rest !== null
                  ? <span className="text-fg4">≈ {row.mode === 'quantity' ? `${formatQuantity(rest)} ${row.unit ?? ''}`.trim() : formatMoney(rest)}</span>
                  : restText(row, allocations[row.key]);
              },
            },
          ]} />
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
          onChange={onChange} />
        <Tooltip content={`Убрать объект ${number}`} name>
          <button type="button" onClick={onRemove}
            className="text-fg4 hover:text-danger p-0.5">
            <Trash2 size={13} />
          </button>
        </Tooltip>
      </div>
      <div className="flex items-center gap-1">
        <Select size="sm" value={target.section ?? ''} aria-label={`Раздел, объект ${number}`}
          disabled={!site || (site.sections.length === 0 && !target.section)}
          onValueChange={section => onChange({ section: section || null })} emptyItem="— вся стройка —">
          {site && target.section && !site.sections.some(s => s.id === target.section) && (
            <SelectItem value={target.section}>{missingSection(target.section, places)}</SelectItem>
          )}
          {site?.sections.map(s => <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>)}
        </Select>
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
