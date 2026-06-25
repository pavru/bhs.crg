import { useState, useId } from 'react';
import { Plus, Trash2, ArrowUp, ArrowDown, Layers } from 'lucide-react';
import type { SchemaField, FieldGroup } from '@/shared/api/schema';
// ─── Group editor ─────────────────────────────────────────────────────────────

export function GroupEditor({
  groups, effectiveFields, onChange,
}: {
  groups: FieldGroup[];
  effectiveFields: SchemaField[];
  onChange: (groups: FieldGroup[]) => void;
}) {
  const [newTitle, setNewTitle] = useState('');
  const uid = useId();

  const allKeys = effectiveFields.map(f => f.key);
  const usedKeys = new Set(groups.flatMap(g => g.fieldKeys));

  function addGroup() {
    if (!newTitle.trim()) return;
    const key = `group_${Date.now()}`;
    onChange([...groups, { key, title: newTitle.trim(), fieldKeys: [] }]);
    setNewTitle('');
  }

  function removeGroup(key: string) {
    onChange(groups.filter(g => g.key !== key));
  }

  function renameGroup(key: string, title: string) {
    onChange(groups.map(g => g.key === key ? { ...g, title } : g));
  }

  function moveGroup(key: string, dir: -1 | 1) {
    const idx = groups.findIndex(g => g.key === key);
    if (idx < 0) return;
    const next = [...groups];
    const swap = idx + dir;
    if (swap < 0 || swap >= next.length) return;
    [next[idx], next[swap]] = [next[swap], next[idx]];
    onChange(next);
  }

  function toggleField(groupKey: string, fieldKey: string, checked: boolean) {
    onChange(groups.map(g => {
      if (g.key !== groupKey) return g;
      const keys = checked
        ? [...g.fieldKeys.filter(k => k !== fieldKey), fieldKey]
        : g.fieldKeys.filter(k => k !== fieldKey);
      return { ...g, fieldKeys: keys };
    }));
  }

  function moveField(groupKey: string, fieldKey: string, dir: -1 | 1) {
    onChange(groups.map(g => {
      if (g.key !== groupKey) return g;
      const idx = g.fieldKeys.indexOf(fieldKey);
      if (idx < 0) return g;
      const next = [...g.fieldKeys];
      const swap = idx + dir;
      if (swap < 0 || swap >= next.length) return g;
      [next[idx], next[swap]] = [next[swap], next[idx]];
      return { ...g, fieldKeys: next };
    }));
  }

  return (
    <div className="space-y-3">
      {groups.length === 0 && (
        <p className="text-xs text-fg4 py-1">Группы не заданы — поля отображаются плоским списком.</p>
      )}
      {groups.map(group => {
        const assignedFields = group.fieldKeys.filter(k => allKeys.includes(k));
        const unassigned = allKeys.filter(k => !group.fieldKeys.includes(k));
        return (
          <div key={group.key} className="border border-stroke rounded-lg p-3 space-y-2">
            <div className="flex items-center gap-2">
              <Layers size={13} className="text-fg4 shrink-0" />
              <input
                value={group.title}
                onChange={e => renameGroup(group.key, e.target.value)}
                className="flex-1 text-sm font-medium border-b border-transparent hover:border-stroke-strong focus:border-brand bg-transparent outline-none"
                placeholder="Название группы"
              />
              <div className="flex items-center gap-0.5 shrink-0">
                <button type="button" onClick={() => moveGroup(group.key, -1)}
                  disabled={groups.indexOf(group) === 0}
                  className="p-1 text-stroke-strong hover:text-fg2 disabled:opacity-20">
                  <ArrowUp size={12} />
                </button>
                <button type="button" onClick={() => moveGroup(group.key, 1)}
                  disabled={groups.indexOf(group) === groups.length - 1}
                  className="p-1 text-stroke-strong hover:text-fg2 disabled:opacity-20">
                  <ArrowDown size={12} />
                </button>
                <button type="button" onClick={() => removeGroup(group.key)}
                  className="p-1 text-stroke-strong hover:text-danger">
                  <Trash2 size={12} />
                </button>
              </div>
            </div>
            <div className="flex flex-col gap-0.5">
              {assignedFields.length === 0 && (
                <span className="text-xs text-fg4">Нет полей</span>
              )}
              {assignedFields.map((k, idx) => {
                const f = effectiveFields.find(ef => ef.key === k);
                return (
                  <div key={k} className="flex items-center gap-1 bg-brand-subtle border border-brand-subtle text-brand-hover text-xs px-2 py-0.5 rounded group/field">
                    <span className="flex-1 truncate">{f?.title ?? k}</span>
                    <button type="button" onClick={() => moveField(group.key, k, -1)}
                      disabled={idx === 0}
                      className="p-0.5 text-brand-subtle hover:text-brand disabled:opacity-20">
                      <ArrowUp size={10} />
                    </button>
                    <button type="button" onClick={() => moveField(group.key, k, 1)}
                      disabled={idx === assignedFields.length - 1}
                      className="p-0.5 text-brand-subtle hover:text-brand disabled:opacity-20">
                      <ArrowDown size={10} />
                    </button>
                    <button type="button" onClick={() => toggleField(group.key, k, false)}
                      className="p-0.5 text-brand-subtle hover:text-brand-hover">×</button>
                  </div>
                );
              })}
            </div>
            {unassigned.length > 0 && (
              <div>
                <p className="text-xs text-fg4 mb-1">Добавить поле:</p>
                <div className="flex flex-wrap gap-1">
                  {unassigned.map(k => {
                    const f = effectiveFields.find(ef => ef.key === k);
                    const inOther = usedKeys.has(k) && !group.fieldKeys.includes(k);
                    return (
                      <button key={`${uid}-${k}`} type="button"
                        onClick={() => toggleField(group.key, k, true)}
                        className={`text-xs px-2 py-0.5 rounded-full border transition-colors ${
                          inOther
                            ? 'border-stroke text-fg4 hover:border-brand-subtle hover:text-brand'
                            : 'border-dashed border-stroke-strong text-fg3 hover:border-brand hover:text-brand'
                        }`}>
                        + {f?.title ?? k}
                        {inOther && <span className="ml-1 text-yellow-500">↗</span>}
                      </button>
                    );
                  })}
                </div>
              </div>
            )}
          </div>
        );
      })}
      <div className="flex items-center gap-2 mt-1">
        <input
          value={newTitle}
          onChange={e => setNewTitle(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && addGroup()}
          placeholder="Название новой группы"
          className="flex-1 border border-dashed border-stroke-strong rounded-md px-3 py-1.5 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand focus:border-brand bg-surface"
        />
        <button type="button" onClick={addGroup} disabled={!newTitle.trim()}
          className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover disabled:opacity-40">
          <Plus size={14} /> Группа
        </button>
      </div>
    </div>
  );
}

