import { useState, useEffect, useRef } from 'react';
import { Routes, Route, useNavigate, useParams, useSearchParams, Link } from 'react-router-dom';
import {
  Plus, Trash2, ChevronRight, Download, Pencil, ChevronDown, ChevronUp, FolderOpen, Eye,
  ArrowUp, ArrowDown, Layers, Loader2, Search, X,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import {
  useListConstructions, useGetConstruction, useCreateConstruction, useRenameConstruction,
  useDeleteConstruction, useCreateSection, useRenameSection, useDeleteSection,
  useCreateDocumentSet, useRenameDocumentSet, useDeleteDocumentSet,
} from '@/shared/api/constructions';
import {
  useGetDocumentSet, useGetAvailableInstances, useAddDocumentToSet, useDeleteDocumentInstance,
  useReorderInstances, useAssembleSet, useDocumentSetOutput, downloadSetOutput,
  useSearchDocuments, downloadGeneratedFile, previewGeneratedFile,
} from '@/shared/api/documentSets';
import type { Construction, Section, DocumentSet, DocumentInstance, DocumentType } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from './fields';
import { InstanceEditor } from './editor';
import { ScopedCatalogPanel } from './catalog';
import { ScopedDataSetsPanel } from '@/features/datasets/ScopedDataSetsPanel';

// ─── Set detail (documents) ───────────────────────────────────────────────────

function SetDetail() {
  const { constructionId, setId } = useParams<{ constructionId: string; setId: string }>();
  const { data: set, isLoading } = useGetDocumentSet(setId);
  const { data: construction } = useGetConstruction(constructionId!);
  const { data: availableInstances = [] } = useGetAvailableInstances(setId);
  const { data: docTypes = [] } = useListDocumentTypes();
  const [addDocOpen, setAddDocOpen] = useState(false);
  const [editInstance, setEditInstance] = useState<DocumentInstance | null>(null);
  const [editDirty, setEditDirty] = useState(false);
  const addMutation = useAddDocumentToSet();
  const deleteMutation = useDeleteDocumentInstance();
  const reorderMutation = useReorderInstances();
  const assembleMutation = useAssembleSet();
  const [addTypeId, setAddTypeId] = useState('');
  const [addError, setAddError] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<DocumentInstance | null>(null);
  // Слежение за сборкой: пока идёт задача — опрашиваем вывод; останавливаемся, когда generatedAt изменится.
  const [watching, setWatching] = useState(false);
  const [assembleMsg, setAssembleMsg] = useState('');
  const watchStartRef = useRef<string | undefined>(undefined);
  const { data: output } = useDocumentSetOutput(setId, watching ? 2500 : false);

  useEffect(() => {
    if (watching && output && output.generatedAt !== watchStartRef.current) {
      setWatching(false);
      setAssembleMsg('Комплект собран — можно скачать.');
    }
  }, [watching, output]);

  // Deep-link из результатов поиска: ?doc={instanceId} авто-открывает документ (один раз, затем чистим).
  const [searchParams, setSearchParams] = useSearchParams();
  const docParam = searchParams.get('doc');
  useEffect(() => {
    if (!docParam || !set) return;
    const found = set.instances.find(i => i.id === docParam);
    if (found) {
      setEditInstance(found);
      setSearchParams(prev => { prev.delete('doc'); return prev; }, { replace: true });
    }
  }, [docParam, set, setSearchParams]);

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!set) return <div className="p-6 text-sm text-danger">Комплект не найден</div>;

  async function handleAssemble() {
    setAssembleMsg('');
    watchStartRef.current = output?.generatedAt;
    try {
      await assembleMutation.mutateAsync({ setId: set!.id });
      setWatching(true);
      setAssembleMsg('Сборка запущена — прогресс в индикаторе задач слева от колокольчика.');
      setTimeout(() => setWatching(false), 5 * 60 * 1000); // страховка: не опрашивать вечно
    } catch (err: unknown) {
      setAssembleMsg(err instanceof Error ? err.message : 'Ошибка запуска сборки');
    }
  }

  function moveDoc(index: number, dir: -1 | 1) {
    const ids = set!.instances.map(i => i.id);
    const j = index + dir;
    if (j < 0 || j >= ids.length) return;
    [ids[index], ids[j]] = [ids[j], ids[index]];
    reorderMutation.mutate({ setId: set!.id, orderedIds: ids });
  }

  async function handleAddDoc(e: React.FormEvent) {
    e.preventDefault();
    setAddError('');
    if (!addTypeId) return;
    try {
      await addMutation.mutateAsync({ setId: set!.id, documentTypeId: addTypeId });
      setAddDocOpen(false);
      setAddTypeId('');
    } catch (err: unknown) { setAddError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  const docTypeMap = Object.fromEntries(docTypes.map(dt => [dt.id, dt]));
  const documentKindTypes = docTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const otherInstances = editInstance
    ? availableInstances.filter(i => i.id !== editInstance.id)
    : availableInstances;
  const sectionName = construction?.sections.find(s => s.id === set.sectionId)?.name;

  return (
    <div className="p-6">
      <nav className="flex items-center gap-1 text-sm text-fg4 mb-5">
        <Link to="/document-sets" className="hover:text-fg2 transition-colors">Стройки</Link>
        <ChevronRight size={14} />
        <Link to={`/document-sets/${constructionId}`} className="hover:text-fg2 transition-colors">{construction?.name ?? 'Разделы и комплекты'}</Link>
        {sectionName && (
          <>
            <ChevronRight size={14} />
            <span className="text-fg4">{sectionName}</span>
          </>
        )}
        <ChevronRight size={14} />
        <span className="text-fg2 font-medium">{set.name}</span>
      </nav>

      <div className="flex items-center justify-between mb-1 gap-3 flex-wrap">
        <h1 className="text-xl font-semibold text-fg1">{set.name}</h1>
        <div className="flex items-center gap-2">
          {output && (
            <button onClick={() => downloadSetOutput(set.id, set.name)}
              className="flex items-center gap-2 border border-stroke hover:bg-base text-fg2 text-sm font-medium px-3 py-2 rounded-md transition-colors"
              title={`Собран ${new Date(output.generatedAt).toLocaleString('ru-RU')}`}>
              <Download size={15} className="text-brand" /> Скачать комплект
            </button>
          )}
          <button onClick={handleAssemble} disabled={assembleMutation.isPending || watching || set.instances.length === 0}
            className="flex items-center gap-2 border border-brand text-brand-hover hover:bg-brand-subtle text-sm font-medium px-3 py-2 rounded-md transition-colors disabled:opacity-50"
            title="Собрать все документы комплекта в один PDF в заданном порядке">
            {assembleMutation.isPending || watching ? <Loader2 size={15} className="animate-spin" /> : <Layers size={15} />}
            Собрать комплект
          </button>
          <button onClick={() => setAddDocOpen(true)}
            className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
            <Plus size={16} /> Добавить документ
          </button>
        </div>
      </div>
      {assembleMsg && <p className="text-xs text-fg4 mb-3">{assembleMsg}</p>}
      {!assembleMsg && <div className="mb-3" />}

      <div className="bg-surface border border-stroke rounded-xl overflow-hidden">
        {set.instances.length === 0 ? (
          <div className="p-10 text-center text-fg4 text-sm">Документов нет</div>
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-base border-b border-stroke">
              <tr>
                <th className="px-2 py-3 w-12" title="Порядок в собранном комплекте" />
                <th className="text-left px-4 py-3 font-medium text-fg2">Документ</th>
                <th className="text-left px-4 py-3 font-medium text-fg2 w-32">Статус</th>
                <th className="px-4 py-3 w-36" />
                <th className="px-4 py-3 w-10" />
              </tr>
            </thead>
            <tbody>
              {set.instances.map((inst, index) => {
                const pdfFiles = inst.generatedFiles.filter(f => f.format === 'Pdf');
                return (
                  <tr key={inst.id} className="border-b border-muted last:border-0 hover:bg-base cursor-pointer group"
                    onClick={() => setEditInstance(inst)}>
                    <td className="px-2 py-3" onClick={e => e.stopPropagation()}>
                      <div className="flex flex-col items-center -my-1">
                        <button onClick={() => moveDoc(index, -1)} disabled={index === 0 || reorderMutation.isPending}
                          className="p-0.5 text-fg4 hover:text-brand disabled:opacity-25 disabled:hover:text-fg4 transition-colors" title="Выше">
                          <ArrowUp size={13} />
                        </button>
                        <button onClick={() => moveDoc(index, 1)} disabled={index === set.instances.length - 1 || reorderMutation.isPending}
                          className="p-0.5 text-fg4 hover:text-brand disabled:opacity-25 disabled:hover:text-fg4 transition-colors" title="Ниже">
                          <ArrowDown size={13} />
                        </button>
                      </div>
                    </td>
                    <td className="px-4 py-3">
                      <div className="font-medium text-fg1">
                        {inst.name || docTypeMap[inst.documentTypeId]?.name || inst.documentTypeId}
                      </div>
                      {inst.name && (
                        <div className="text-xs text-fg4 mt-0.5">
                          {docTypeMap[inst.documentTypeId]?.name}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`text-xs px-2 py-0.5 rounded font-medium ${STATUS_COLORS[inst.status] ?? 'bg-muted text-fg2'}`}>
                        {STATUS_LABELS[inst.status] ?? inst.status}
                      </span>
                    </td>
                    <td className="px-4 py-3" onClick={e => e.stopPropagation()}>
                      <div className="flex items-center gap-1">
                        {pdfFiles.length === 1 && (
                          <>
                            <button onClick={() => previewGeneratedFile(inst.id, pdfFiles[0].templateId)}
                              className="flex items-center gap-1 px-2 py-1 text-xs border border-stroke rounded hover:bg-brand-subtle hover:border-brand-subtle text-fg2 hover:text-brand-hover transition-colors"
                              title="Открыть PDF">
                              <Eye size={11} /> PDF
                            </button>
                            <button onClick={() => downloadGeneratedFile(inst.id, pdfFiles[0].templateId)}
                              className="p-1 text-fg4 hover:text-brand transition-colors"
                              title="Скачать PDF">
                              <Download size={12} />
                            </button>
                          </>
                        )}
                        {pdfFiles.length > 1 && (
                          <button onClick={() => setEditInstance(inst)}
                            className="flex items-center gap-1 px-2 py-1 text-xs border border-stroke rounded hover:bg-brand-subtle hover:border-brand-subtle text-fg2 hover:text-brand-hover transition-colors"
                            title="Несколько PDF — открыть документ для выбора">
                            <Eye size={11} /> {pdfFiles.length} PDF
                          </button>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-right">
                      <button
                        onClick={e => { e.stopPropagation(); setDeleteTarget(inst); }}
                        disabled={deleteMutation.isPending}
                        className="p-1 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 transition-all disabled:opacity-30"
                        title="Удалить документ">
                        <Trash2 size={14} />
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {setId && (
        <div className="mt-6 space-y-4">
          <ScopedCatalogPanel scope="Set" scopeId={setId} allDocTypes={docTypes} setId={setId} />
          <ScopedDataSetsPanel scope="Set" scopeId={setId} />
        </div>
      )}

      <Modal open={addDocOpen} onOpenChange={setAddDocOpen} title="Добавить документ"
        footer={
          <div className="flex justify-end gap-3">
            <button type="button" onClick={() => setAddDocOpen(false)}
              className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="submit" form="add-doc-form" disabled={addMutation.isPending}
              className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {addMutation.isPending ? 'Добавление...' : 'Добавить'}
            </button>
          </div>
        }>
        {addDocOpen && (
          <form id="add-doc-form" onSubmit={handleAddDoc} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-fg2 mb-1">Тип документа</label>
              <select value={addTypeId} onChange={e => setAddTypeId(e.target.value)} required
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
                <option value="">Выберите тип...</option>
                {documentKindTypes.map(dt => <option key={dt.id} value={dt.id}>{dt.name}</option>)}
              </select>
            </div>
            {addError && <p className="text-sm text-danger">{addError}</p>}
          </form>
        )}
      </Modal>

      {editInstance && setId && (() => {
        const liveInstance = set.instances.find(i => i.id === editInstance.id) ?? editInstance;
        return (
          <Modal open={!!editInstance} onOpenChange={open => { if (!open) { setEditInstance(null); setEditDirty(false); } }}
            title="Редактировать документ" wide isDirty={editDirty} flushBody>
            <InstanceEditor key={liveInstance.id} instance={liveInstance} setId={setId} docType={docTypeMap[liveInstance.documentTypeId]}
              allDocTypes={docTypes} otherInstances={otherInstances}
              onClose={() => { setEditInstance(null); setEditDirty(false); }} onDirtyChange={setEditDirty} />
          </Modal>
        );
      })()}

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить документ «${deleteTarget ? (deleteTarget.name || docTypeMap[deleteTarget.documentTypeId]?.name || deleteTarget.documentTypeId) : ''}»?`}
        confirmLabel="Удалить документ"
        onConfirm={() => {
          if (!deleteTarget) return;
          if (editInstance?.id === deleteTarget.id) setEditInstance(null);
          deleteMutation.mutate({ setId: set.id, instanceId: deleteTarget.id });
        }}
      />
    </div>
  );
}

// ─── Construction detail (sections + sets) ────────────────────────────────────

function SectionCard({ section, construction, expanded, onToggle, allDocTypes }: {
  section: Section; construction: Construction; expanded: boolean; onToggle: () => void; allDocTypes: DocumentType[];
}) {
  const navigate = useNavigate();
  const [addSetOpen, setAddSetOpen] = useState(false);
  const [newSetName, setNewSetName] = useState('');
  const [editName, setEditName] = useState(false);
  const [nameVal, setNameVal] = useState(section.name);
  const [addError, setAddError] = useState('');

  const createSet = useCreateDocumentSet();
  const renameSection = useRenameSection();
  const deleteSection = useDeleteSection();
  const renameSet = useRenameDocumentSet();
  const deleteSet = useDeleteDocumentSet();
  const [deleteSectionConfirm, setDeleteSectionConfirm] = useState(false);
  const [deleteSetTarget, setDeleteSetTarget] = useState<DocumentSet | null>(null);

  const setsInSection = section.documentSets.length;
  const docsInSection = section.documentSets.reduce((acc, ds) => acc + (ds.instances?.length ?? 0), 0);

  async function handleAddSet(e: React.FormEvent) {
    e.preventDefault();
    setAddError('');
    try {
      await createSet.mutateAsync({ sectionId: section.id, name: newSetName, constructionId: construction.id });
      setAddSetOpen(false);
      setNewSetName('');
    } catch (err: unknown) { setAddError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  async function handleRenameSection() {
    if (!nameVal.trim() || nameVal === section.name) { setEditName(false); return; }
    await renameSection.mutateAsync({ id: section.id, name: nameVal });
    setEditName(false);
  }

  return (
    <div className="border border-stroke rounded-xl overflow-hidden">
      <div className="flex items-center bg-surface px-4 py-3 gap-2">
        <button onClick={onToggle} className="flex-1 flex items-center gap-3 text-left">
          {expanded ? <ChevronUp size={16} className="text-fg4 shrink-0" /> : <ChevronDown size={16} className="text-fg4 shrink-0" />}
          {editName ? (
            <input value={nameVal} onChange={e => setNameVal(e.target.value)}
              onBlur={handleRenameSection} onKeyDown={e => { if (e.key === 'Enter') handleRenameSection(); if (e.key === 'Escape') { setNameVal(section.name); setEditName(false); } }}
              autoFocus onClick={e => e.stopPropagation()}
              className="text-sm font-medium border-b border-brand bg-transparent outline-none flex-1" />
          ) : (
            <span className="text-sm font-medium text-fg1">{section.name}</span>
          )}
          <span className="text-xs text-fg4 ml-1">
            {ruCount(section.documentSets.length, 'комплект', 'комплекта', 'комплектов')}
          </span>
        </button>
        <button onClick={() => setEditName(true)} className="p-1 text-stroke-strong hover:text-fg2 transition-colors" title="Переименовать">
          <Pencil size={13} />
        </button>
        <button onClick={() => setDeleteSectionConfirm(true)}
          className="p-1 text-stroke-strong hover:text-danger transition-colors" title="Удалить раздел">
          <Trash2 size={13} />
        </button>
      </div>

      {expanded && (
        <div className="border-t border-muted bg-base px-4 py-3 space-y-2">
          {section.documentSets.length === 0 && (
            <p className="text-xs text-fg4 py-1">Нет комплектов</p>
          )}
          {section.documentSets.map(ds => (
            <DocumentSetRow key={ds.id} ds={ds} section={section} construction={construction}
              onOpen={() => navigate(`/document-sets/${construction.id}/sets/${ds.id}`)}
              onRename={(name) => renameSet.mutateAsync({ id: ds.id, name, constructionId: construction.id })}
              onDelete={() => setDeleteSetTarget(ds)}
            />
          ))}
          <button onClick={() => setAddSetOpen(true)}
            className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors mt-1">
            <Plus size={14} /> Добавить комплект
          </button>
          <ScopedCatalogPanel scope="Section" scopeId={section.id} allDocTypes={allDocTypes} />
          <ScopedDataSetsPanel scope="Section" scopeId={section.id} />
        </div>
      )}

      <Modal open={addSetOpen} onOpenChange={setAddSetOpen} title="Новый комплект"
        footer={
          <div className="flex justify-end gap-3">
            <button type="button" onClick={() => setAddSetOpen(false)} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="submit" form="add-set-form" disabled={createSet.isPending} className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {createSet.isPending ? 'Создание...' : 'Создать'}
            </button>
          </div>
        }>
        {addSetOpen && (
          <form id="add-set-form" onSubmit={handleAddSet} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-fg2 mb-1">Наименование</label>
              <input value={newSetName} onChange={e => setNewSetName(e.target.value)} required autoFocus
                placeholder="например: Кабельный журнал, секция 2"
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
            </div>
            {addError && <p className="text-sm text-danger">{addError}</p>}
          </form>
        )}
      </Modal>

      <ConfirmDialog
        open={deleteSectionConfirm}
        onOpenChange={setDeleteSectionConfirm}
        title={`Удалить раздел «${section.name}»?`}
        description={
          setsInSection > 0 ? (
            <>
              <p>Вместе с ним будут безвозвратно удалены:</p>
              <CascadeList items={[
                ruCount(setsInSection, 'комплект', 'комплекта', 'комплектов'),
                ...(docsInSection > 0 ? [`${ruCount(docsInSection, 'документ', 'документа', 'документов')} (и их сгенерированные PDF)`] : []),
              ]} />
            </>
          ) : undefined
        }
        confirmLabel={`Удалить раздел «${section.name}»`}
        requireCheckbox={setsInSection > 0 ? 'Понимаю, что это необратимо' : undefined}
        onConfirm={() => deleteSection.mutate({ id: section.id, constructionId: construction.id })}
      />

      <ConfirmDialog
        open={!!deleteSetTarget}
        onOpenChange={o => { if (!o) setDeleteSetTarget(null); }}
        title={`Удалить комплект «${deleteSetTarget?.name ?? ''}»?`}
        description={
          deleteSetTarget && deleteSetTarget.instances.length > 0
            ? <CascadeList items={[`${ruCount(deleteSetTarget.instances.length, 'документ', 'документа', 'документов')} (и их сгенерированные PDF)`]} />
            : undefined
        }
        confirmLabel="Удалить комплект"
        onConfirm={() => { if (deleteSetTarget) deleteSet.mutate({ id: deleteSetTarget.id, constructionId: construction.id }); }}
      />
    </div>
  );
}

function DocumentSetRow({ ds, section: _section, construction: _construction, onOpen, onRename, onDelete }: {
  ds: DocumentSet; section: Section; construction: Construction;
  onOpen: () => void; onRename: (name: string) => void; onDelete: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [val, setVal] = useState(ds.name);

  function commitRename() {
    if (!val.trim() || val === ds.name) { setEditing(false); return; }
    onRename(val);
    setEditing(false);
  }

  return (
    <div className="flex items-center gap-2 bg-surface border border-stroke rounded-lg px-3 py-2 group hover:border-brand-subtle hover:bg-brand-subtle/40 transition-colors">
      <FolderOpen size={14} className="text-brand shrink-0" />
      {editing ? (
        <input value={val} onChange={e => setVal(e.target.value)}
          onBlur={commitRename} onKeyDown={e => { if (e.key === 'Enter') commitRename(); if (e.key === 'Escape') { setVal(ds.name); setEditing(false); } }}
          autoFocus className="flex-1 text-sm border-b border-brand bg-transparent outline-none" />
      ) : (
        <button onClick={onOpen} className="flex-1 text-left text-sm font-medium text-fg1 hover:text-brand-hover transition-colors">
          {ds.name}
        </button>
      )}
      <span className="text-xs text-fg4 shrink-0">{ds.instances?.length ?? 0} doc</span>
      <button onClick={() => setEditing(true)} className="p-1 text-stroke-strong hover:text-fg2 opacity-0 group-hover:opacity-100 transition-all" title="Переименовать">
        <Pencil size={12} />
      </button>
      <button onClick={onDelete} className="p-1 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 transition-all" title="Удалить">
        <Trash2 size={12} />
      </button>
      <ChevronRight size={13} className="text-stroke-strong shrink-0" />
    </div>
  );
}

function ConstructionDetail() {
  const { constructionId } = useParams<{ constructionId: string }>();
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  const { data: docTypes = [] } = useListDocumentTypes();
  const [expandedSections, setExpandedSections] = useState<Set<string>>(new Set());
  const [addSectionOpen, setAddSectionOpen] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [sectionError, setSectionError] = useState('');
  const createSection = useCreateSection();

  function toggleSection(id: string) {
    setExpandedSections(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  async function handleAddSection(e: React.FormEvent) {
    e.preventDefault();
    setSectionError('');
    try {
      const s = await createSection.mutateAsync({ constructionId: constructionId!, name: newSectionName });
      setAddSectionOpen(false);
      setNewSectionName('');
      setExpandedSections(prev => new Set([...prev, s.id]));
    } catch (err: unknown) { setSectionError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-sm text-danger">Стройка не найдена</div>;

  return (
    <div className="p-6">
      <nav className="flex items-center gap-1 text-sm text-fg4 mb-5">
        <Link to="/document-sets" className="hover:text-fg2 transition-colors">Стройки</Link>
        <ChevronRight size={14} />
        <span className="text-fg2 font-medium">{construction.name}</span>
      </nav>

      <div className="flex items-center justify-between mb-5">
        <h1 className="text-xl font-semibold text-fg1">{construction.name}</h1>
        <button onClick={() => setAddSectionOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> Добавить раздел
        </button>
      </div>

      {construction.sections.length === 0 ? (
        <div className="text-center py-12 text-fg4 text-sm">Нет разделов. Добавьте первый раздел (дисциплину).</div>
      ) : (
        <div className="space-y-3">
          {construction.sections.map(s => (
            <SectionCard key={s.id} section={s} construction={construction}
              expanded={expandedSections.has(s.id)} onToggle={() => toggleSection(s.id)}
              allDocTypes={docTypes} />
          ))}
        </div>
      )}

      {/* Блок уровня «Стройка» — визуально обособлен от списка разделов (иначе читается как ещё один раздел). */}
      <div className="mt-6 pt-5 border-t border-stroke space-y-3">
        <h2 className="text-xs font-semibold uppercase tracking-wide text-fg4">Общие для стройки</h2>
        <ScopedCatalogPanel scope="Construction" scopeId={constructionId!} allDocTypes={docTypes} />
        <ScopedDataSetsPanel scope="Construction" scopeId={constructionId!} />
      </div>

      <Modal open={addSectionOpen} onOpenChange={setAddSectionOpen} title="Новый раздел"
        footer={
          <div className="flex justify-end gap-3">
            <button type="button" onClick={() => setAddSectionOpen(false)} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="submit" form="add-section-form" disabled={createSection.isPending} className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {createSection.isPending ? 'Создание...' : 'Создать'}
            </button>
          </div>
        }>
        {addSectionOpen && (
          <form id="add-section-form" onSubmit={handleAddSection} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-fg2 mb-1">Название раздела (дисциплина)</label>
              <input value={newSectionName} onChange={e => setNewSectionName(e.target.value)} required autoFocus
                placeholder="например: Электроснабжение, Слаботочные системы"
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
            </div>
            {sectionError && <p className="text-sm text-danger">{sectionError}</p>}
          </form>
        )}
      </Modal>
    </div>
  );
}

// ─── Document search (across kits) ─────────────────────────────────────────────

function DocumentSearchPanel() {
  const navigate = useNavigate();
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  useEffect(() => { const t = setTimeout(() => setQ(text), 300); return () => clearTimeout(t); }, [text]);
  const { data: results = [], isFetching } = useSearchDocuments(q);
  const active = q.trim().length > 0;

  return (
    <div className="mb-5">
      <div className="relative">
        <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
        <input value={text} onChange={e => setText(e.target.value)}
          placeholder="Найти документ по всем комплектам (имя, тип, реквизиты)…"
          className="w-full pl-9 pr-8 py-2 text-sm border border-stroke-strong rounded-md bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        {text && (
          <button onClick={() => setText('')} title="Очистить"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-fg4 hover:text-fg2 transition-colors">
            <X size={14} />
          </button>
        )}
      </div>
      {active && (
        <div className="mt-2 border border-stroke rounded-lg overflow-hidden bg-surface">
          {isFetching && results.length === 0 ? (
            <div className="px-3 py-3 text-xs text-fg4">Поиск…</div>
          ) : results.length === 0 ? (
            <div className="px-3 py-3 text-xs text-fg4">Ничего не найдено по «{q.trim()}»</div>
          ) : (
            <ul className="divide-y divide-muted max-h-96 overflow-y-auto">
              {results.map(r => (
                <li key={r.instanceId}>
                  <button onClick={() => navigate(`/document-sets/${r.constructionId}/sets/${r.setId}?doc=${r.instanceId}`)}
                    className="w-full text-left px-3 py-2 hover:bg-base transition-colors flex items-center gap-3">
                    <div className="min-w-0 flex-1">
                      <div className="text-sm text-fg1 truncate">{r.name || r.typeName}</div>
                      <div className="text-xs text-fg4 truncate">
                        {r.constructionName} › {r.sectionName} › {r.setName}
                        {r.name && <span> · {r.typeName}</span>}
                      </div>
                    </div>
                    <span className={`text-xs px-2 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[r.status] ?? 'bg-muted text-fg2'}`}>
                      {STATUS_LABELS[r.status] ?? r.status}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

// ─── Constructions list ───────────────────────────────────────────────────────

function ConstructionsList() {
  const navigate = useNavigate();
  const [createOpen, setCreateOpen] = useState(false);
  const [newName, setNewName] = useState('');
  const [createError, setCreateError] = useState('');

  const { data: constructions = [], isLoading } = useListConstructions();
  const createMutation = useCreateConstruction();
  const deleteMutation = useDeleteConstruction();
  const renameMutation = useRenameConstruction();
  const [editId, setEditId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<Construction | null>(null);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setCreateError('');
    try {
      const c = await createMutation.mutateAsync(newName);
      setCreateOpen(false);
      setNewName('');
      navigate(`/document-sets/${c.id}`);
    } catch (err: unknown) { setCreateError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  async function commitRename(c: Construction) {
    if (!editName.trim() || editName === c.name) { setEditId(null); return; }
    await renameMutation.mutateAsync({ id: c.id, name: editName });
    setEditId(null);
  }

  return (
    <div className="px-6 py-4">
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold text-fg1">Стройки</h1>
        <button onClick={() => setCreateOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> Новая стройка
        </button>
      </div>

      <DocumentSearchPanel />

      {isLoading ? (
        <div className="text-center py-10 text-fg4 text-sm">Загрузка...</div>
      ) : constructions.length === 0 ? (
        <div className="text-center py-16 text-fg4 text-sm">Нет строек. Создайте первую.</div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {constructions.map(c => {
            const setsCount = c.sections.reduce((acc, s) => acc + s.documentSets.length, 0);
            return (
              <div key={c.id} className="bg-surface border border-stroke rounded-xl p-5 flex flex-col gap-3 hover:border-brand-subtle hover:shadow-sm transition-all group cursor-pointer"
                onClick={() => editId !== c.id && navigate(`/document-sets/${c.id}`)}>
                <div className="flex items-start justify-between gap-2">
                  {editId === c.id ? (
                    <input value={editName} onChange={e => setEditName(e.target.value)}
                      onBlur={() => commitRename(c)}
                      onKeyDown={e => { if (e.key === 'Enter') commitRename(c); if (e.key === 'Escape') setEditId(null); }}
                      autoFocus onClick={e => e.stopPropagation()}
                      className="flex-1 text-base font-semibold border-b border-brand bg-transparent outline-none" />
                  ) : (
                    <h3 className="text-base font-semibold text-fg1 flex-1">{c.name}</h3>
                  )}
                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity shrink-0">
                    <button onClick={e => { e.stopPropagation(); setEditId(c.id); setEditName(c.name); }}
                      className="p-1.5 text-fg4 hover:text-fg2 rounded transition-colors" title="Переименовать">
                      <Pencil size={13} />
                    </button>
                    <button onClick={e => { e.stopPropagation(); setDeleteTarget(c); }}
                      className="p-1.5 text-fg4 hover:text-danger rounded transition-colors" title="Удалить">
                      <Trash2 size={13} />
                    </button>
                  </div>
                </div>
                <div className="flex items-center gap-4 text-xs text-fg4">
                  <span>{ruCount(c.sections.length, 'раздел', 'раздела', 'разделов')}</span>
                  <span>{ruCount(setsCount, 'комплект', 'комплекта', 'комплектов')}</span>
                </div>
                {c.sections.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {c.sections.slice(0, 4).map(s => (
                      <span key={s.id} className="text-xs bg-brand-subtle text-brand px-2 py-0.5 rounded-full">{s.name}</span>
                    ))}
                    {c.sections.length > 4 && (
                      <span className="text-xs text-fg4">+{c.sections.length - 4} ещё</span>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новая стройка"
        footer={
          <div className="flex justify-end gap-3">
            <button type="button" onClick={() => setCreateOpen(false)} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
            <button type="submit" form="create-construction-form" disabled={createMutation.isPending} className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
              {createMutation.isPending ? 'Создание...' : 'Создать'}
            </button>
          </div>
        }>
        {createOpen && (
          <form id="create-construction-form" onSubmit={handleCreate} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-fg2 mb-1">Название стройки</label>
              <input value={newName} onChange={e => setNewName(e.target.value)} required autoFocus
                placeholder="например: ЖК Северный, корпус 1"
                className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
            </div>
            {createError && <p className="text-sm text-danger">{createError}</p>}
          </form>
        )}
      </Modal>

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить стройку «${deleteTarget?.name ?? ''}»?`}
        description={(() => {
          if (!deleteTarget) return undefined;
          const sectionsN = deleteTarget.sections.length;
          const setsN = deleteTarget.sections.reduce((acc, s) => acc + s.documentSets.length, 0);
          const docsN = deleteTarget.sections.reduce((acc, s) => acc + s.documentSets.reduce((a, ds) => a + (ds.instances?.length ?? 0), 0), 0);
          if (sectionsN === 0) return undefined;
          return (
            <>
              <p>Вместе с ней будут безвозвратно удалены:</p>
              <CascadeList items={[
                ruCount(sectionsN, 'раздел', 'раздела', 'разделов'),
                ...(setsN > 0 ? [ruCount(setsN, 'комплект', 'комплекта', 'комплектов')] : []),
                ...(docsN > 0 ? [`${ruCount(docsN, 'документ', 'документа', 'документов')} (и их сгенерированные PDF)`] : []),
              ]} />
            </>
          );
        })()}
        confirmLabel={`Удалить стройку «${deleteTarget?.name ?? ''}»`}
        requireCheckbox={deleteTarget && deleteTarget.sections.length > 0 ? 'Понимаю, что это необратимо' : undefined}
        onConfirm={() => { if (deleteTarget) deleteMutation.mutate(deleteTarget.id); }}
      />
    </div>
  );
}

// ─── Page entry ───────────────────────────────────────────────────────────────

export function DocumentSetsPage() {
  return (
    <Routes>
      <Route index element={<ConstructionsList />} />
      <Route path=":constructionId" element={<ConstructionDetail />} />
      <Route path=":constructionId/sets/:setId" element={<SetDetail />} />
    </Routes>
  );
}
