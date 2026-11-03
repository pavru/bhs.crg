import { useMemo } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Add, Close, Delete, Warning } from '@/shared/ui/icons';
import { parseXPath, toXPath, type XPathModel, type XPathStep, type XPathPredicate } from './xpathModel';
import { useExpressionPreview, type ExpressionPreviewSpec } from '@/shared/api/datasets';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';
import { IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { TextField } from '@/shared/ui/TextField';

interface XPathBuilderProps {
  value: string;
  onChange: (value: string) => void;
  /** Имя поля для читалки: плотное поле подпись прячет, а пример ввода — не имя. */
  label?: string;
  placeholder?: string;
  /**
   * Даёт предпросмотр данных, которые матчит текущее выражение — вычисляется от debounced
   * value. Возвращает null, если предпросмотр сейчас невозможен (например, для колонки не
   * задан ещё row-selector). Для row-selector'а — вернуть { fileId, rowSelector: value } (без
   * expr — предпросмотр самого пути); для колонки — { fileId, rowSelector: <контекст>, expr: value }.
   */
  preview?: (value: string) => ExpressionPreviewSpec | null;
}

/**
 * Редактор XPath-выражения: сырой текст (всегда редактируемый) + визуальный конструктор
 * шагов/условий под ним. Конструктор строится из value через parseXPath — если выражение
 * вышло за пределы поддерживаемого поднабора (см. xpathModel.ts), показывается
 * предупреждение вместо визуального редактора; сам текст при этом остаётся рабочим.
 */
export function XPathBuilder({ value, onChange, label = 'Выражение XPath', placeholder, preview }: XPathBuilderProps) {
  const model = useMemo(() => parseXPath(value), [value]);
  const unparseable = value.trim() !== '' && model === null;

  function updateModel(next: XPathModel) {
    onChange(toXPath(next));
  }

  const debouncedValue = useDebouncedValue(value, 400);
  const previewSpec = preview ? preview(debouncedValue) : null;

  return (
    <div className="space-y-1.5">
      <TextField density="dense" label={label} value={value} onChange={e => onChange(e.target.value)}
        placeholder={placeholder ?? '/Root/Item'} spellCheck={false} className="font-mono" />
      {unparseable ? (
        <p className="text-body-small text-warning flex items-start gap-1.5 py-1">
          <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
          Выражение вне поддерживаемого конструктором набора (необычные оси, функции,
          объединения) — конструктор недоступен, редактируйте текст выше напрямую.
          Он снова заработает, если выражение станет распознаваемым.
        </p>
      ) : (
        <StepsEditor model={model ?? { absolute: true, steps: [] }} onChange={updateModel} />
      )}
      {preview && <XPathPreviewPanel spec={previewSpec} />}
    </div>
  );
}

// ─── Предпросмотр данных ────────────────────────────────────────────────────────

function XPathPreviewPanel({ spec }: { spec: ExpressionPreviewSpec | null }) {
  const { data, isFetching, error } = useExpressionPreview(spec);

  if (!spec) return null;

  return (
    <Card variant="filled" flush className="px-3 py-2 text-body-small">
      {isFetching ? (
        <span className="flex items-center gap-1.5 text-on-surface-variant">
          <CircularProgress size={18} /> Проверка...
        </span>
      ) : error ? (
        <span className="text-error">
          {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось проверить выражение'}
        </span>
      ) : data ? (
        <div className="space-y-0.5">
          <span className="font-medium text-on-surface-variant">
            {spec.expr ? `Значений найдено: ${data.matchCount}` : `Узлов найдено: ${data.matchCount}`}
          </span>
          {data.samples.length > 0 && (
            <ul className="text-on-surface-variant">
              {data.samples.map((s, i) => <li key={i} className="truncate">{s}</li>)}
            </ul>
          )}
        </div>
      ) : null}
    </Card>
  );
}

// ─── Шаги ───────────────────────────────────────────────────────────────────────

function StepsEditor({ model, onChange }: { model: XPathModel; onChange: (m: XPathModel) => void }) {
  function updateStep(i: number, patch: Partial<XPathStep>) {
    onChange({ ...model, steps: model.steps.map((s, idx) => idx === i ? { ...s, ...patch } : s) });
  }
  function addStep() {
    onChange({ ...model, steps: [...model.steps, { axis: 'child', name: '', predicates: [] }] });
  }
  function removeStep(i: number) {
    onChange({ ...model, steps: model.steps.filter((_, idx) => idx !== i) });
  }

  return (
    <div className="flex flex-wrap items-start gap-1">
      {model.absolute && <span className="text-on-surface-variant text-body-small pt-1.5 shrink-0">/</span>}
      {model.steps.map((step, i) => (
        <StepEditor key={i} step={step} isLast={i === model.steps.length - 1}
          onChange={patch => updateStep(i, patch)} onRemove={() => removeStep(i)} />
      ))}
      <button type="button" onClick={addStep}
        className="text-body-small text-primary state-layer flex items-center gap-1 px-1.5 py-1 shrink-0">
        <Icon icon={Add} size={18} /> Шаг
      </button>
    </div>
  );
}

function StepEditor({ step, isLast, onChange, onRemove }: {
  step: XPathStep; isLast: boolean;
  onChange: (patch: Partial<XPathStep>) => void; onRemove: () => void;
}) {
  function addPredicate() {
    onChange({ predicates: [...step.predicates, { kind: 'exists', path: '' }] });
  }
  function updatePredicate(i: number, next: XPathPredicate) {
    onChange({ predicates: step.predicates.map((p, idx) => idx === i ? next : p) });
  }
  function removePredicate(i: number) {
    onChange({ predicates: step.predicates.filter((_, idx) => idx !== i) });
  }

  return (
    // Шаг — карточка на уровень (Р6), условия — внутри неё.
    <Card flush className="px-2 py-1.5 min-w-0">
      <div className="flex items-center gap-1">
        {isLast && (
          <Tooltip content="Атрибут / элемент">
            <button type="button"
              onClick={() => onChange({ axis: step.axis === 'attribute' ? 'child' : 'attribute' })}
              className={`text-body-small px-1 rounded-xs shrink-0 ${step.axis === 'attribute' ? 'bg-primary text-on-primary' : 'text-on-surface-variant border border-outline-variant hover:border-outline'}`}>
              @
            </button>
          </Tooltip>
        )}
        <TextField density="dense" label={step.axis === 'attribute' ? 'Имя атрибута' : 'Имя элемента'}
          value={step.name} onChange={e => onChange({ name: e.target.value })}
          placeholder={step.axis === 'attribute' ? 'атрибут' : 'элемент / *'}
          className="font-mono" containerClassName="w-32" />
        <IconButton label="Удалить шаг" danger onClick={onRemove} icon={Delete} />
      </div>
      <div className="flex flex-wrap items-center gap-1 mt-1">
        {step.predicates.map((p, i) => (
          <PredicateEditor key={i} predicate={p}
            onChange={next => updatePredicate(i, next)} onRemove={() => removePredicate(i)} />
        ))}
        <button type="button" onClick={addPredicate} className="text-label-small text-primary state-layer shrink-0">
          + условие
        </button>
      </div>
    </Card>
  );
}

// ─── Условия ────────────────────────────────────────────────────────────────────

type PredicateKind = 'exists' | 'equals' | 'contains' | 'index' | 'last';

function predicateKind(p: XPathPredicate): PredicateKind {
  if (p.kind === 'position') return p.op;
  return p.kind;
}

function PredicateEditor({ predicate, onChange, onRemove }: {
  predicate: XPathPredicate; onChange: (p: XPathPredicate) => void; onRemove: () => void;
}) {
  function setKind(kind: PredicateKind) {
    switch (kind) {
      case 'last':     onChange({ kind: 'position', op: 'last' }); break;
      case 'index':    onChange({ kind: 'position', op: 'index', index: 1 }); break;
      case 'equals':   onChange({ kind: 'equals', path: '', op: '=', value: '' }); break;
      case 'contains': onChange({ kind: 'contains', path: '', value: '' }); break;
      case 'exists':   onChange({ kind: 'exists', path: '' }); break;
    }
  }

  return (
    <span className="inline-flex flex-wrap items-center gap-1 rounded-sm bg-surface-container-high px-1.5 py-1 text-label-small text-on-surface">
      <Select variant="inline" aria-label="Вид условия" value={predicateKind(predicate)} onValueChange={kind => setKind(kind as PredicateKind)}>
        <SelectItem value="exists">путь есть</SelectItem>
        <SelectItem value="equals">путь =</SelectItem>
        <SelectItem value="contains">содержит</SelectItem>
        <SelectItem value="index">позиция N</SelectItem>
        <SelectItem value="last">последний</SelectItem>
      </Select>

      {predicate.kind !== 'position' && (
        <TextField density="dense" label="Путь условия" placeholder="путь" value={predicate.path}
          onChange={e => onChange({ ...predicate, path: e.target.value })} className="font-mono" containerClassName="w-28" />
      )}

      {predicate.kind === 'equals' && (
        <>
          <Select variant="inline" aria-label="Сравнение" value={predicate.op} onValueChange={op => onChange({ ...predicate, op: op as '=' | '!=' })}>
            <SelectItem value="=">=</SelectItem>
            <SelectItem value="!=">≠</SelectItem>
          </Select>
          <TextField density="dense" label="Значение условия" placeholder="значение" value={predicate.value}
            onChange={e => onChange({ ...predicate, value: e.target.value })} containerClassName="w-28" />
        </>
      )}

      {predicate.kind === 'contains' && (
        <TextField density="dense" label="Значение условия" placeholder="значение" value={predicate.value}
          onChange={e => onChange({ ...predicate, value: e.target.value })} containerClassName="w-28" />
      )}

      {predicate.kind === 'position' && predicate.op === 'index' && (
        <TextField density="dense" label="Номер позиции" type="number" min={1} value={predicate.index}
          onChange={e => onChange({ kind: 'position', op: 'index', index: Number(e.target.value) })} containerClassName="w-20" />
      )}

      <IconButton label="Удалить условие" onClick={onRemove} icon={Close} />
    </span>
  );
}
