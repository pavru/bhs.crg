import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { useListDataSetFiles } from '@/shared/api/datasets';
import type { DataSetSource } from '@/shared/api/types';
import {
  useCreateReconciliation, useUpdateReconciliation,
  OPERATOR_LABELS,
  type ComparisonOperator, type Reconciliation, type ReconciliationSide, type ToleranceKind,
} from '@/shared/api/reconciliations';

/**
 * Редактор определения сверки (issue #436, Admin).
 *
 * Колонки выбираются из схемы источника, а не вводятся руками: опечатка в имени колонки даёт пустую
 * сторону и находки «нет слева» по всему списку — молчаливая неверность, которую пользователь примет
 * за реальное расхождение.
 */

const EMPTY_SIDE: ReconciliationSide = { sourceId: '', keyColumns: [], valueColumn: '' };

function columnsOf(source: DataSetSource | undefined): string[] {
  if (!source?.cachedSchema) return [];
  try {
    const parsed: unknown = JSON.parse(source.cachedSchema);
    return Array.isArray(parsed)
      ? parsed.map(c => (c as { name?: string }).name).filter((n): n is string => !!n)
      : [];
  } catch {
    return [];
  }
}

function SideEditor({ title, side, onChange, sources }: {
  title: string;
  side: ReconciliationSide;
  onChange: (s: ReconciliationSide) => void;
  sources: { source: DataSetSource; fileName: string }[];
}) {
  const current = sources.find(s => s.source.id === side.sourceId)?.source;
  const columns = columnsOf(current);

  function toggleKey(col: string) {
    // Порядок значим: стороны обязаны перечислять ключевые колонки согласованно, иначе ключи не сойдутся.
    onChange({
      ...side,
      keyColumns: side.keyColumns.includes(col)
        ? side.keyColumns.filter(c => c !== col)
        : [...side.keyColumns, col],
    });
  }

  return (
    <div className="space-y-3 rounded-lg border border-stroke p-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-fg4">{title}</div>

      <Select value={side.sourceId} onValueChange={v => onChange({ ...EMPTY_SIDE, sourceId: v })}
        placeholder="Источник данных" aria-label={`${title}: источник`}>
        {sources.map(({ source, fileName }) => (
          <SelectItem key={source.id} value={source.id}>
            {fileName} — {source.name}
          </SelectItem>
        ))}
      </Select>

      {side.sourceId && columns.length === 0 && (
        <p className="text-xs text-danger">У источника нет разобранной схемы — колонки не выбрать.</p>
      )}

      {columns.length > 0 && (
        <>
          <div>
            <div className="text-xs text-fg3 mb-1">
              Ключевые колонки {side.keyColumns.length > 0 && (
                <span className="text-fg4">— порядок: {side.keyColumns.join(' + ')}</span>
              )}
            </div>
            <div className="flex flex-wrap gap-1">
              {columns.map(c => {
                const on = side.keyColumns.includes(c);
                return (
                  <button key={c} type="button" onClick={() => toggleKey(c)}
                    className={`text-xs px-2 py-1 rounded-full transition-colors ${
                      on ? 'bg-brand-subtle text-brand font-medium' : 'bg-muted text-fg3 hover:text-fg1'}`}>
                    {c}
                  </button>
                );
              })}
            </div>
          </div>

          <div>
            <div className="text-xs text-fg3 mb-1">Колонка количества (строки с одним ключом суммируются)</div>
            <Select value={side.valueColumn} onValueChange={v => onChange({ ...side, valueColumn: v })}
              placeholder="Выберите колонку" aria-label={`${title}: количество`}>
              {columns.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}
            </Select>
          </div>
        </>
      )}
    </div>
  );
}

export function ReconciliationEditor({ existing, onClose }: {
  existing: Reconciliation | null;
  onClose: () => void;
}) {
  const toast = useToast();
  const create = useCreateReconciliation();
  const update = useUpdateReconciliation();

  const [name, setName] = useState(existing?.name ?? '');
  const [left, setLeft] = useState<ReconciliationSide>(existing?.spec.left ?? EMPTY_SIDE);
  const [right, setRight] = useState<ReconciliationSide>(existing?.spec.right ?? EMPTY_SIDE);
  const [operator, setOperator] = useState<ComparisonOperator>(
    existing?.spec.comparison.operator ?? 'GreaterOrEqual');
  const [tolerance, setTolerance] = useState(String(existing?.spec.comparison.tolerance ?? 0));
  const [toleranceKind, setToleranceKind] = useState<ToleranceKind>(
    existing?.spec.comparison.toleranceKind ?? 'Absolute');
  const [busy, setBusy] = useState(false);

  const { data: files = [] } = useListDataSetFiles('System');

  const sources = useMemo(
    () => files.flatMap(f => (f.sources ?? []).map(s => ({ source: s, fileName: f.name }))),
    [files]);

  const valid = name.trim() !== ''
    && left.sourceId && left.valueColumn && left.keyColumns.length > 0
    && right.sourceId && right.valueColumn && right.keyColumns.length > 0;

  async function save() {
    const spec = {
      left, right,
      comparison: { operator, tolerance: Number(tolerance) || 0, toleranceKind },
    };
    setBusy(true);
    try {
      if (existing) await update.mutateAsync({ id: existing.id, name: name.trim(), spec });
      else await create.mutateAsync({ name: name.trim(), scope: 'System', spec });
      toast.success(existing ? 'Сверка изменена' : 'Сверка создана');
      onClose();
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} wide
      title={existing ? 'Изменить сверку' : 'Новая сверка'}
      footer={
        <div className="flex items-center gap-2 justify-end">
          <Button disabled={busy} onClick={onClose}>Отмена</Button>
          <Button variant="filled" disabled={!valid} loading={busy} onClick={save}>Сохранить</Button>
        </div>
      }>
      <div className="space-y-4">
        <TextField label="Название" value={name} onChange={e => setName(e.target.value)}
          hint="Например: Кабель — проложено против реестра материалов" />

        <div className="grid grid-cols-2 gap-3">
          <SideEditor title="Слева" side={left} onChange={setLeft} sources={sources} />
          <SideEditor title="Справа" side={right} onChange={setRight} sources={sources} />
        </div>

        <div className="rounded-lg border border-stroke p-3 space-y-3">
          <div className="text-xs font-semibold uppercase tracking-wide text-fg4">Правило</div>
          <div className="flex items-center gap-2 text-sm text-fg2">
            <span>Слева должно быть</span>
            <Select value={operator} onValueChange={v => setOperator(v as ComparisonOperator)}
              aria-label="Оператор" className="w-40">
              {(Object.keys(OPERATOR_LABELS) as ComparisonOperator[]).map(op => (
                <SelectItem key={op} value={op}>{OPERATOR_LABELS[op]}</SelectItem>
              ))}
            </Select>
            <span>чем справа</span>
          </div>
          <div className="flex items-end gap-2">
            <TextField label="Допуск" value={tolerance} inputMode="decimal"
              onChange={e => setTolerance(e.target.value)} containerClassName="w-32"
              hint="0 — точное сравнение" />
            <Select value={toleranceKind} onValueChange={v => setToleranceKind(v as ToleranceKind)}
              aria-label="Вид допуска" className="w-40">
              <SelectItem value="Absolute">в единицах</SelectItem>
              <SelectItem value="Percent">в процентах</SelectItem>
            </Select>
          </div>
          <p className="text-[11px] text-fg4">
            Допуск гасит расхождения округления — без него отчёт забьётся находками на сотые доли.
          </p>
        </div>
      </div>
    </Modal>
  );
}
