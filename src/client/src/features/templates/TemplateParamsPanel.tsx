import { useState } from 'react';
import { ChevronDown, ChevronUp, SlidersHorizontal, Plus, Trash2 } from 'lucide-react';
import { useUpdateTemplateParameters } from '@/shared/api/templates';
import type { Template, TemplateParam } from '@/shared/api/types';

export function parseTemplateParams(json: string | null): TemplateParam[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? (a as TemplateParam[]) : []; } catch { return []; }
}

/**
 * Объявление параметров шаблона (имя/подпись/тип/значение по умолчанию) — чтобы одним шаблоном
 * покрыть варианты без дублей. В Typst доступны как <code>data.params.имя</code>; значение по
 * умолчанию можно переопределить на конкретном документе. Сохраняется по каждому изменению
 * (как «Настройки страницы»). Родитель монтирует с key={template.id} — стейт инициализируется из шаблона.
 */
export function TemplateParamsPanel({ template, onSaved }: { template: Template; onSaved: (t: Template) => void }) {
  const [open, setOpen] = useState(false);
  const [params, setParams] = useState<TemplateParam[]>(() => parseTemplateParams(template.parameters));
  const update = useUpdateTemplateParameters();

  function save(next: TemplateParam[]) {
    setParams(next);
    update.mutate({ id: template.id, parameters: next.length ? JSON.stringify(next) : null }, { onSuccess: onSaved });
  }
  const patch = (i: number, p: Partial<TemplateParam>) => save(params.map((x, idx) => (idx === i ? { ...x, ...p } : x)));

  return (
    <div className="border-t border-stroke bg-surface">
      <button onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-4 py-2.5 hover:bg-base transition-colors text-left">
        <SlidersHorizontal size={13} className="text-fg4" />
        <span className="text-xs font-medium text-fg2 flex-1">Параметры шаблона{params.length > 0 ? ` (${params.length})` : ''}</span>
        {open ? <ChevronUp size={13} className="text-fg4" /> : <ChevronDown size={13} className="text-fg4" />}
      </button>
      {open && (
        <div className="px-4 pb-4 pt-3 border-t border-muted space-y-2">
          <p className="text-[11px] text-fg4">
            Доступны в Typst как <code className="text-fg3">data.params.имя</code>. Значение по умолчанию можно
            переопределить на конкретном документе.
          </p>
          {params.map((p, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <input value={p.name} onChange={e => patch(i, { name: e.target.value })} placeholder="имя"
                className="w-24 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
              <input value={p.label} onChange={e => patch(i, { label: e.target.value })} placeholder="подпись"
                className="flex-1 min-w-0 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
              <select value={p.type}
                onChange={e => patch(i, { type: e.target.value as TemplateParam['type'], default: e.target.value === 'boolean' ? false : e.target.value === 'number' ? 0 : '' })}
                className="text-xs border border-stroke-strong rounded px-1 py-1 bg-surface text-fg3">
                <option value="string">текст</option>
                <option value="number">число</option>
                <option value="boolean">да/нет</option>
              </select>
              <ParamDefault type={p.type} value={p.default} onChange={v => patch(i, { default: v })} />
              <button onClick={() => save(params.filter((_, idx) => idx !== i))} title="Удалить параметр"
                className="p-1 text-stroke-strong hover:text-danger shrink-0"><Trash2 size={12} /></button>
            </div>
          ))}
          <button onClick={() => save([...params, { name: '', label: '', type: 'string', default: '' }])}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={12} /> Добавить параметр
          </button>
        </div>
      )}
    </div>
  );
}

/** Поле значения по умолчанию, зависит от типа параметра. */
function ParamDefault({ type, value, onChange }: {
  type: TemplateParam['type']; value: TemplateParam['default']; onChange: (v: TemplateParam['default']) => void;
}) {
  if (type === 'boolean')
    return (
      <span className="w-20 shrink-0 flex justify-center">
        <input type="checkbox" checked={value === true} onChange={e => onChange(e.target.checked)} title="Значение по умолчанию" />
      </span>
    );
  return (
    <input type={type === 'number' ? 'number' : 'text'} value={value == null ? '' : String(value)}
      onChange={e => onChange(type === 'number' ? Number(e.target.value) : e.target.value)} placeholder="по умолч."
      className="w-20 shrink-0 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
  );
}
