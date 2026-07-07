import { useMemo } from 'react';
import { useSetDocumentTemplateParams } from '@/shared/api/documentSets';
import { parseTemplateParams } from '@/features/templates/TemplateParamsPanel';
import type { DocumentInstance, Template, TemplateParam } from '@/shared/api/types';

/**
 * Значения параметров шаблонов на документе. Параметры ИНДИВИДУАЛЬНЫ для шаблона — переопределения
 * хранятся вложенно {templateId:{name:value}}. Для каждого переданного шаблона (обычно — выбранных
 * для генерации), у которого объявлены параметры, показывается своя секция с эффективными значениями
 * (переопределение документа, иначе дефолт шаблона), возможностью переопределить и сбросить.
 * Изменение сбрасывает документ в черновик (влияет на вывод).
 */
export function DocumentTemplateParams({ setId, instance, templates }: {
  setId: string; instance: DocumentInstance; templates: Template[];
}) {
  const setParams = useSetDocumentTemplateParams();
  const allOverrides = useMemo<Record<string, Record<string, unknown>>>(() => {
    try { return instance.templateParams ? JSON.parse(instance.templateParams) : {}; } catch { return {}; }
  }, [instance.templateParams]);

  const withParams = templates
    .map(t => ({ t, declared: parseTemplateParams(t.parameters) }))
    .filter(x => x.declared.length > 0);
  if (withParams.length === 0) return null;

  function commit(next: Record<string, Record<string, unknown>>) {
    // Убираем пустые под-объекты шаблонов, а если ничего не осталось — null.
    const clean = Object.fromEntries(Object.entries(next).filter(([, v]) => Object.keys(v).length > 0));
    setParams.mutate({ setId, instanceId: instance.id, params: Object.keys(clean).length ? clean : null });
  }

  const multi = withParams.length > 1;

  return (
    <div className="space-y-2">
      <label className="block text-xs font-medium text-fg2">Параметры шаблон{multi ? 'ов' : 'а'}</label>
      <div className="space-y-2">
        {withParams.map(({ t, declared }) => {
          const overrides = allOverrides[t.id] ?? {};
          return (
            <div key={t.id} className="rounded-md border border-stroke p-2.5 space-y-1.5">
              {multi && <div className="text-[11px] font-medium text-fg3">{t.name}</div>}
              {declared.map(p => {
                const overridden = Object.prototype.hasOwnProperty.call(overrides, p.name);
                const value = overridden ? overrides[p.name] : p.default;
                return (
                  <div key={p.name} className="flex items-center gap-2">
                    <span className="text-xs text-fg2 flex-1 min-w-0 truncate" title={p.name}>{p.label || p.name}</span>
                    <ParamValueInput type={p.type} value={value}
                      onChange={v => commit({ ...allOverrides, [t.id]: { ...overrides, [p.name]: v } })} />
                    {overridden ? (
                      <button onClick={() => { const n = { ...overrides }; delete n[p.name]; commit({ ...allOverrides, [t.id]: n }); }}
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
