import { useState, useEffect, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { Library, Lock, Boxes } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { useToast } from '@/shared/ui/Toast';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListTemplates, useCreateTemplate, useDeleteTemplate, useDuplicateTemplate, useTemplatesUsage } from '@/shared/api/templates';
import type { Template, DocumentType } from '@/shared/api/types';
import { useMaxTemplateVersions } from '@/features/settings/SettingsPage';
import { buildBlankTypst } from './templateBlank';
import { EditorPanel } from './EditorPanel';
import { UserLibPanel } from './UserLibPanel';
import { SystemLibPanel } from './SystemLibPanel';
import { TypeBlocksPanel } from './TypeBlocksPanel';
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
        <TextField label="Название шаблона" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
        <p className="text-xs text-fg4">
          Будет создан Typst-шаблон с колонтитулами, нумерацией страниц и списком всех реквизитов типа документа.
        </p>
        {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-2">
        <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
        <Button type="submit" variant="filled" loading={mutation.isPending}>
          {mutation.isPending ? 'Создание…' : 'Создать'}
        </Button>
      </div>
    </form>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

type PageMode = 'templates' | 'userlib' | 'systemlib' | 'typeblocks';

/**
 * Конвенция (issue #778 → #780): выбор list-detail живёт в URL (replace — стрелки браузера не
 * ходят по кликам в рейле), последний открытый — в localStorage. Здесь выбор двухступенчатый,
 * поэтому в URL два параметра (`?type=&template=`), а в памяти пара `{ typeId, templateId }`:
 * templateId указывает конкретную версию — она и есть единица выбора в рейле.
 *
 * Режим страницы (общие функции / системные / блоки типов) сознательно НЕ восстанавливается:
 * вход всегда в «Шаблоны», иначе возврат в библиотеку, открытую неделю назад, был бы сюрпризом.
 */
const TEMPLATES_LAST_KEY = 'templates-last';

function readLastTemplate(): { typeId: string; templateId: string } {
  try {
    const raw = localStorage.getItem(TEMPLATES_LAST_KEY);
    const v = raw ? JSON.parse(raw) as { typeId?: string; templateId?: string } : null;
    return { typeId: v?.typeId ?? '', templateId: v?.templateId ?? '' };
  } catch { return { typeId: '', templateId: '' }; }
}

export function TemplatesPage() {
  const [mode, setMode] = useState<PageMode>('templates');

  // Порядок разрешения при входе: `?type=&template=` → localStorage → пусто (прежнее поведение).
  // Читаем один раз: дальше состояние ведёт страница, а URL и память лишь зеркалят выбор.
  const [searchParams, setSearchParams] = useSearchParams();
  const [restored] = useState(() => {
    const urlType = searchParams.get('type') ?? '';
    return urlType
      ? { typeId: urlType, templateId: searchParams.get('template') ?? '' }
      : readLastTemplate();
  });

  const [pickedTypeId, setPickedTypeId] = useState(restored.typeId);
  const [selectedTemplate, setSelectedTemplate] = useState<Template | null>(null);
  // Восстанавливаемая версия ждёт здесь загрузки списка: сброс шаблона при смене типа её не трёт.
  const pendingTemplateId = useRef(restored.templateId);
  const [newModalOpen, setNewModalOpen] = useState(false);
  const [cleanupGroup, setCleanupGroup] = useState<TemplateGroup | null>(null);
  const [maxVersions] = useMaxTemplateVersions();
  const [deleteTarget, setDeleteTarget] = useState<Template | null>(null);
  const [deleteGroupTarget, setDeleteGroupTarget] = useState<TemplateGroup | null>(null);

  const { data: docTypes = [] } = useListDocumentTypes();

  // Тип из памяти или из ссылки мог быть удалён, стать абстрактным или сменить kind — селектор
  // такой не показывает. Поэтому выбранным считаем только пригодный тип: fallback молчаливый
  // («Выберите тип документа»), и запрос за шаблонами несуществующего типа не уходит.
  const selectedDocType = docTypes.find(dt => dt.id === pickedTypeId && dt.kind === 'Document' && !dt.isAbstract) ?? null;
  const selectedTypeId = selectedDocType?.id ?? '';

  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(selectedTypeId || undefined);
  const { data: usage = {} } = useTemplatesUsage(selectedTypeId || undefined);
  const deleteMutation = useDeleteTemplate();
  const duplicateMutation = useDuplicateTemplate();
  const toast = useToast();

  const groups = groupTemplates(templates);

  // Заголовок вкладки: библиотека / выбранный шаблон / просматриваемый тип замещают раздел.
  useDocumentTitle(
    mode === 'systemlib' ? 'Системная библиотека Typst'
    : mode === 'typeblocks' ? 'Блоки типов Typst'
    : mode === 'userlib' ? 'Библиотека Typst'
    : selectedTemplate ? `Шаблон «${selectedTemplate.name}»`
    : selectedDocType ? selectedDocType.name
    : null);

  // Зеркалим выбор в URL и в память. Авто-выбор версии (ниже) сюда не попадает — он
  // детерминированно повторяется при следующем входе, а память хранит осознанный выбор.
  const persist = (typeId: string, templateId: string) => {
    localStorage.setItem(TEMPLATES_LAST_KEY, JSON.stringify({ typeId, templateId }));
    setSearchParams(prev => {
      const next = new URLSearchParams(prev);
      if (typeId) next.set('type', typeId); else next.delete('type');
      if (templateId) next.set('template', templateId); else next.delete('template');
      return next;
    }, { replace: true });
  };

  const selectTemplate = (t: Template | null) => {
    setSelectedTemplate(t);
    persist(t?.documentTypeId ?? selectedTypeId, t?.id ?? '');
  };

  useEffect(() => { setSelectedTemplate(null); }, [selectedTypeId]);

  useEffect(() => {
    if (templates.length > 0 && !selectedTemplate) {
      // Восстановленная версия — только на первый показ списка; дальше обычный авто-выбор.
      // Версию могли вычистить или удалить вместе с шаблоном — тогда fallback молчаливый.
      const wanted = pendingTemplateId.current
        ? templates.find(t => t.id === pendingTemplateId.current)
        : undefined;
      pendingTemplateId.current = '';
      const active = wanted
        ?? templates.find(t => t.isDefault && t.isActive)
        ?? templates.find(t => t.isActive)
        ?? templates[0];
      setSelectedTemplate(active);
    }
  }, [templates]);

  function handleTypeChange(id: string) {
    setPickedTypeId(id);
    setSelectedTemplate(null);
    pendingTemplateId.current = '';
    persist(id, '');
  }

  function handleDelete(t: Template) {
    setDeleteTarget(t);
  }

  async function confirmDelete() {
    if (!deleteTarget) return;
    const target = deleteTarget;
    if (selectedTemplate?.id === target.id) selectTemplate(null);
    // Если версия запиннута — сбрасываем документы на дефолт (reassign). Иначе обычное удаление.
    const reassign = (usage[target.id]?.count ?? 0) > 0;
    try {
      await deleteMutation.mutateAsync({ id: target.id, documentTypeId: target.documentTypeId, reassign });
      if (reassign) toast.info(`${ruCount(usage[target.id].count, 'документ', 'документа', 'документов')} переведено в черновик — вернулись на шаблон по умолчанию`);
    } catch (err: unknown) {
      toast.error(err instanceof Error ? err.message : 'Не удалось удалить версию');
    }
  }

  function handleDeleteGroup(group: TemplateGroup) {
    setDeleteGroupTarget(group);
  }

  async function confirmDeleteGroup() {
    if (!deleteGroupTarget) return;
    if (selectedTemplate?.name === deleteGroupTarget.name) selectTemplate(null);
    // Удаление всего шаблона: снимаем пины у всех документов (→ дефолт) и удаляем все версии.
    try {
      for (const t of deleteGroupTarget.versions)
        await deleteMutation.mutateAsync({ id: t.id, documentTypeId: t.documentTypeId, reassign: true });
    } catch (err: unknown) {
      toast.error(err instanceof Error ? err.message : 'Не удалось удалить шаблон');
    }
  }

  async function handleDuplicateGroup(group: TemplateGroup) {
    // Источник — активная (или последняя) версия группы.
    const source = group.versions.find(t => t.isActive) ?? group.versions[0];
    if (!source) return;
    const created = await duplicateMutation.mutateAsync({ id: source.id, documentTypeId: source.documentTypeId });
    selectTemplate(created);
  }

  function handleCleanupDeleted(deletedIds: Set<string>) {
    if (selectedTemplate && deletedIds.has(selectedTemplate.id)) {
      selectTemplate(null);
    }
  }

  return (
    <div className="flex flex-col h-full">
      {/* ── Header ── */}
      <div className="px-6 py-3 border-b border-stroke bg-surface">
        <div className="flex items-center justify-between gap-4">
          {/* Mode tabs */}
          <div className="flex items-center gap-1 bg-muted rounded-lg p-0.5 shrink-0">
            <button
              onClick={() => setMode('templates')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md whitespace-nowrap transition-colors ${
                mode === 'templates'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              Шаблоны документов
            </button>
            <button
              onClick={() => setMode('userlib')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md whitespace-nowrap transition-colors ${
                mode === 'userlib'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              <Library size={14} className="shrink-0" />
              Общие функции Typst
            </button>
            <button
              onClick={() => setMode('systemlib')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md whitespace-nowrap transition-colors ${
                mode === 'systemlib'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              <Lock size={14} className="shrink-0" />
              Системные функции
            </button>
            {/* Собранный typeblocks.typ (issue #770): рядом с двумя другими библиотеками, потому что
                шаблон видит все три одинаково — импортом. Только чтение, как и системные функции. */}
            <button
              onClick={() => setMode('typeblocks')}
              className={`flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-md whitespace-nowrap transition-colors ${
                mode === 'typeblocks'
                  ? 'bg-surface text-fg1 font-medium shadow-sm'
                  : 'text-fg3 hover:text-fg2'
              }`}
            >
              <Boxes size={14} className="shrink-0" />
              Блоки типов
            </button>
          </div>

          {/* Doc type selector — only in templates mode */}
          {mode === 'templates' && (
            <DocTypeSelector docTypes={docTypes} selected={selectedTypeId} onSelect={handleTypeChange} />
          )}
        </div>
      </div>

      {/* ── Content ── */}
      {mode === 'systemlib' ? (
        <div className="flex-1 min-h-0 overflow-hidden">
          <SystemLibPanel />
        </div>
      ) : mode === 'typeblocks' ? (
        <div className="flex-1 min-h-0 overflow-hidden">
          <TypeBlocksPanel />
        </div>
      ) : mode === 'userlib' ? (
        <div className="flex-1 min-h-0 overflow-hidden">
          <UserLibPanel />
        </div>
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
            usage={usage}
            onSelect={selectTemplate}
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
                onSaved={(updated) => selectTemplate(updated)}
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
            onCreated={(t) => selectTemplate(t)}
          />
        )}
      </Modal>

      {cleanupGroup && (
        <VersionCleanupModal
          group={cleanupGroup}
          usage={usage}
          onClose={() => setCleanupGroup(null)}
          onDeleted={handleCleanupDeleted}
        />
      )}

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить версию v${deleteTarget?.version ?? ''} шаблона «${deleteTarget?.name ?? ''}»?`}
        description={deleteTarget && (usage[deleteTarget.id]?.count ?? 0) > 0 ? (
          <div className="space-y-2">
            <p>
              Версия используется в <strong>{ruCount(usage[deleteTarget.id].count, 'документе', 'документах', 'документах')}</strong>.
              Они вернутся на шаблон по умолчанию и станут черновиками — PDF придётся перегенерировать. Необратимо.
            </p>
            <CascadeList items={[
              ...usage[deleteTarget.id].names,
              ...(usage[deleteTarget.id].count > usage[deleteTarget.id].names.length
                ? [`…и ещё ${usage[deleteTarget.id].count - usage[deleteTarget.id].names.length}`] : []),
            ]} />
          </div>
        ) : undefined}
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

