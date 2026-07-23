import { useState, useEffect } from 'react';
import { Plus, Trash2, Star, ChevronDown, ChevronUp, AlertTriangle, Copy } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import type { PickType } from '@/shared/ui/TypePicker';
import type { Template, DocumentType } from '@/shared/api/types';
import { useDeleteTemplate } from '@/shared/api/templates';
import { TemplateAssetsPanel } from './TemplateAssetsPanel';
// ─── Template grouping ────────────────────────────────────────────────────────

export interface TemplateGroup {
  name: string;
  versions: Template[];
}

export function groupTemplates(templates: Template[]): TemplateGroup[] {
  const map = new Map<string, Template[]>();
  for (const t of templates) {
    const arr = map.get(t.name) ?? [];
    arr.push(t);
    map.set(t.name, arr);
  }
  return [...map.entries()].map(([name, versions]) => ({
    name,
    versions: [...versions].sort((a, b) => b.version - a.version),
  }));
}

// ─── Version cleanup modal ────────────────────────────────────────────────────

export function VersionCleanupModal({ group, onClose, onDeleted }: {
  group: TemplateGroup;
  onClose: () => void;
  onDeleted: (deletedIds: Set<string>) => void;
}) {
  const deleteMutation = useDeleteTemplate();
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState('');

  const allDefault = group.versions.length > 0 && group.versions.every(t => t.isDefault);
  const protectedIds = new Set<string>();
  if (group.versions.length > 0) protectedIds.add(group.versions[0].id);
  if (!allDefault) {
    for (const t of group.versions) {
      if (t.isDefault) protectedIds.add(t.id);
    }
  }

  const [toDeleteIds, setToDeleteIds] = useState<Set<string>>(
    () => new Set(group.versions.filter(t => !protectedIds.has(t.id)).map(t => t.id)),
  );

  function toggleVersion(id: string) {
    setToDeleteIds(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  const toDelete = group.versions.filter(t => toDeleteIds.has(t.id));

  async function handleDelete() {
    setDeleting(true);
    setError('');
    const deletedIds = new Set<string>();
    try {
      for (const t of toDelete) {
        await deleteMutation.mutateAsync({ id: t.id, documentTypeId: t.documentTypeId });
        deletedIds.add(t.id);
      }
      onDeleted(deletedIds);
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка при удалении');
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Очистка старых версий"
      footer={
        <div className="flex justify-end gap-3">
          <button onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
          <button onClick={handleDelete} disabled={deleting || toDelete.length === 0}
            className="px-4 py-2 text-sm bg-danger hover:bg-danger text-white rounded-md disabled:opacity-50 transition-colors">
            {deleting ? 'Удаление...' : `Удалить ${toDelete.length} вер.`}
          </button>
        </div>
      }>
      <div className="space-y-4">
        <p className="text-sm text-fg2">
          Шаблон <strong>«{group.name}»</strong> — {group.versions.length} версий.
          Отметьте версии для удаления. Активные и дефолтные защищены.
        </p>
        <div className="border border-stroke rounded-lg overflow-hidden">
          {group.versions.map((t, i) => {
            const protected_ = protectedIds.has(t.id);
            const checked = toDeleteIds.has(t.id);
            return (
              <label key={t.id}
                className={`flex items-center gap-3 px-3 py-2.5 text-sm cursor-pointer transition-colors
                  ${i > 0 ? 'border-t border-muted' : ''}
                  ${protected_ ? 'opacity-60 cursor-not-allowed bg-base' : checked ? 'bg-danger-subtle hover:bg-danger-subtle' : 'hover:bg-base'}`}>
                <input
                  type="checkbox"
                  checked={checked}
                  disabled={protected_}
                  onChange={() => toggleVersion(t.id)}
                  className="w-4 h-4 rounded border-stroke-strong text-danger disabled:opacity-40"
                />
                <span className="font-mono text-fg2 shrink-0">v{t.version}</span>
                <span className="flex items-center gap-1.5 flex-1 min-w-0">
                  {t.isActive && <span className="text-xs text-success bg-success-subtle px-1.5 py-0.5 rounded shrink-0">активный</span>}
                  {t.isDefault && <span className="text-xs text-yellow-600 bg-warning-subtle px-1.5 py-0.5 rounded shrink-0">по умолч.</span>}
                  {i === 0 && !t.isActive && <span className="text-xs text-fg4 shrink-0">последняя</span>}
                  {t.comment && <span className="text-xs text-fg4 truncate" title={t.comment}>{t.comment}</span>}
                </span>
                {protected_ && <span className="text-xs text-fg4 shrink-0">защищена</span>}
                {!protected_ && checked && <span className="text-xs text-danger shrink-0">удалить</span>}
              </label>
            );
          })}
        </div>
        {toDelete.length === 0
          ? <p className="text-sm text-fg3">Ничего не выбрано для удаления.</p>
          : <p className="text-sm text-fg2">Будет удалено: <strong>{toDelete.length}</strong> вер.</p>
        }
        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

// ─── Doc type selector ────────────────────────────────────────────────────────

export function DocTypeSelector({ docTypes, selected, onSelect }: {
  docTypes: DocumentType[]; selected: string; onSelect: (id: string) => void;
}) {
  const nonAbstractDocs = docTypes
    .filter(dt => dt.kind === 'Document' && !dt.isAbstract)
    .map<PickType>(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Типы документов' }));
  return (
    <TypePickerField size="sm" className="w-56" recentKey="doc-type" title="Тип документа"
      placeholder="Выберите тип документа" aria-label="Тип документа"
      types={nonAbstractDocs} value={selected || undefined} onChange={id => id && onSelect(id)} />
  );
}

// ─── Grouped sidebar ──────────────────────────────────────────────────────────

export function TemplateSidebar({ groups, selectedTemplate, maxVersions, documentTypeId, onSelect, onNew, onDelete, onDeleteGroup, onDuplicate, onCleanup }: {
  groups: TemplateGroup[];
  selectedTemplate: Template | null;
  maxVersions: number;
  documentTypeId: string;
  onSelect: (t: Template) => void;
  onNew: () => void;
  onDelete: (t: Template) => void;
  onDeleteGroup: (g: TemplateGroup) => void;
  onDuplicate: (g: TemplateGroup) => void;
  onCleanup: (g: TemplateGroup) => void;
}) {
  const [expandedNames, setExpandedNames] = useState<Set<string>>(() => {
    const s = new Set<string>();
    if (selectedTemplate) s.add(selectedTemplate.name);
    return s;
  });

  useEffect(() => {
    if (selectedTemplate) {
      setExpandedNames(prev => new Set([...prev, selectedTemplate.name]));
    }
  }, [selectedTemplate?.name]);

  function toggleExpand(name: string) {
    setExpandedNames(prev => {
      const next = new Set(prev);
      next.has(name) ? next.delete(name) : next.add(name);
      return next;
    });
  }

  function handleGroupHeaderClick(group: TemplateGroup) {
    const wasExpanded = expandedNames.has(group.name);
    if (!wasExpanded) {
      const active = group.versions.find(t => t.isActive) ?? group.versions[0];
      if (active) onSelect(active);
    }
    toggleExpand(group.name);
  }

  return (
    <aside className="w-64 border-r border-stroke bg-surface flex flex-col">
      <div className="px-4 py-3 border-b border-muted flex items-center justify-between">
        <span className="text-sm font-medium text-fg2">Шаблоны</span>
        <button onClick={onNew} className="text-brand hover:text-brand-hover transition-colors" title="Новый шаблон">
          <Plus size={16} />
        </button>
      </div>
      <TemplateAssetsPanel
        key={`assets-${documentTypeId}`}
        scope="DocumentType" scopeId={documentTypeId} title="Ассеты типа документа"
        hintScopes={[{ scope: 'System', scopeId: null, label: 'системных' }]}
      />
      <div className="flex-1 overflow-auto py-1">
        {groups.length === 0 && (
          <div className="px-4 py-3 text-sm text-fg4">Шаблонов нет</div>
        )}
        {groups.map(group => {
          const isExpanded = expandedNames.has(group.name);
          const hasDefault = group.versions.some(t => t.isDefault);
          const tooMany = group.versions.length >= maxVersions;
          const latestVersion = group.versions[0]?.version ?? 1;
          const isGroupSelected = selectedTemplate?.name === group.name;

          return (
            <div key={group.name}>
              <div className={`flex items-center group/grp transition-colors ${isGroupSelected ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
                <button
                  onClick={() => handleGroupHeaderClick(group)}
                  className="flex-1 flex items-center gap-1.5 px-3 py-2.5 text-left min-w-0"
                >
                  {isExpanded
                    ? <ChevronUp size={12} className="text-fg4 shrink-0" />
                    : <ChevronDown size={12} className="text-fg4 shrink-0" />}
                  {hasDefault && <Star size={10} className="fill-yellow-400 text-yellow-400 shrink-0" />}
                  <span className={`text-sm truncate flex-1 ${isGroupSelected ? 'text-brand-hover font-medium' : 'text-fg1'}`}>
                    {group.name}
                  </span>
                  <span className="text-xs text-fg4 shrink-0">v{latestVersion}</span>
                  {tooMany && (
                    <AlertTriangle size={12} className="text-warning shrink-0" />
                  )}
                </button>
                <button
                  onClick={() => onDuplicate(group)}
                  className="px-2 py-2.5 text-stroke-strong hover:text-brand opacity-0 group-hover/grp:opacity-100 transition-all shrink-0"
                  title="Дублировать шаблон"
                >
                  <Copy size={13} />
                </button>
                <button
                  onClick={() => onDeleteGroup(group)}
                  className="px-2 py-2.5 text-stroke-strong hover:text-danger opacity-0 group-hover/grp:opacity-100 transition-all shrink-0"
                  title="Удалить шаблон со всеми версиями"
                >
                  <Trash2 size={13} />
                </button>
              </div>

              {isExpanded && (
                <div className="border-b border-muted">
                  {group.versions.map(t => (
                    <div key={t.id}
                      className={`flex items-center group/ver transition-colors pl-5
                        ${selectedTemplate?.id === t.id ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
                      <button
                        onClick={() => onSelect(t)}
                        className={`flex-1 flex items-center gap-1.5 px-2 py-1.5 text-left
                          ${selectedTemplate?.id === t.id ? 'text-brand-hover' : 'text-fg2'}`}
                      >
                        <span className="text-xs font-mono shrink-0 w-6">v{t.version}</span>
                        <span className="flex items-center gap-1 flex-1 min-w-0">
                          {t.isActive && <span className="text-xs text-success shrink-0">активный</span>}
                          {t.isDefault && <span className="text-xs text-yellow-600 shrink-0">по умолч.</span>}
                          {t.comment && (
                            <span className="text-xs text-fg4 truncate" title={t.comment}>{t.comment}</span>
                          )}
                        </span>
                      </button>
                      <button
                        onClick={() => onDelete(t)}
                        className="px-2 py-1.5 text-stroke-strong hover:text-danger opacity-0 group-hover/ver:opacity-100 transition-all shrink-0"
                        title="Удалить версию"
                      >
                        <Trash2 size={12} />
                      </button>
                    </div>
                  ))}
                  {tooMany && (
                    <div className="mx-3 mb-2 mt-1 px-2 py-1.5 bg-warning-subtle border border-warning-border rounded-md flex items-center gap-2">
                      <AlertTriangle size={11} className="text-warning shrink-0" />
                      <span className="text-xs text-warning flex-1">Много версий</span>
                      <button
                        onClick={() => onCleanup(group)}
                        className="text-xs text-warning underline hover:text-warning shrink-0"
                      >
                        Очистить
                      </button>
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </aside>
  );
}

