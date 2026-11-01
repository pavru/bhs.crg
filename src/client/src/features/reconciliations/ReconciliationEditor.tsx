import { useMemo, useState } from 'react';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useListDataSetFiles } from '@/shared/api/datasets';
import type { DataSetSource } from '@/shared/api/types';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete } from '@/shared/ui/icons';
import {
  useCreateReconciliation, useUpdateReconciliation,
  OPERATOR_LABELS, sidePartsOf,
  type ComparisonOperator, type Reconciliation, type ReconciliationSide, type SideSource,
  type ToleranceKind,
} from '@/shared/api/reconciliations';
import { Chip } from '@/shared/ui/Chip';

/**
 * Редактор определения сверки (issue #436).
 *
 * Открыт по праву `core.reconciliation.run`, а не администратору: пометка «Admin» осталась здесь от
 * прежнего устройства, где доступ давала роль, и пережила разбор ворот (#989). Её убрали вместе с
 * такой же пометкой на экране сверки — имя роли не решает ничего ни в коде, ни в комментариях.
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
    // space-y-3, а не -2: ниже стоит Select с постоянно поднятой меткой, и она выступает над рамкой
    // поля примерно на 8px (см. OutlinedField) — при 8px зазора от ряда чипов оставалось три
    // пикселя, метка читалась приклеенной к колонкам. Та же причина, что и в issue #727.
    <div className="space-y-3 rounded-sm bg-surface-container/40 p-2">
      <div className="flex items-center gap-2">
        <Select label={`Источник ${index + 1}`} value={part.sourceId}
          onValueChange={v => onChange({ ...EMPTY_PART, sourceId: v })}
          placeholder="— выберите —" containerClassName="flex-1">
          {sources.map(({ source, fileName }) => (
            <SelectItem key={source.id} value={source.id}>{fileName} — {source.name}</SelectItem>
          ))}
        </Select>
        {onRemove && (
          <Button variant="text" danger onClick={onRemove} aria-label="Убрать источник">
            <Icon icon={Delete} size={18} />
          </Button>
        )}
      </div>

      {part.sourceId && columns.length === 0 && (
        <p className="text-body-small text-error">У источника нет разобранной схемы — колонки не выбрать.</p>
      )}

      {columns.length > 0 && (
        <>
          <div>
            <div className="text-body-small text-on-surface-variant mb-1">
              Ключевые колонки {part.keyColumns.length > 0 && (
                <span className="text-on-surface-variant">— порядок: {part.keyColumns.join(' + ')}</span>
              )}
            </div>
            <div className="flex flex-wrap gap-x-1 gap-y-2">
              {columns.map(c => {
                const on = part.keyColumns.includes(c);
                return (
                  <Chip key={c} selected={on} onClick={() => toggleKey(c)}>{c}</Chip>
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
    <div className="space-y-2 rounded-md border border-outline-variant p-3">
      <div className="flex items-center justify-between">
        <span className="text-title-small text-on-surface-variant">{title}</span>
        <Button variant="text" onClick={() => onChange([...parts, EMPTY_PART])}>
          <Icon icon={Add} size={18} /> Источник
        </Button>
      </div>

      {parts.map((part, i) => (
        <PartEditor key={i} part={part} index={i} sources={sources}
          onChange={p => onChange(parts.map((x, xi) => xi === i ? p : x))}
          onRemove={parts.length > 1 ? () => onChange(parts.filter((_, xi) => xi !== i)) : undefined} />
      ))}

      {parts.length > 1 && (
        <p className="text-label-small text-on-surface-variant">
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
  const toast = useSnackbar();
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
          <ModalExit disabled={busy} />
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

        <div className="rounded-md border border-outline-variant p-3 space-y-3">
          <div className="text-title-small text-on-surface-variant">Правило</div>
          <div className="flex items-center gap-2 text-body-medium text-on-surface-variant">
            <span>Слева должно быть</span>
            <Select density="dense" value={operator} onValueChange={v => setOperator(v as ComparisonOperator)}
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
            <Select density="dense" value={toleranceKind} onValueChange={v => setToleranceKind(v as ToleranceKind)}
              aria-label="Вид допуска" className="w-40">
              <SelectItem value="Absolute">в единицах</SelectItem>
              <SelectItem value="Percent">в процентах</SelectItem>
            </Select>
          </div>
          <p className="text-label-small text-on-surface-variant">
            Допуск гасит расхождения округления — без него отчёт забьётся находками на сотые доли.
          </p>
        </div>
      </div>
    </Modal>
  );
}
