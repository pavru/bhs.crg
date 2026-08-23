import { useState, useEffect, useRef } from 'react';
import { useNavigate, useParams, useSearchParams, Link } from 'react-router';
import { Plus, Trash2, Download, Pencil, FolderOpen, Eye, GripVertical, Copy, FolderInput, FolderOutput, Layers, FileText, Mail, Database, Table2, Users, AlertTriangle } from 'lucide-react';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { useToast } from '@/shared/ui/Toast';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { CopyDocumentDialog } from './CopyDocumentDialog';
import { MoveDocumentDialog } from './MoveDocumentDialog';
import { CatalogResource } from './catalog/CatalogResource';
import { DataSetsResource } from '@/features/datasets/DataSetsResource';
import { SetProblemsPanel } from './SetProblemsPanel';
import { useRelatedProblems } from '@/shared/api/reconciliations';
import { SubscribersResource } from './SubscribersResource';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListConstructions, useGetConstruction, useRenameDocumentSet, useDeleteDocumentSet } from '@/shared/api/constructions';
import { useGetDocumentSet, useGetAvailableInstances, useAddDocumentToSet, useDeleteDocumentInstance, useDuplicateDocumentInstance, useReorderInstances, useAssembleSet, useDocumentSetOutput, downloadSetOutput, downloadGeneratedFile, previewGeneratedFile } from '@/shared/api/documentSets';
import type { DocumentInstance } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from './fields';
import { InstanceEditor } from './editor';
import { ErrorBoundary } from '@/shared/ui/ErrorBoundary';
import { EmailSendDialog } from './EmailSendDialog';
import { useEmailSet } from '@/shared/api/documentSets';
import { useAuth } from '@/shared/hooks/useAuth';

// ── Экран комплекта ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

type SetPanel = 'documents' | 'catalog' | 'datasets' | 'subscribers' | 'issues';

export function SetDetail() {
  const { constructionId, setId, panel } = useParams<{ constructionId: string; setId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: SetPanel = (['catalog', 'datasets', 'subscribers', 'issues'].includes(panel ?? '') ? panel : 'documents') as SetPanel;
  const { data: set, isLoading } = useGetDocumentSet(setId);
  const { data: construction } = useGetConstruction(constructionId!);
  const { data: allConstructions = [] } = useListConstructions();
  const { data: availableInstances = [] } = useGetAvailableInstances(setId);
  const { data: problems } = useRelatedProblems('Set', setId);
  const { data: docTypes = [] } = useListDocumentTypes();
  const [addDocOpen, setAddDocOpen] = useState(false);
  const [editInstance, setEditInstance] = useState<DocumentInstance | null>(null);
  const [editDirty, setEditDirty] = useState(false);
  const addMutation = useAddDocumentToSet();
  const deleteMutation = useDeleteDocumentInstance();
  const duplicateMutation = useDuplicateDocumentInstance();
  const reorderMutation = useReorderInstances();
  const assembleMutation = useAssembleSet();
  const renameSet = useRenameDocumentSet();
  const deleteSet = useDeleteDocumentSet();
  const [addError, setAddError] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<DocumentInstance | null>(null);
  const [copyInstance, setCopyInstance] = useState<DocumentInstance | null>(null);
  const [moveInstance, setMoveInstance] = useState<DocumentInstance | null>(null);
  const [renameSetOpen, setRenameSetOpen] = useState(false);
  const [renameSetVal, setRenameSetVal] = useState('');
  const [deleteSetConfirm, setDeleteSetConfirm] = useState(false);
  // DnD-порядок документов: dragIndex — перетаскиваемая строка; dropPos — позиция вставки 0..N
  // (перед instances[dropPos]; N = в конец). Стрелки ↑↓ остаются доступным фолбэком.
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const [dropPos, setDropPos] = useState<number | null>(null);
  // Слежение за сборкой: пока идёт задача — опрашиваем вывод; останавливаемся, когда generatedAt изменится.
  const [watching, setWatching] = useState(false);
  const [assembleMsg, setAssembleMsg] = useState('');
  const [emailKitOpen, setEmailKitOpen] = useState(false);
  const watchStartRef = useRef<string | undefined>(undefined);
  const { data: output } = useDocumentSetOutput(setId, watching ? 2500 : false);
  const { user: me } = useAuth();
  const isAdmin = me?.role === 'Admin';
  const emailSet = useEmailSet();
  const toast = useToast();

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

  // Заголовок вкладки: открытый документ замещает имя комплекта («Документ — «Комплект»»).
  const openDocName = editInstance
    ? (editInstance.name || docTypes.find(t => t.id === editInstance.documentTypeId)?.name || 'Документ')
    : null;
  useDocumentTitle(set ? (openDocName ? `${openDocName} — «${set.name}»` : `Комплект «${set.name}»`) : null);

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
    reorderMutation.mutate({ setId: set!.id, orderedIds: ids },
      { onError: e => toast.apiError(e, 'Не удалось изменить порядок документов') });
  }

  // Переставить документ из позиции from в позицию вставки pos (перед instances[pos]; pos=N — в конец).
  function moveDocToPos(from: number, pos: number) {
    if (pos === from || pos === from + 1) return; // на месте
    const ids = set!.instances.map(i => i.id);
    const [moved] = ids.splice(from, 1);
    const insertAt = pos > from ? pos - 1 : pos; // учёт сдвига после удаления
    ids.splice(insertAt, 0, moved);
    reorderMutation.mutate({ setId: set!.id, orderedIds: ids },
      { onError: e => toast.apiError(e, 'Не удалось изменить порядок документов') });
  }
  function endDrag() { setDragIndex(null); setDropPos(null); }
  // Позиция вставки по половине строки под курсором (верх → перед, низ → после) — полный диапазон, вкл. конец.
  function onRowDragOver(e: React.DragEvent, index: number) {
    if (dragIndex === null) return;
    e.preventDefault();
    const r = e.currentTarget.getBoundingClientRect();
    const pos = e.clientY < r.top + r.height / 2 ? index : index + 1;
    if (pos !== dropPos) setDropPos(pos);
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
      {/* Проблемы живут здесь, а не в разделе «Сверка»: комплект — тот уровень, где их разбирают. */}
      <NavItem icon={<AlertTriangle size={17} />} label="Проблемы"
        count={problems?.needsAttention || undefined}
        active={activePanel === 'issues'} onClick={() => goPanel('issues')} />
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
                <th className="px-2 py-3 w-16" title="Порядок в собранном комплекте" />
                <th className="text-left px-4 py-3 font-medium text-fg2">Документ</th>
                <th className="text-left px-4 py-3 font-medium text-fg2 w-32">Статус</th>
                <th className="px-4 py-3 w-36" />
                <th className="px-4 py-3 w-10" />
              </tr>
            </thead>
            <tbody>
              {set.instances.map((inst, index) => {
                const pdfFiles = inst.generatedFiles.filter(f => f.format === 'Pdf');
                const isDragging = dragIndex === index;
                const lastIndex = set.instances.length - 1;
                const showTop = dragIndex !== null && dropPos === index;
                const showBottom = dragIndex !== null && dropPos === set.instances.length && index === lastIndex;
                const rowStyle = showTop ? { boxShadow: 'inset 0 2px 0 0 var(--color-brand)' }
                  : showBottom ? { boxShadow: 'inset 0 -2px 0 0 var(--color-brand)' } : undefined;
                return (
                  <tr key={inst.id}
                    className={`border-b border-muted last:border-0 hover:bg-base cursor-pointer group transition-opacity ${isDragging ? 'opacity-40' : ''}`}
                    style={rowStyle}
                    onClick={() => setEditInstance(inst)}
                    onDragOver={e => onRowDragOver(e, index)}
                    onDrop={() => { if (dragIndex !== null && dropPos !== null) moveDocToPos(dragIndex, dropPos); endDrag(); }}>
                    <td className="px-2 py-3" onClick={e => e.stopPropagation()}>
                      <div className="flex items-center gap-1">
                        <button type="button" draggable
                          onDragStart={() => setDragIndex(index)} onDragEnd={endDrag}
                          className="cursor-grab active:cursor-grabbing text-fg4 hover:text-fg2 shrink-0 focus-visible:outline-none focus-visible:text-brand"
                          title="Перетащить для изменения порядка" aria-label={`Переместить документ ${index + 1} перетаскиванием`}>
                          <GripVertical size={14} />
                        </button>
                        <div className="flex flex-col items-center -my-1">
                          <MoveButtons onUp={() => moveDoc(index, -1)} onDown={() => moveDoc(index, 1)}
                            isFirst={index === 0} isLast={index === lastIndex}
                            busy={reorderMutation.isPending} size={13}
                            className="hover:text-brand transition-colors" />
                        </div>
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
                    <td className="px-4 py-3 text-right" onClick={e => e.stopPropagation()}>
                      <RowActionsMenu ariaLabel="Действия документа" actions={[
                        { key: 'dup', label: 'Дублировать', icon: <Copy size={14} />,
                          disabled: duplicateMutation.isPending,
                          onSelect: () => duplicateMutation.mutate({ setId: set.id, instanceId: inst.id },
                            { onError: e => toast.apiError(e, 'Не удалось дублировать документ') }) },
                        { key: 'copy', label: 'Скопировать в комплект', icon: <FolderInput size={14} />,
                          onSelect: () => setCopyInstance(inst) },
                        { key: 'move', label: 'Перенести в комплект', icon: <FolderOutput size={14} />,
                          onSelect: () => setMoveInstance(inst) },
                        { key: 'del', label: 'Удалить документ', icon: <Trash2 size={14} />, danger: true,
                          disabled: deleteMutation.isPending, onSelect: () => setDeleteTarget(inst) },
                      ]} />
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
      {/* Каталог и Наборы данных — во всю ширину (рейл у левого края); документы/подписчики центрируем. */}
      {activePanel === 'catalog'
        ? <CatalogResource scope="Set" scopeId={setId ?? null} allDocTypes={docTypes} />
        : activePanel === 'datasets'
        ? <DataSetsResource scope="Set" scopeId={setId} />
        : activePanel === 'issues'
        ? <SetProblemsPanel setId={setId!} setName={set.name}
            documentNames={new Map(set.instances.map(i => [i.id, i.name ?? '(без имени)']))} />
        : <div className="mx-auto max-w-5xl">
            {activePanel === 'documents' ? documentsContent
              : <SubscribersResource scope="Set" scopeId={setId!} />}
          </div>}
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
              // Граница ошибок (issue #305): краш рендера редактора изолирован в панель ошибки
              // вместо немого пустого экрана; Esc/закрытие живут на уровне Modal (вне границы).
              // resetKeys=[id] — смена документа сбрасывает пойманную ошибку.
              <ErrorBoundary variant="inline" allowReload resetKeys={[liveInstance.id]}
                title="Не удалось открыть документ">
                <InstanceEditor key={liveInstance.id} instance={liveInstance} setId={setId} docType={docTypeMap[liveInstance.documentTypeId]}
                  allDocTypes={docTypes} otherInstances={otherInstances}
                  onClose={() => { setEditInstance(null); setEditDirty(false); }} onDirtyChange={setEditDirty}
                  requestClose={requestClose} />
              </ErrorBoundary>
            )}
          </Modal>
        );
      })()}

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить документ «${deleteTarget ? (deleteTarget.name || docTypeMap[deleteTarget.documentTypeId]?.name || deleteTarget.documentTypeId) : ''}»?`}
        confirmLabel="Удалить документ"
        onConfirm={async () => {
          if (!deleteTarget) return;
          await deleteMutation.mutateAsync({ setId: set.id, instanceId: deleteTarget.id });
          if (editInstance?.id === deleteTarget.id) setEditInstance(null);
        }}
      />

      {copyInstance && (
        <CopyDocumentDialog
          open={!!copyInstance}
          onClose={() => setCopyInstance(null)}
          setId={set.id}
          instance={{ id: copyInstance.id, name: copyInstance.name || docTypeMap[copyInstance.documentTypeId]?.name || copyInstance.documentTypeId }}
          constructions={allConstructions}
        />
      )}

      {moveInstance && (
        <MoveDocumentDialog
          open={!!moveInstance}
          onClose={() => setMoveInstance(null)}
          setId={set.id}
          currentSetName={set.name}
          instance={{ id: moveInstance.id, name: moveInstance.name || docTypeMap[moveInstance.documentTypeId]?.name || moveInstance.documentTypeId }}
          constructions={allConstructions}
        />
      )}

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
        onConfirm={async () => {
          // mutateAsync ради видимости отказа 409 (issue #739) — см. ConstructionDetail.
          await deleteSet.mutateAsync({ id: set.id, constructionId: constructionId! });
          navigate(`/document-sets/${constructionId}/sections/${set.sectionId}`);
        }}
      />
    </>
  );
}
