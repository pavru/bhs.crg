import { useMemo } from 'react';
import { useSetDocumentTemplateParams } from '@/shared/api/documentSets';
import { parseTemplateParams } from '@/features/templates/TemplateParamsPanel';
import type { DocumentInstance, Template, TemplateParam } from '@/shared/api/types';

/**
 * Значения параметров шаблона на конкретном документе: показывает объявленные шаблоном параметры с их
 * эффективным значением (переопределение документа, иначе дефолт шаблона), позволяет переопределить и
 * сбросить. Изменение сбрасывает документ в черновик (влияет на вывод). Ничего не рендерит, если у
 * активного шаблона параметров нет.
 */
export function DocumentTemplateParams({ setId, instance, template }: {
  setId: string; instance: DocumentInstance; template: Template;
}) {
  const declared = useMemo(() => parseTemplateParams(template.parameters), [template.parameters]);
  const overrides = useMemo<Record<string, unknown>>(() => {
    try { return instance.templateParams ? JSON.parse(instance.templateParams) : {}; } catch { return {}; }
  }, [instance.templateParams]);
  const setParams = useSetDocumentTemplateParams();

  if (declared.length === 0) return null;

  function commit(next: Record<string, unknown>) {
    setParams.mutate({ setId, instanceId: instance.id, params: Object.keys(next).length ? next : null });
  }

  return (
    <div className="space-y-1.5">
      <label className="block text-xs font-medium text-fg2">Параметры шаблона</label>
      <div className="space-y-1.5 rounded-md border border-stroke p-2.5">
        {declared.map(p => {
          const overridden = Object.prototype.hasOwnProperty.call(overrides, p.name);
          const value = overridden ? overrides[p.name] : p.default;
          return (
            <div key={p.name} className="flex items-center gap-2">
              <span className="text-xs text-fg2 flex-1 min-w-0 truncate" title={p.name}>{p.label || p.name}</span>
              <ParamValueInput type={p.type} value={value}
                onChange={v => commit({ ...overrides, [p.name]: v })} />
              {overridden ? (
                <button onClick={() => { const n = { ...overrides }; delete n[p.name]; commit(n); }}
                  title="Сбросить к значению по умолчанию" className="text-[10px] text-fg4 hover:text-brand shrink-0">
                  сброс
                </button>
              ) : (
                <span className="text-[10px] text-fg4 shrink-0" title="Используется значение по умолчанию шаблона">по умолч.</span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function ParamValueInput({ type, value, onChange }: {
  type: TemplateParam['type']; value: unknown; onChange: (v: unknown) => void;
}) {
  if (type === 'boolean')
    return <input type="checkbox" checked={value === true} onChange={e => onChange(e.target.checked)} className="shrink-0" />;
  return (
    <input type={type === 'number' ? 'number' : 'text'} value={value == null ? '' : String(value)}
      onChange={e => onChange(type === 'number' ? Number(e.target.value) : e.target.value)}
      className="w-40 shrink-0 text-xs border border-stroke-strong rounded px-1.5 py-1 bg-surface text-fg1" />
  );
}
