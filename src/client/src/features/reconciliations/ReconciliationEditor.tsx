import { useMemo, useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { useListDataSetFiles } from '@/shared/api/datasets';
import type { DataSetSource } from '@/shared/api/types';
import { Plus, Trash2 } from 'lucide-react';
import {
  useCreateReconciliation, useUpdateReconciliation,
  OPERATOR_LABELS, sidePartsOf,
  type ComparisonOperator, type Reconciliation, type ReconciliationSide, type SideSource,
  type ToleranceKind,
} from '@/shared/api/reconciliations';

/**
 * Редактор определения сверки (issue #436, Admin).
 *
 * Колонки выбираются из схемы источника, а не вводятся руками: опечатка в имени колонки даёт пустую
 * сторону и находки «нет слева» по всему списку — молчаливая неверность, которую пользователь примет
 * за реальное расхождение.
 */

const EMPTY_PART: SideSource = { sourceId: '', keyColumns: [], valueColumn: '' };

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

function PartEditor({ part, onChange, onRemove, sources, index }: {
  part: SideSource;
  onChange: (p: SideSource) => void;
  onRemove?: () => void;
  sources: { source: DataSetSource; fileName: string }[];
  index: number;
}) {
  const current = sources.find(s => s.source.id === part.sourceId)?.source;
  const columns = columnsOf(current);

  function toggleKey(col: string) {
    // Порядок значим: стороны обязаны перечислять ключевые колонки согласованно, иначе ключи не сойдутся.
    onChange({
      ...part,
      keyColumns: part.keyColumns.includes(col)
        ? part.keyColumns.filter(c => c !== col)
        : [...part.keyColumns, col],
    });
  }

  return (
    <div className="space-y-2 rounded-md bg-muted/40 p-2">
      <div className="flex items-center gap-2">
        <Select label={`Источник ${index + 1}`} value={part.sourceId}
          onValueChange={v => onChange({ ...EMPTY_PART, sourceId: v })}
          placeholder="— выберите —" containerClassName="flex-1">
          {sources.map(({ source, fileName }) => (
            <SelectItem key={source.id} value={source.id}>{fileName} — {source.name}</SelectItem>
          ))}
        </Select>
        {onRemove && (
          <Button variant="text" size="sm" danger onClick={onRemove} aria-label="Убрать источник">
            <Trash2 size={14} />
          </Button>
        )}
      </div>

      {part.sourceId && columns.length === 0 && (
        <p className="text-xs text-danger">У источника нет разобранной схемы — колонки не выбрать.</p>
      )}

      {columns.length > 0 && (
        <>
          <div>
            <div className="text-xs text-fg3 mb-1">
              Ключевые колонки {part.keyColumns.length > 0 && (
                <span className="text-fg4">— порядок: {part.keyColumns.join(' + ')}</span>
              )}
            </div>
            <div className="flex flex-wrap gap-1">
              {columns.map(c => {
                const on = part.keyColumns.includes(c);
                return (
                  <button key={c} type="button" onClick={() => toggleKey(c)}
                    className={`text-xs px-2 py-1 rounded-full transition-colors ${
                      on ? 'bg-brand-subtle text-brand font-medium' : 'bg-base text-fg3 hover:text-fg1'}`}>
                    {c}
                  </button>
                );
              })}
            </div>
          </div>

          <Select label="Колонка количества" value={part.valueColumn}
            onValueChange={v => onChange({ ...part, valueColumn: v })} placeholder="— выберите —">
            {columns.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}
          </Select>
        </>
      )}
    </div>
  );
}

function SideEditor({ title, parts, onChange, sources }: {
  title: string;
  parts: SideSource[];
  onChange: (p: SideSource[]) => void;
  sources: { source: DataSetSource; fileName: string }[];
}) {
  return (
    <div className="space-y-2 rounded-lg border border-stroke p-3">
      <div className="flex items-center justify-between">
        <span className="text-xs font-semibold uppercase tracking-wide text-fg4">{title}</span>
        <Button variant="text" size="sm" onClick={() => onChange([...parts, EMPTY_PART])}>
          <Plus size={14} /> Источник
        </Button>
      </div>

      {parts.map((part, i) => (
        <PartEditor key={i} part={part} index={i} sources={sources}
          onChange={p => onChange(parts.map((x, xi) => xi === i ? p : x))}
          onRemove={parts.length > 1 ? () => onChange(parts.filter((_, xi) => xi !== i)) : undefined} />
      ))}

      {parts.length > 1 && (
        <p className="text-[11px] text-fg4">
          Количества по одной позиции складываются по всем источникам стороны.
        </p>
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
  const [left, setLeft] = useState<SideSource[]>(
    existing ? sidePartsOf(existing.spec.left) : [EMPTY_PART]);
  const [right, setRight] = useState<SideSource[]>(
    existing ? sidePartsOf(existing.spec.right) : [EMPTY_PART]);
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

  const complete = (parts: SideSource[]) =>
    parts.length > 0 && parts.every(p => p.sourceId && p.valueColumn && p.keyColumns.length > 0);
  const valid = name.trim() !== '' && complete(left) && complete(right);

  async function save() {
    // Первый источник дублируется в поля стороны: их читают спеки прежней формы, и терять
    // совместимость ради формы записи незачем.
    const side = (parts: SideSource[]): ReconciliationSide => ({ ...parts[0], sources: parts });
    const spec = {
      left: side(left), right: side(right),
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
          <SideEditor title="Слева" parts={left} onChange={setLeft} sources={sources} />
          <SideEditor title="Справа" parts={right} onChange={setRight} sources={sources} />
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
