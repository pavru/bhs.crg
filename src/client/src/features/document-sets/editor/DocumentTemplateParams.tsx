import { useMemo } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useSetDocumentTemplateParams } from '@/shared/api/documentSets';
import { parseTemplateParams } from '@/features/templates/templateParams';
import type { DocumentInstance, Template, TemplateParam } from '@/shared/api/types';
import { Tooltip } from '@/shared/ui/Tooltip';

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
        <label className="block text-xs font-medium text-on-surface-variant">
          Параметры · <span className="text-on-surface">«{template.name}»</span>
        </label>
        {!participating && (
          <Tooltip content="Шаблон не отмечен для генерации">
            <span className="flex items-center gap-1 text-caption text-warning">
              <AlertTriangle size={11} className="shrink-0" />
              не участвует — значения сохранятся, но PDF по нему не создастся
            </span>
          </Tooltip>
        )}
      </div>
      {declared.length === 0 ? (
        <p className="text-caption text-fg4">У этого шаблона нет объявленных параметров.</p>
      ) : (
        <div className="rounded-md border border-outline-variant p-2.5 space-y-1.5">
          {declared.map(p => {
            const overridden = Object.prototype.hasOwnProperty.call(overrides, p.name);
            const value = overridden ? overrides[p.name] : p.default;
            return (
              <div key={p.name} className="flex items-center gap-2">
                <Tooltip content={p.name}><span className="text-xs text-on-surface-variant flex-1 min-w-0 truncate">{p.label || p.name}</span></Tooltip>
                <ParamValueInput type={p.type} value={value}
                  onChange={v => commit({ ...overrides, [p.name]: v })} />
                {overridden ? (
                  <Tooltip content="Сбросить к значению по умолчанию">
                    <button onClick={() => { const n = { ...overrides }; delete n[p.name]; commit(n); }} className="text-caption text-fg4 hover:text-primary shrink-0">
                      сброс
                    </button>
                  </Tooltip>
                ) : (
                  <Tooltip content="Используется значение по умолчанию шаблона"><span className="text-caption text-fg4 shrink-0">по умолч.</span></Tooltip>
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
      className="w-40 shrink-0 text-xs border border-outline rounded px-1.5 py-1 bg-surface text-on-surface" />
  );
}
