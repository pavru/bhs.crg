import { useState, useEffect, useRef } from 'react';
import { Routes, Route, useNavigate, useParams, useSearchParams, Link } from 'react-router-dom';
import {
  Plus, Trash2, Download, Pencil, FolderOpen, Eye,
  ArrowUp, ArrowDown, Layers, Building2, FileText, Search, X, Mail, Database, Table2, Users,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { CatalogResource } from './catalog/CatalogResource';
import { DataSetsResource } from '@/features/datasets/DataSetsResource';
import { SubscribersResource } from './SubscribersResource';
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
import type { Construction, DocumentInstance } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from './fields';
import { InstanceEditor } from './editor';
import { EmailSendDialog } from './EmailSendDialog';
import { useEmailSet } from '@/shared/api/documentSets';
import { useAuth } from '@/shared/hooks/useAuth';

// ─── Set detail (documents) ───────────────────────────────────────────────────

type SetPanel = 'documents' | 'catalog' | 'datasets' | 'subscribers';

function SetDetail() {
  const { constructionId, setId, panel } = useParams<{ constructionId: string; setId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: SetPanel = (['catalog', 'datasets', 'subscribers'].includes(panel ?? '') ? panel : 'documents') as SetPanel;
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
  const renameSet = useRenameDocumentSet();
  const deleteSet = useDeleteDocumentSet();
  const [addError, setAddError] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<DocumentInstance | null>(null);
  const [renameSetOpen, setRenameSetOpen] = useState(false);
  const [renameSetVal, setRenameSetVal] = useState('');
  const [deleteSetConfirm, setDeleteSetConfirm] = useState(false);
  // Слежение за сборкой: пока идёт задача — опрашиваем вывод; останавливаемся, когда generatedAt изменится.
  const [watching, setWatching] = useState(false);
  const [assembleMsg, setAssembleMsg] = useState('');
  const [emailKitOpen, setEmailKitOpen] = useState(false);
  const watchStartRef = useRef<string | undefined>(undefined);
  const { data: output } = useDocumentSetOutput(setId, watching ? 2500 : false);
  const { user: me } = useAuth();
  const isAdmin = me?.role === 'Admin';
  const emailSet = useEmailSet();

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

  // Пикер сам закрывается по выбору; добавляем документ выбранного типа (ошибку показываем строкой у шапки).
  async function handlePickDocType(typeId: string) {
    setAddError('');
    try {
      await addMutation.mutateAsync({ setId: set!.id, documentTypeId: typeId });
    } catch (err: unknown) { setAddError(err instanceof Error ? err.message : 'Ошибка добавления документа'); }
  }

  const docTypeMap = Object.fromEntries(docTypes.map(dt => [dt.id, dt]));
  const documentKindTypes = docTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const otherInstances = editInstance
    ? availableInstances.filter(i => i.id !== editInstance.id)
    : availableInstances;
  const sectionName = construction?.sections.find(s => s.id === set.sectionId)?.name;

  const base = `/document-sets/${constructionId}/sets/${setId}`;
  const goPanel = (p: SetPanel) => navigate(p === 'documents' ? base : `${base}/${p}`);

  const contextCrumbs = (
    <div className="flex items-center gap-1.5 flex-wrap">
      <Link to="/document-sets" className="text-xs text-fg4 hover:text-fg2 transition-colors">Стройки</Link>
      {construction && (
        <Link to={`/document-sets/${constructionId}`}
          className="text-[11px] px-2 py-0.5 rounded-full border border-stroke text-fg3 hover:border-stroke-strong hover:text-fg1 transition-colors">
          Стройка: {construction.name}
        </Link>
      )}
      {sectionName && (
        <Link to={`/document-sets/${constructionId}/sections/${set.sectionId}`}
          className="text-[11px] px-2 py-0.5 rounded-full border border-stroke text-fg3 hover:border-stroke-strong hover:text-fg1 transition-colors">
          Раздел: {sectionName}
        </Link>
      )}
    </div>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      <NavItem icon={<FileText size={17} />} label="Документы" count={set.instances.length}
        active={activePanel === 'documents'} onClick={() => goPanel('documents')} />
      <NavSection label="Этот комплект" />
      <NavItem icon={<Database size={17} />} label="Каталог" active={activePanel === 'catalog'} onClick={() => goPanel('catalog')} />
      <NavItem icon={<Table2 size={17} />} label="Наборы данных" active={activePanel === 'datasets'} onClick={() => goPanel('datasets')} />
      <NavItem icon={<Users size={17} />} label="Подписчики" active={activePanel === 'subscribers'} onClick={() => goPanel('subscribers')} />
    </div>
  );

  const headerAction = (
    <div className="flex items-center gap-2 shrink-0">
      {activePanel === 'documents' && (
        <>
          {output && (
            <Button variant="outlined" size="sm" icon={<Download size={15} />} onClick={() => downloadSetOutput(set.id, set.name)}
              title={`Собран ${new Date(output.generatedAt).toLocaleString('ru-RU')}`}>Скачать</Button>
          )}
          {isAdmin && output && (
            <Button variant="outlined" size="sm" icon={<Mail size={15} />} onClick={() => setEmailKitOpen(true)}
              title="Отправить собранный комплект по почте">Почта</Button>
          )}
          <Button variant="tonal" size="sm" icon={<Layers size={15} />} loading={assembleMutation.isPending || watching}
            disabled={assembleMutation.isPending || watching || set.instances.length === 0} onClick={handleAssemble}
            title="Собрать все документы комплекта в один PDF">Собрать</Button>
          <Button variant="filled" size="sm" icon={<Plus size={16} />} onClick={() => setAddDocOpen(true)}>Добавить документ</Button>
        </>
      )}
      <RowActionsMenu ariaLabel="Действия комплекта" actions={[
        { key: 'rename', label: 'Переименовать', icon: <Pencil size={14} />, onSelect: () => { setRenameSetVal(set.name); setRenameSetOpen(true); } },
        { key: 'delete', label: 'Удалить комплект', icon: <Trash2 size={14} />, danger: true, onSelect: () => setDeleteSetConfirm(true) },
      ]} />
    </div>
  );

  const documentsContent = (
    <>
      {addError && <p className="text-xs text-danger mb-3">{addError}</p>}
      {!addError && assembleMsg && <p className="text-xs text-fg4 mb-3">{assembleMsg}</p>}

      <div className="bg-surface border border-stroke rounded-xl overflow-hidden">
        {set.instances.length === 0 ? (
          <EmptyState className="m-4 border-0" icon={<FileText size={30} />} title="В комплекте пока нет документов"
            description="Добавьте документ нужного типа — заполните реквизиты и сгенерируйте PDF."
            action={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setAddDocOpen(true)}>Добавить документ</Button>} />
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
                            <IconButton label="Скачать PDF" size="sm"
                              onClick={() => downloadGeneratedFile(inst.id, pdfFiles[0].templateId)}>
                              <Download size={13} />
                            </IconButton>
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
                      <IconButton label="Удалить документ" size="sm" danger
                        onClick={e => { e.stopPropagation(); setDeleteTarget(inst); }}
                        disabled={deleteMutation.isPending}
                        className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100">
                        <Trash2 size={14} />
                      </IconButton>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </>
  );

  const detail = (
    <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
      <div className="mx-auto max-w-5xl">
        {activePanel === 'documents' ? documentsContent
          : activePanel === 'catalog' ? <CatalogResource scope="Set" scopeId={setId ?? null} allDocTypes={docTypes} />
          : activePanel === 'datasets' ? <DataSetsResource scope="Set" scopeId={setId} />
          : <SubscribersResource scope="Set" scopeId={setId!} />}
      </div>
    </div>
  );

  return (
    <>
      <ListDetailShell title={set.name} titleIcon={<FolderOpen size={20} />} breadcrumb={contextCrumbs}
        headerAction={headerAction} nav={nav} detail={detail} />

      {isAdmin && (
        <EmailSendDialog open={emailKitOpen} onClose={() => setEmailKitOpen(false)}
          setId={set.id} itemName={`Комплект «${set.name}»`}
          defaultSubjectHint={`Исполнительная документация — ${set.name}`}
          defaultBodyHint={`Направляем собранный комплект исполнительной документации «${set.name}».`}
          ready={!!output} notReadyHint="Комплект ещё не собран. Сначала соберите его («Собрать комплект»), затем отправляйте."
          onSend={(to, subject, body) => emailSet.mutateAsync({ setId: set.id, to, subject, body })} />
      )}

      <TypePicker
        open={addDocOpen}
        onOpenChange={setAddDocOpen}
        title="Добавить документ"
        recentKey="doc-type"
        types={documentKindTypes.map<PickType>(dt => ({ id: dt.id, name: dt.name, code: dt.code, section: 'Типы документов' }))}
        onSelect={handlePickDocType}
      />

      {editInstance && setId && (() => {
        const liveInstance = set.instances.find(i => i.id === editInstance.id) ?? editInstance;
        return (
          <Modal open={!!editInstance} onOpenChange={open => { if (!open) { setEditInstance(null); setEditDirty(false); } }}
            title={liveInstance.name || docTypeMap[liveInstance.documentTypeId]?.name || 'Редактировать документ'}
            fullScreen headerless isDirty={editDirty} flushBody>
            {requestClose => (
              <InstanceEditor key={liveInstance.id} instance={liveInstance} setId={setId} docType={docTypeMap[liveInstance.documentTypeId]}
                allDocTypes={docTypes} otherInstances={otherInstances}
                onClose={() => { setEditInstance(null); setEditDirty(false); }} onDirtyChange={setEditDirty}
                requestClose={requestClose} />
            )}
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

      <Modal open={renameSetOpen} onOpenChange={setRenameSetOpen} title="Переименовать комплект"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setRenameSetOpen(false)}>Отмена</Button>
            <Button type="submit" form="rename-set-form" variant="filled" loading={renameSet.isPending}>Сохранить</Button>
          </div>
        }>
        {renameSetOpen && (
          <form id="rename-set-form" className="space-y-4"
            onSubmit={async e => {
              e.preventDefault();
              if (!renameSetVal.trim() || renameSetVal === set.name) { setRenameSetOpen(false); return; }
              await renameSet.mutateAsync({ id: set.id, name: renameSetVal, constructionId: constructionId! });
              setRenameSetOpen(false);
            }}>
            <TextField label="Наименование" value={renameSetVal} onChange={e => setRenameSetVal(e.target.value)} required autoFocus />
          </form>
        )}
      </Modal>

      <ConfirmDialog
        open={deleteSetConfirm}
        onOpenChange={setDeleteSetConfirm}
        title={`Удалить комплект «${set.name}»?`}
        description={
          set.instances.length > 0
            ? <CascadeList items={[`${ruCount(set.instances.length, 'документ', 'документа', 'документов')} (и их сгенерированные PDF)`]} />
            : undefined
        }
        confirmLabel="Удалить комплект"
        onConfirm={() => {
          deleteSet.mutate({ id: set.id, constructionId: constructionId! });
          navigate(`/document-sets/${constructionId}/sections/${set.sectionId}`);
        }}
      />
    </>
  );
}

// ─── Section detail (sets as children + section resources) ────────────────────

type SectionPanel = 'catalog' | 'datasets' | 'subscribers';

function SectionDetail() {
  const { constructionId, sectionId, panel } = useParams<{ constructionId: string; sectionId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: SectionPanel = (['datasets', 'subscribers'].includes(panel ?? '') ? panel : 'catalog') as SectionPanel;
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  const { data: docTypes = [] } = useListDocumentTypes();

  const [addSetOpen, setAddSetOpen] = useState(false);
  const [newSetName, setNewSetName] = useState('');
  const [addError, setAddError] = useState('');
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const createSet = useCreateDocumentSet();
  const renameSection = useRenameSection();
  const deleteSection = useDeleteSection();

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-sm text-danger">Стройка не найдена</div>;
  const section = construction.sections.find(s => s.id === sectionId);
  if (!section) return <div className="p-6 text-sm text-danger">Раздел не найден</div>;

  const setsInSection = section.documentSets.length;
  const docsInSection = section.documentSets.reduce((acc, ds) => acc + (ds.instances?.length ?? 0), 0);
  const base = `/document-sets/${constructionId}/sections/${sectionId}`;
  const goPanel = (p: SectionPanel) => navigate(p === 'catalog' ? base : `${base}/${p}`);

  async function handleAddSet(e: React.FormEvent) {
    e.preventDefault();
    setAddError('');
    try {
      const ds = await createSet.mutateAsync({ sectionId: section!.id, name: newSetName, constructionId: construction!.id });
      setAddSetOpen(false);
      setNewSetName('');
      navigate(`/document-sets/${constructionId}/sets/${ds.id}`);
    } catch (err: unknown) { setAddError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  const contextCrumbs = (
    <div className="flex items-center gap-1.5 flex-wrap">
      <Link to="/document-sets" className="text-xs text-fg4 hover:text-fg2 transition-colors">Стройки</Link>
      <Link to={`/document-sets/${constructionId}`}
        className="text-[11px] px-2 py-0.5 rounded-full border border-stroke text-fg3 hover:border-stroke-strong hover:text-fg1 transition-colors">
        Стройка: {construction.name}
      </Link>
    </div>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      <NavSection label="Комплекты" />
      {section.documentSets.length === 0 && <p className="px-3 py-1.5 text-xs text-fg4">Нет комплектов</p>}
      {section.documentSets.map(ds => (
        <NavItem key={ds.id} icon={<FolderOpen size={17} />} label={ds.name} count={ds.instances?.length ?? 0} chevron
          onClick={() => navigate(`/document-sets/${constructionId}/sets/${ds.id}`)} />
      ))}
      <button type="button" onClick={() => setAddSetOpen(true)}
        className="w-full flex items-center gap-2.5 px-3 h-9 rounded-full text-left text-sm text-brand hover:bg-brand-subtle transition-colors">
        <Plus size={16} className="shrink-0" /> Добавить комплект
      </button>
      <NavSection label="Этот раздел" />
      <NavItem icon={<Database size={17} />} label="Каталог" active={activePanel === 'catalog'} onClick={() => goPanel('catalog')} />
      <NavItem icon={<Table2 size={17} />} label="Наборы данных" active={activePanel === 'datasets'} onClick={() => goPanel('datasets')} />
      <NavItem icon={<Users size={17} />} label="Подписчики" active={activePanel === 'subscribers'} onClick={() => goPanel('subscribers')} />
    </div>
  );

  const headerAction = (
    <div className="flex items-center gap-2 shrink-0">
      <Button variant="filled" size="sm" icon={<Plus size={16} />} onClick={() => setAddSetOpen(true)}>Добавить комплект</Button>
      <RowActionsMenu ariaLabel="Действия раздела" actions={[
        { key: 'rename', label: 'Переименовать', icon: <Pencil size={14} />, onSelect: () => { setRenameVal(section.name); setRenameOpen(true); } },
        { key: 'delete', label: 'Удалить раздел', icon: <Trash2 size={14} />, danger: true, onSelect: () => setDeleteConfirm(true) },
      ]} />
    </div>
  );

  const detail = (
    <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
      <div className="mx-auto max-w-5xl">
        {activePanel === 'catalog' ? <CatalogResource scope="Section" scopeId={sectionId ?? null} allDocTypes={docTypes} />
          : activePanel === 'datasets' ? <DataSetsResource scope="Section" scopeId={sectionId} />
          : <SubscribersResource scope="Section" scopeId={sectionId!} />}
      </div>
    </div>
  );

  return (
    <>
      <ListDetailShell title={section.name} titleIcon={<Layers size={20} />} breadcrumb={contextCrumbs}
        headerAction={headerAction} nav={nav} detail={detail} />

      <Modal open={addSetOpen} onOpenChange={setAddSetOpen} title="Новый комплект"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setAddSetOpen(false)}>Отмена</Button>
            <Button type="submit" form="add-set-form" variant="filled" loading={createSet.isPending}>
              {createSet.isPending ? 'Создание…' : 'Создать'}
            </Button>
          </div>
        }>
        {addSetOpen && (
          <form id="add-set-form" onSubmit={handleAddSet} className="space-y-4">
            <TextField label="Наименование" value={newSetName} onChange={e => setNewSetName(e.target.value)}
              required autoFocus hint="например: Кабельный журнал, секция 2" />
            {addError && <p className="text-sm text-danger">{addError}</p>}
          </form>
        )}
      </Modal>

      <Modal open={renameOpen} onOpenChange={setRenameOpen} title="Переименовать раздел"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setRenameOpen(false)}>Отмена</Button>
            <Button type="submit" form="rename-section-form" variant="filled" loading={renameSection.isPending}>Сохранить</Button>
          </div>
        }>
        {renameOpen && (
          <form id="rename-section-form" className="space-y-4"
            onSubmit={async e => {
              e.preventDefault();
              if (!renameVal.trim() || renameVal === section.name) { setRenameOpen(false); return; }
              await renameSection.mutateAsync({ id: section.id, name: renameVal });
              setRenameOpen(false);
            }}>
            <TextField label="Название раздела (дисциплина)" value={renameVal} onChange={e => setRenameVal(e.target.value)} required autoFocus />
          </form>
        )}
      </Modal>

      <ConfirmDialog
        open={deleteConfirm}
        onOpenChange={setDeleteConfirm}
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
        onConfirm={() => {
          deleteSection.mutate({ id: section.id, constructionId: constructionId! });
          navigate(`/document-sets/${constructionId}`);
        }}
      />
    </>
  );
}

type ConstructionPanel = 'catalog' | 'datasets' | 'subscribers';

function ConstructionDetail() {
  const { constructionId, panel } = useParams<{ constructionId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: ConstructionPanel = (['datasets', 'subscribers'].includes(panel ?? '') ? panel : 'catalog') as ConstructionPanel;
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  const { data: docTypes = [] } = useListDocumentTypes();
  const [addSectionOpen, setAddSectionOpen] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [sectionError, setSectionError] = useState('');
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const createSection = useCreateSection();
  const renameConstruction = useRenameConstruction();
  const deleteConstruction = useDeleteConstruction();

  async function handleAddSection(e: React.FormEvent) {
    e.preventDefault();
    setSectionError('');
    try {
      const s = await createSection.mutateAsync({ constructionId: constructionId!, name: newSectionName });
      setAddSectionOpen(false);
      setNewSectionName('');
      navigate(`/document-sets/${constructionId}/sections/${s.id}`);
    } catch (err: unknown) { setSectionError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-sm text-danger">Стройка не найдена</div>;

  const base = `/document-sets/${constructionId}`;
  const goPanel = (p: ConstructionPanel) => navigate(p === 'catalog' ? base : `${base}/${p}`);
  const sectionsN = construction.sections.length;
  const setsN = construction.sections.reduce((a, s) => a + s.documentSets.length, 0);
  const docsN = construction.sections.reduce((a, s) => a + s.documentSets.reduce((x, ds) => x + (ds.instances?.length ?? 0), 0), 0);

  const contextCrumbs = (
    <Link to="/document-sets" className="text-xs text-fg4 hover:text-fg2 transition-colors">Стройки</Link>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      <NavSection label="Разделы" />
      {construction.sections.length === 0 && <p className="px-3 py-1.5 text-xs text-fg4">Нет разделов</p>}
      {construction.sections.map(s => (
        <NavItem key={s.id} icon={<Layers size={17} />} label={s.name} count={s.documentSets.length} chevron
          onClick={() => navigate(`/document-sets/${constructionId}/sections/${s.id}`)} />
      ))}
      <button type="button" onClick={() => setAddSectionOpen(true)}
        className="w-full flex items-center gap-2.5 px-3 h-9 rounded-full text-left text-sm text-brand hover:bg-brand-subtle transition-colors">
        <Plus size={16} className="shrink-0" /> Добавить раздел
      </button>
      <NavSection label="Эта стройка" />
      <NavItem icon={<Database size={17} />} label="Каталог" active={activePanel === 'catalog'} onClick={() => goPanel('catalog')} />
      <NavItem icon={<Table2 size={17} />} label="Наборы данных" active={activePanel === 'datasets'} onClick={() => goPanel('datasets')} />
      <NavItem icon={<Users size={17} />} label="Подписчики" active={activePanel === 'subscribers'} onClick={() => goPanel('subscribers')} />
    </div>
  );

  const headerAction = (
    <div className="flex items-center gap-2 shrink-0">
      <Button variant="filled" size="sm" icon={<Plus size={16} />} onClick={() => setAddSectionOpen(true)}>Добавить раздел</Button>
      <RowActionsMenu ariaLabel="Действия стройки" actions={[
        { key: 'rename', label: 'Переименовать', icon: <Pencil size={14} />, onSelect: () => { setRenameVal(construction.name); setRenameOpen(true); } },
        { key: 'delete', label: 'Удалить стройку', icon: <Trash2 size={14} />, danger: true, onSelect: () => setDeleteConfirm(true) },
      ]} />
    </div>
  );

  const detail = (
    <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
      <div className="mx-auto max-w-5xl">
        {activePanel === 'catalog' ? <CatalogResource scope="Construction" scopeId={constructionId ?? null} allDocTypes={docTypes} />
          : activePanel === 'datasets' ? <DataSetsResource scope="Construction" scopeId={constructionId} />
          : <SubscribersResource scope="Construction" scopeId={constructionId!} />}
      </div>
    </div>
  );

  return (
    <>
      <ListDetailShell title={construction.name} titleIcon={<Building2 size={20} />} breadcrumb={contextCrumbs}
        headerAction={headerAction} nav={nav} detail={detail} />

      <Modal open={addSectionOpen} onOpenChange={setAddSectionOpen} title="Новый раздел"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setAddSectionOpen(false)}>Отмена</Button>
            <Button type="submit" form="add-section-form" variant="filled" loading={createSection.isPending}>
              {createSection.isPending ? 'Создание…' : 'Создать'}
            </Button>
          </div>
        }>
        {addSectionOpen && (
          <form id="add-section-form" onSubmit={handleAddSection} className="space-y-4">
            <TextField label="Название раздела (дисциплина)" value={newSectionName}
              onChange={e => setNewSectionName(e.target.value)} required autoFocus
              hint="например: Электроснабжение, Слаботочные системы" />
            {sectionError && <p className="text-sm text-danger">{sectionError}</p>}
          </form>
        )}
      </Modal>

      <Modal open={renameOpen} onOpenChange={setRenameOpen} title="Переименовать стройку"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setRenameOpen(false)}>Отмена</Button>
            <Button type="submit" form="rename-construction-form" variant="filled" loading={renameConstruction.isPending}>Сохранить</Button>
          </div>
        }>
        {renameOpen && (
          <form id="rename-construction-form" className="space-y-4"
            onSubmit={async e => {
              e.preventDefault();
              if (!renameVal.trim() || renameVal === construction.name) { setRenameOpen(false); return; }
              await renameConstruction.mutateAsync({ id: construction.id, name: renameVal });
              setRenameOpen(false);
            }}>
            <TextField label="Название стройки" value={renameVal} onChange={e => setRenameVal(e.target.value)} required autoFocus />
          </form>
        )}
      </Modal>

      <ConfirmDialog
        open={deleteConfirm}
        onOpenChange={setDeleteConfirm}
        title={`Удалить стройку «${construction.name}»?`}
        description={
          sectionsN > 0 ? (
            <>
              <p>Вместе с ней будут безвозвратно удалены:</p>
              <CascadeList items={[
                ruCount(sectionsN, 'раздел', 'раздела', 'разделов'),
                ...(setsN > 0 ? [ruCount(setsN, 'комплект', 'комплекта', 'комплектов')] : []),
                ...(docsN > 0 ? [`${ruCount(docsN, 'документ', 'документа', 'документов')} (и их сгенерированные PDF)`] : []),
              ]} />
            </>
          ) : undefined
        }
        confirmLabel={`Удалить стройку «${construction.name}»`}
        requireCheckbox={sectionsN > 0 ? 'Понимаю, что это необратимо' : undefined}
        onConfirm={() => {
          deleteConstruction.mutate(construction.id);
          navigate('/document-sets');
        }}
      />
    </>
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
          <IconButton label="Очистить" size="sm" onClick={() => setText('')}
            className="absolute right-1.5 top-1/2 -translate-y-1/2">
            <X size={14} />
          </IconButton>
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
        <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
          Новая стройка
        </Button>
      </div>

      <DocumentSearchPanel />

      {isLoading ? (
        <div className="text-center py-10 text-fg4 text-sm">Загрузка...</div>
      ) : constructions.length === 0 ? (
        <EmptyState icon={<Building2 size={30} />} title="Пока нет строек"
          description="Создайте первую стройку, чтобы начать вести исполнительную документацию по её разделам и комплектам."
          action={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>Новая стройка</Button>} />
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {constructions.map(c => {
            const setsCount = c.sections.reduce((acc, s) => acc + s.documentSets.length, 0);
            return (
              <div key={c.id} className="bg-surface border border-stroke rounded-xl p-5 flex flex-col gap-3 hover:border-brand hover:shadow-[var(--f-shadow16)] transition-all group cursor-pointer"
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
                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity shrink-0">
                    <IconButton label="Переименовать" size="sm"
                      onClick={e => { e.stopPropagation(); setEditId(c.id); setEditName(c.name); }}>
                      <Pencil size={13} />
                    </IconButton>
                    <IconButton label="Удалить" size="sm" danger
                      onClick={e => { e.stopPropagation(); setDeleteTarget(c); }}>
                      <Trash2 size={13} />
                    </IconButton>
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
          <div className="flex justify-end gap-2">
            <Button variant="text" onClick={() => setCreateOpen(false)}>Отмена</Button>
            <Button type="submit" form="create-construction-form" variant="filled" loading={createMutation.isPending}>
              {createMutation.isPending ? 'Создание…' : 'Создать'}
            </Button>
          </div>
        }>
        {createOpen && (
          <form id="create-construction-form" onSubmit={handleCreate} className="space-y-4">
            <TextField label="Название стройки" value={newName} onChange={e => setNewName(e.target.value)}
              required autoFocus hint="например: ЖК Северный, корпус 1" />
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
      <Route path=":constructionId/:panel" element={<ConstructionDetail />} />
      <Route path=":constructionId/sections/:sectionId" element={<SectionDetail />} />
      <Route path=":constructionId/sections/:sectionId/:panel" element={<SectionDetail />} />
      <Route path=":constructionId/sets/:setId" element={<SetDetail />} />
      <Route path=":constructionId/sets/:setId/:panel" element={<SetDetail />} />
    </Routes>
  );
}
