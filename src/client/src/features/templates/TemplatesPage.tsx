import { useState, useEffect } from 'react';
import { Library } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListTemplates, useCreateTemplate, useDeleteTemplate, useDuplicateTemplate } from '@/shared/api/templates';
import type { Template, DocumentType } from '@/shared/api/types';
import { useMaxTemplateVersions } from '@/features/settings/SettingsPage';
import { buildBlankTypst } from './templateBlank';
import { EditorPanel } from './EditorPanel';
import { UserLibPanel } from './UserLibPanel';
import { TemplateSidebar, DocTypeSelector, VersionCleanupModal, groupTemplates, type TemplateGroup } from './TemplateSidebar';
// ─── New template form ────────────────────────────────────────────────────────

interface NewTemplateFormProps {
  documentTypeId: string;
  docType: DocumentType;
  allDocTypes: DocumentType[];
  onClose: () => void;
  onCreated: (t: Template) => void;
}

function NewTemplateForm({ documentTypeId, docType, allDocTypes, onClose, onCreated }: NewTemplateFormProps) {
  const [name, setName] = useState('');
  const [error, setError] = useState('');
  const mutation = useCreateTemplate();

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    try {
      const content = buildBlankTypst(name.trim() || 'Новый шаблон', docType, allDocTypes);
      const t = await mutation.mutateAsync({ documentTypeId, name, content });
      onCreated(t);
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка создания');
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Название шаблона</label>
          <input value={name} onChange={(e) => setName(e.target.value)} required autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
        <p className="text-xs text-fg4">
          Будет создан Typst-шаблон с колонтитулами, нумерацией страниц и списком всех реквизитов типа документа.
        </p>
        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-3">
        <button type="button" onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
        <button type="submit" disabled={mutation.isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
          {mutation.isPending ? 'Создание...' : 'Создать'}
        </button>
      </div>
    </form>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

type PageMode = 'templates' | 'userlib';

export function TemplatesPage() {
  const [mode, setMode] = useState<PageMode>('templates');
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [selectedTemplate, setSelectedTemplate] = useState<Template | null>(null);
  const [newModalOpen, setNewModalOpen] = useState(false);
  const [cleanupGroup, setCleanupGroup] = useState<TemplateGroup | null>(null);
  const [maxVersions] = useMaxTemplateVersions();
  const [deleteTarget, setDeleteTarget] = useState<Template | null>(null);
  const [deleteGroupTarget, setDeleteGroupTarget] = useState<TemplateGroup | null>(null);

  const { data: docTypes = [] } = useListDocumentTypes();
  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(selectedTypeId || undefined);
  const deleteMutation = useDeleteTemplate();
  const duplicateMutation = useDuplicateTemplate();

  const selectedDocType = docTypes.find(dt => dt.id === selectedTypeId) ?? null;
  const groups = groupTemplates(templates);

  useEffect(() => { setSelectedTemplate(null); }, [selectedTypeId]);

  useEffect(() => {
    if (templates.length > 0 && !selectedTemplate) {
      const active = templates.find(t => t.isDefault && t.isActive)
        ?? templates.find(t => t.isActive)
        ?? templates[0];
      setSelectedTemplate(active);
    }
  }, [templates]);

  function handleTypeChange(id: string) {
    setSelectedTypeId(id);
    setSelectedTemplate(null);
  }

  function handleDelete(t: Template) {
    setDeleteTarget(t);
  }

  function confirmDelete() {
    if (!deleteTarget) return;
    if (selectedTemplate?.id === deleteTarget.id) setSelectedTemplate(null);
    deleteMutation.mutate({ id: deleteTarget.id, documentTypeId: deleteTarget.documentTypeId });
  }

  function handleDeleteGroup(group: TemplateGroup) {
    setDeleteGroupTarget(group);
  }

  function confirmDeleteGroup() {
    if (!deleteGroupTarget) return;
    if (selectedTemplate?.name === deleteGroupTarget.name) setSelectedTemplate(null);
    for (const t of deleteGroupTarget.versions) {
      deleteMutation.mutate({ id: t.id, documentTypeId: t.documentTypeId });
    }
  }

  async function handleDuplicateGroup(group: TemplateGroup) {
    // Источник — активная (или последняя) версия группы.
    const source = group.versions.find(t => t.isActive) ?? group.versions[0];
    if (!source) return;
    const created = await duplicateMutation.mutateAsync({ id: source.id, documentTypeId: source.documentTypeId });
    setSelectedTemplate(created);
  }

  function handleCleanupDeleted(deletedIds: Set<string>) {
    if (selectedTemplate && deletedIds.has(selectedTemplate.id)) {
      setSelectedTemplate(null);
    }
  }

  return (
    <div className="flex flex-col h-full">
      {/* ── Header ── */}
      <div className="px-6 py-3 border-b border-stroke bg-surface">
        <div className="flex items-center justify-between gap-4">
          {/* Mode tabs */}
          <div className="flex items-center gap-1 bg-muted rounded-lg p-0.5">
            <button
              onClick={() => setMode('templates')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md transition-colors ${
                mode === 'templates'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              Шаблоны документов
            </button>
            <button
              onClick={() => setMode('userlib')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md transition-colors ${
                mode === 'userlib'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              <Library size={14} />
              Общие функции Typst
            </button>
          </div>

          {/* Doc type selector — only in templates mode */}
          {mode === 'templates' && (
            <DocTypeSelector docTypes={docTypes} selected={selectedTypeId} onSelect={handleTypeChange} />
          )}
        </div>
      </div>

      {/* ── Content ── */}
      {mode === 'userlib' ? (
        <UserLibPanel />
      ) : !selectedTypeId ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Выберите тип документа</div>
      ) : templatesLoading ? (
        <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка...</div>
      ) : (
        <div className="flex flex-1 overflow-hidden">
          <TemplateSidebar
            groups={groups}
            selectedTemplate={selectedTemplate}
            maxVersions={maxVersions}
            documentTypeId={selectedTypeId}
            onSelect={setSelectedTemplate}
            onNew={() => setNewModalOpen(true)}
            onDelete={handleDelete}
            onDeleteGroup={handleDeleteGroup}
            onDuplicate={handleDuplicateGroup}
            onCleanup={setCleanupGroup}
          />

          <div className="flex-1 overflow-hidden">
            {selectedTemplate && selectedDocType ? (
              <EditorPanel
                template={selectedTemplate}
                docType={selectedDocType}
                allDocTypes={docTypes}
                onSaved={(updated) => setSelectedTemplate(updated)}
              />
            ) : (
              <div className="h-full flex items-center justify-center text-fg4 text-sm">
                Выберите шаблон или создайте новый
              </div>
            )}
          </div>
        </div>
      )}

      <Modal open={newModalOpen} onOpenChange={setNewModalOpen} title="Новый шаблон" flushBody>
        {newModalOpen && selectedTypeId && selectedDocType && (
          <NewTemplateForm
            documentTypeId={selectedTypeId}
            docType={selectedDocType}
            allDocTypes={docTypes}
            onClose={() => setNewModalOpen(false)}
            onCreated={(t) => setSelectedTemplate(t)}
          />
        )}
      </Modal>

      {cleanupGroup && (
        <VersionCleanupModal
          group={cleanupGroup}
          onClose={() => setCleanupGroup(null)}
          onDeleted={handleCleanupDeleted}
        />
      )}

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить версию v${deleteTarget?.version ?? ''} шаблона «${deleteTarget?.name ?? ''}»?`}
        confirmLabel="Удалить версию"
        onConfirm={confirmDelete}
      />
      <ConfirmDialog
        open={!!deleteGroupTarget}
        onOpenChange={o => { if (!o) setDeleteGroupTarget(null); }}
        title={`Удалить шаблон «${deleteGroupTarget?.name ?? ''}»?`}
        description={deleteGroupTarget ? <CascadeList items={[ruCount(deleteGroupTarget.versions.length, 'версию', 'версии', 'версий')]} /> : undefined}
        confirmLabel="Удалить шаблон"
        onConfirm={confirmDeleteGroup}
      />
    </div>
  );
}

