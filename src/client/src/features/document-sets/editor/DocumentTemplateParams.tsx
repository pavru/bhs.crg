import { useMemo } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useSetDocumentTemplateParams } from '@/shared/api/documentSets';
import { parseTemplateParams } from '@/features/templates/TemplateParamsPanel';
import type { DocumentInstance, Template, TemplateParam } from '@/shared/api/types';

/**
 * Значения параметров ОДНОГО сфокусированного шаблона на документе. Параметры индивидуальны для
 * шаблона — переопределения хранятся вложенно {templateId:{name:value}}. Блок всегда носит имя
 * сфокусированного шаблона в заголовке (связь «выбранная строка списка → её параметры»). Если
 * шаблон не участвует в генерации — показываем метку: значения сохранятся, но PDF по нему не создастся.
 * Изменение сбрасывает документ в черновик (влияет на вывод).
 */
export function DocumentTemplateParams({ setId, instance, template, participating }: {
  setId: string; instance: DocumentInstance; template: Template; participating: boolean;
}) {
  const setParams = useSetDocumentTemplateParams();
  const allOverrides = useMemo<Record<string, Record<string, unknown>>>(() => {
    try { return instance.templateParams ? JSON.parse(instance.templateParams) : {}; } catch { return {}; }
  }, [instance.templateParams]);

  const declared = parseTemplateParams(template.parameters);
  const overrides = allOverrides[template.id] ?? {};

  function commit(nextForTemplate: Record<string, unknown>) {
    // Пустой под-объект шаблона убираем, а если после этого пусто всё — пишем null.
    const merged = { ...allOverrides, [template.id]: nextForTemplate };
    const clean = Object.fromEntries(Object.entries(merged).filter(([, v]) => Object.keys(v).length > 0));
    setParams.mutate({ setId, instanceId: instance.id, params: Object.keys(clean).length ? clean : null });
  }

  return (
    <div className="space-y-2">
      <div className="flex items-baseline gap-2 flex-wrap">
        <label className="block text-xs font-medium text-fg2">
          Параметры · <span className="text-fg1">«{template.name}»</span>
        </label>
        {!participating && (
          <span className="flex items-center gap-1 text-[11px] text-warning" title="Шаблон не отмечен для генерации">
            <AlertTriangle size={11} className="shrink-0" />
            не участвует — значения сохранятся, но PDF по нему не создастся
          </span>
        )}
      </div>
      {declared.length === 0 ? (
        <p className="text-[11px] text-fg4">У этого шаблона нет объявленных параметров.</p>
      ) : (
        <div className="rounded-md border border-stroke p-2.5 space-y-1.5">
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
      )}
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
