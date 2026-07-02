import { useMemo } from 'react';
import { Plus, Trash2, AlertTriangle, Loader2 } from 'lucide-react';
import { parseXPath, toXPath, type XPathModel, type XPathStep, type XPathPredicate } from './xpathModel';
import { useExpressionPreview, type ExpressionPreviewSpec } from '@/shared/api/datasets';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';

interface XPathBuilderProps {
  value: string;
  onChange: (value: string) => void;
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
export function XPathBuilder({ value, onChange, placeholder, preview }: XPathBuilderProps) {
  const model = useMemo(() => parseXPath(value), [value]);
  const unparseable = value.trim() !== '' && model === null;

  function updateModel(next: XPathModel) {
    onChange(toXPath(next));
  }

  const debouncedValue = useDebouncedValue(value, 400);
  const previewSpec = preview ? preview(debouncedValue) : null;

  return (
    <div className="space-y-1.5">
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        placeholder={placeholder ?? '/Root/Item'}
        spellCheck={false}
        className="w-full border border-stroke-strong rounded-md px-2 py-1.5 text-xs font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand"
      />
      {unparseable ? (
        <p className="text-xs text-warning flex items-start gap-1.5 py-1">
          <AlertTriangle size={13} className="shrink-0 mt-0.5" />
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
    <div className="text-xs rounded-md border border-stroke bg-base px-2 py-1.5">
      {isFetching ? (
        <span className="flex items-center gap-1.5 text-fg4">
          <Loader2 size={11} className="animate-spin" /> Проверка...
        </span>
      ) : error ? (
        <span className="text-danger">
          {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось проверить выражение'}
        </span>
      ) : data ? (
        <div className="space-y-0.5">
          <span className="font-medium text-fg2">
            {spec.expr ? `Значений найдено: ${data.matchCount}` : `Узлов найдено: ${data.matchCount}`}
          </span>
          {data.samples.length > 0 && (
            <ul className="text-fg4">
              {data.samples.map((s, i) => <li key={i} className="truncate">{s}</li>)}
            </ul>
          )}
        </div>
      ) : null}
    </div>
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
      {model.absolute && <span className="text-fg4 text-xs pt-1.5 shrink-0">/</span>}
      {model.steps.map((step, i) => (
        <StepEditor key={i} step={step} isLast={i === model.steps.length - 1}
          onChange={patch => updateStep(i, patch)} onRemove={() => removeStep(i)} />
      ))}
      <button type="button" onClick={addStep}
        className="text-xs text-brand hover:text-brand-hover flex items-center gap-1 px-1.5 py-1 shrink-0">
        <Plus size={12} /> Шаг
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
    <div className="border border-stroke rounded-md px-1.5 py-1 bg-base flex flex-col gap-1 min-w-0">
      <div className="flex items-center gap-1">
        {isLast && (
          <button type="button"
            onClick={() => onChange({ axis: step.axis === 'attribute' ? 'child' : 'attribute' })}
            title="Атрибут / элемент"
            className={`text-xs px-1 rounded shrink-0 ${step.axis === 'attribute' ? 'bg-brand text-white' : 'text-fg4 border border-stroke hover:border-stroke-strong'}`}>
            @
          </button>
        )}
        <input value={step.name} onChange={e => onChange({ name: e.target.value })}
          placeholder={step.axis === 'attribute' ? 'атрибут' : 'элемент / *'}
          className="w-24 text-xs font-mono border-b border-transparent hover:border-stroke focus:border-brand bg-transparent outline-none" />
        <button type="button" onClick={onRemove} className="text-fg4 hover:text-danger shrink-0">
          <Trash2 size={11} />
        </button>
      </div>
      <div className="flex flex-wrap gap-1">
        {step.predicates.map((p, i) => (
          <PredicateEditor key={i} predicate={p}
            onChange={next => updatePredicate(i, next)} onRemove={() => removePredicate(i)} />
        ))}
        <button type="button" onClick={addPredicate} className="text-[11px] text-brand hover:text-brand-hover shrink-0">
          + условие
        </button>
      </div>
    </div>
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

  const inputCls = 'bg-transparent border-b border-brand-subtle outline-none focus:border-brand-hover';

  return (
    <span className="inline-flex items-center gap-1 bg-brand-subtle border border-brand-subtle rounded px-1.5 py-0.5 text-[11px] text-brand-hover">
      <select value={predicateKind(predicate)} onChange={e => setKind(e.target.value as PredicateKind)}
        className="bg-transparent outline-none">
        <option value="exists">путь есть</option>
        <option value="equals">путь =</option>
        <option value="contains">содержит</option>
        <option value="index">позиция N</option>
        <option value="last">последний</option>
      </select>

      {predicate.kind !== 'position' && (
        <input value={predicate.path} onChange={e => onChange({ ...predicate, path: e.target.value })}
          placeholder="путь" className={`w-16 font-mono ${inputCls}`} />
      )}

      {predicate.kind === 'equals' && (
        <>
          <select value={predicate.op} onChange={e => onChange({ ...predicate, op: e.target.value as '=' | '!=' })}
            className="bg-transparent outline-none">
            <option value="=">=</option>
            <option value="!=">≠</option>
          </select>
          <input value={predicate.value} onChange={e => onChange({ ...predicate, value: e.target.value })}
            placeholder="значение" className={`w-16 ${inputCls}`} />
        </>
      )}

      {predicate.kind === 'contains' && (
        <input value={predicate.value} onChange={e => onChange({ ...predicate, value: e.target.value })}
          placeholder="значение" className={`w-16 ${inputCls}`} />
      )}

      {predicate.kind === 'position' && predicate.op === 'index' && (
        <input type="number" min={1} value={predicate.index}
          onChange={e => onChange({ kind: 'position', op: 'index', index: Number(e.target.value) })}
          className={`w-10 ${inputCls}`} />
      )}

      <button type="button" onClick={onRemove} className="text-brand-subtle hover:text-danger">×</button>
    </span>
  );
}
