import { formatDateTime } from '@/shared/format/format';
import { useState, useEffect, useRef } from 'react';
import { Navigate, useNavigate, useParams, useSearchParams, Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, ContentCopy, Database, Delete, Description, Download, DragIndicator, DriveFileMove, Edit, FolderCopy, FolderOpen, Group, Layers, Mail, Table, Target, Visibility, Warning } from '@/shared/ui/icons';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TypePicker, type PickType } from '@/shared/ui/TypePicker';
import { EmptyState } from '@/shared/ui/EmptyState';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { useScopePurge } from './useScopePurge';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { NotFavoriteNote } from '@/shared/siteFilter/SiteFilterNotes';
import { useToast } from '@/shared/ui/Toast';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { CopyDocumentDialog } from './CopyDocumentDialog';
import { MoveDocumentDialog } from './MoveDocumentDialog';
import { CatalogResource } from './catalog/CatalogResource';
import { DataSetsResource } from '@/features/datasets/DataSetsResource';
import { SetProblemsPanel } from './SetProblemsPanel';
import { PlanPanel } from './PlanPanel';
import { PlanProgressBadge } from './PlanProgressBadge';
import { useRelatedProblems } from '@/shared/api/reconciliations';
import { usePlanSummary } from '@/shared/api/plans';
import { SubscribersResource } from './SubscribersResource';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListConstructions, useGetConstruction, useRenameDocumentSet, useDeleteDocumentSet } from '@/shared/api/constructions';
import { useGetDocumentSet, useAddDocumentToSet, useDeleteDocumentInstance, useDuplicateDocumentInstance, useReorderInstances, useAssembleSet, useDocumentSetOutput, downloadSetOutput, downloadGeneratedFile, previewGeneratedFile } from '@/shared/api/documentSets';
import type { DocumentInstance } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from './fields';
import { documentPath, FROM_SET } from './documentPath';
import { EmailSendDialog } from './EmailSendDialog';
import { useEmailSet } from '@/shared/api/documentSets';
import { useAccess, NO_ACCESS, hasPermission } from '@/shared/api/access';
import { Tooltip } from '@/shared/ui/Tooltip';

// ── Экран комплекта ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

type SetPanel = 'documents' | 'catalog' | 'datasets' | 'subscribers' | 'issues' | 'plan';

export function SetDetail() {
  const { constructionId, setId, panel } = useParams<{ constructionId: string; setId: string; panel?: string }>();
  const navigate = useNavigate();
  const { data: access = NO_ACCESS } = useAccess();
  // «Проблемы» — по праву сверки (issue #1125): без него вкладка читала бы пустоту и писала
  // «Всё разобрано.», то есть отказ выглядел бы результатом. Ссылка на вкладку ведёт к документам.
  const canReconcile = hasPermission(access, 'core.reconciliation.run');
  const panels = ['catalog', 'datasets', 'subscribers', 'plan', ...(canReconcile ? ['issues'] : [])];
  const activePanel: SetPanel = (panels.includes(panel ?? '') ? panel : 'documents') as SetPanel;
  const { data: set, isLoading } = useGetDocumentSet(setId);
  const { data: construction } = useGetConstruction(constructionId!);
  const { data: allConstructions = [] } = useListConstructions();
  const { data: problems } = useRelatedProblems('Set', setId);
  const { data: plan } = usePlanSummary('Set', setId);
  const { data: docTypes = [] } = useListDocumentTypes();
  const [addDocOpen, setAddDocOpen] = useState(false);
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
  const purge = useScopePurge('set', () => setDeleteSetConfirm(false),
    () => navigate(`/document-sets/${constructionId}/sections/${set?.sectionId}`));
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
  // Кнопка отправки — по ПРАВУ, а не по имени роли (ТЗ ID-4.1, issue #989). Состав роли меняют в
  // редакторе, а имя остаётся прежним: проверка по имени означала, что носитель заведённой
  // администратором роли с правом рассылки кнопки не увидит, хотя сервер его пропускает.
  const canSend = hasPermission(access, 'id.document.send');
  const emailSet = useEmailSet();
  const toast = useToast();

  useEffect(() => {
    if (watching && output && output.generatedAt !== watchStartRef.current) {
      setWatching(false);
      setAssembleMsg('Комплект собран — можно скачать.');
    }
  }, [watching, output]);

  /**
   * Старый адрес документа — `?doc={instanceId}` (закладки, письма, ссылки до #1384). Документ теперь
   * страница со своим адресом; старый ведёт туда же, заменяя себя в истории.
   */
  const [searchParams] = useSearchParams();
  const docParam = searchParams.get('doc');
  useDocumentTitle(set ? `Комплект «${set.name}»` : null);

  if (docParam) return <Navigate replace to={documentPath(constructionId!, setId!, docParam)} />;
  if (isLoading) return <div className="p-6 text-body-medium text-on-surface-variant">Загрузка...</div>;
  if (!set) return <div className="p-6 text-body-medium text-error">Комплект не найден</div>;

  async function handleAssemble() {
    setAssembleMsg('');
    watchStartRef.current = output?.generatedAt;
    try {
      await assembleMutation.mutateAsync({ setId: set!.id });
      setWatching(true);
      setAssembleMsg('Сборка запущена — ход виден на кнопке активности в шапке.');
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
  const sectionName = construction?.sections.find(s => s.id === set.sectionId)?.name;

  const base = `/document-sets/${constructionId}/sets/${setId}`;
  const openDocument = (inst: DocumentInstance) => navigate(documentPath(constructionId!, setId!, inst.id), { state: FROM_SET });
  const goPanel = (p: SetPanel) => navigate(p === 'documents' ? base : `${base}/${p}`);

  const contextCrumbs = (
    <div className="flex items-center gap-1.5 flex-wrap">
      <Link to="/document-sets" className="text-body-small text-on-surface-variant hover:text-on-surface-variant transition-colors">Стройки</Link>
      {construction && (
        <Link to={`/document-sets/${constructionId}`}
          className="text-label-small px-2 py-0.5 rounded-full border border-outline-variant text-on-surface-variant hover:border-outline hover:text-on-surface transition-colors">
          Стройка: {construction.name}
        </Link>
      )}
      {sectionName && (
        <Link to={`/document-sets/${constructionId}/sections/${set.sectionId}`}
          className="text-label-small px-2 py-0.5 rounded-full border border-outline-variant text-on-surface-variant hover:border-outline hover:text-on-surface transition-colors">
          Раздел: {sectionName}
        </Link>
      )}
      <NotFavoriteNote constructionId={constructionId!} />
    </div>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      <NavItem icon={<Icon icon={Description} size={18} />} label="Документы" count={set.instances.length}
        active={activePanel === 'documents'} onClick={() => goPanel('documents')} />
      <NavSection label="Этот комплект" />
      <NavItem icon={<Icon icon={Database} size={18} />} label="Каталог" active={activePanel === 'catalog'} onClick={() => goPanel('catalog')} />
      <NavItem icon={<Icon icon={Table} size={18} />} label="Наборы данных" active={activePanel === 'datasets'} onClick={() => goPanel('datasets')} />
      <NavItem icon={<Icon icon={Group} size={18} />} label="Подписчики" active={activePanel === 'subscribers'} onClick={() => goPanel('subscribers')} />
      {/* План — рядом с ресурсами комплекта, а не в настройках: сколько документов должно быть,
          решают там же, где их и заводят. */}
      <NavItem icon={<Icon icon={Target} size={18} />} label="План"
        progress={plan?.own.percent ?? null}
        active={activePanel === 'plan'} onClick={() => goPanel('plan')} />
      {/* Проблемы живут здесь, а не в разделе «Сверка»: комплект — тот уровень, где их разбирают. */}
      {canReconcile && (
        <NavItem icon={<Icon icon={Warning} size={18} />} label="Проблемы"
          count={problems?.needsAttention || undefined}
          active={activePanel === 'issues'} onClick={() => goPanel('issues')} />
      )}
    </div>
  );

  const headerAction = (
    <>
      {activePanel === 'documents' && (
        <>
          {output && (
            <Button variant="outlined" size="sm" icon={<Icon icon={Download} size={18} />} onClick={() => downloadSetOutput(set.id, set.name)}
              title={`Собран ${formatDateTime(output.generatedAt)}`}>Скачать</Button>
          )}
          {canSend && output && (
            <Button variant="outlined" size="sm" icon={<Icon icon={Mail} size={18} />} onClick={() => setEmailKitOpen(true)}
              title="Отправить собранный комплект по почте">Почта</Button>
          )}
          <Button variant="tonal" size="sm" icon={<Icon icon={Layers} size={18} />} loading={assembleMutation.isPending || watching}
            disabled={assembleMutation.isPending || watching || set.instances.length === 0} onClick={handleAssemble}
            title="Собрать все документы комплекта в один PDF">Собрать</Button>
          <Button variant="filled" size="sm" icon={<Icon icon={Add} size={18} />} onClick={() => setAddDocOpen(true)}>Добавить документ</Button>
        </>
      )}
      <RowActionsMenu ariaLabel="Действия комплекта" actions={[
        { key: 'rename', label: 'Переименовать', icon: <Icon icon={Edit} size={18} />, onSelect: () => { setRenameSetVal(set.name); setRenameSetOpen(true); } },
        { key: 'delete', label: 'Удалить комплект', icon: <Icon icon={Delete} size={18} />, danger: true, onSelect: () => setDeleteSetConfirm(true) },
      ]} />
    </>
  );

  const lastIndex = set.instances.length - 1;
  const documentColumns: DataGridColumn<DocumentInstance>[] = [
    { key: 'order', label: '', hint: 'Порядок в собранном комплекте', width: 'w-16', interactive: true,
      cell: (_, index) => (
        <div className="flex items-center gap-1">
          <IconButton label={`Переместить документ ${index + 1} перетаскиванием`}
            title="Перетащить для изменения порядка"
            size="sm"
            className="cursor-grab active:cursor-grabbing"
            draggable
            onDragStart={() => setDragIndex(index)}
            onDragEnd={endDrag}>
            <Icon icon={DragIndicator} size={18} />
          </IconButton>
          <div className="flex flex-col items-center -my-1">
            <MoveButtons onUp={() => moveDoc(index, -1)} onDown={() => moveDoc(index, 1)}
              isFirst={index === 0} isLast={index === lastIndex}
              busy={reorderMutation.isPending}
              className="hover:text-primary transition-colors" />
          </div>
        </div>
      ) },
    { key: 'name', label: 'Документ', wrap: true,
      cell: inst => (
        <>
          <div className="font-medium text-on-surface">
            {inst.name || docTypeMap[inst.documentTypeId]?.name || inst.documentTypeId}
          </div>
          {inst.name && <div className="text-body-small text-on-surface-variant mt-0.5">{docTypeMap[inst.documentTypeId]?.name}</div>}
        </>
      ) },
    { key: 'status', label: 'Статус', width: 'w-32',
      cell: inst => (
        <span className={`text-body-small px-2 py-0.5 rounded-xs font-medium ${STATUS_COLORS[inst.status] ?? 'bg-surface-container text-on-surface-variant'}`}>
          {STATUS_LABELS[inst.status] ?? inst.status}
        </span>
      ) },
    { key: 'pdf', label: '', width: 'w-36', interactive: true,
      cell: inst => {
        const pdfFiles = inst.generatedFiles.filter(f => f.format === 'Pdf');
        return (
          <div className="flex items-center gap-1">
            {pdfFiles.length === 1 && (
              <>
                {/* Отказ из открытия доходит один — вкладку не дали открыть: об отказе загрузки говорит сама вкладка. */}
                <Tooltip content="Открыть PDF">
                  <button onClick={() => previewGeneratedFile(inst.id, pdfFiles[0].templateId).catch((e: Error) => toast.error(e.message))}
                    className="flex items-center gap-1 px-2 py-1 text-body-small border border-outline-variant rounded-xs hover:bg-primary-container hover:border-primary-container text-on-surface-variant transition-colors">
                    <Icon icon={Visibility} size={18} /> PDF
                  </button>
                </Tooltip>
                <IconButton label="Скачать PDF" size="sm"
                  onClick={() => downloadGeneratedFile(inst.id, pdfFiles[0].templateId)}>
                  <Icon icon={Download} size={18} />
                </IconButton>
              </>
            )}
            {pdfFiles.length > 1 && (
              <Tooltip content="Несколько PDF — открыть документ для выбора">
                <button onClick={() => openDocument(inst)}
                  className="flex items-center gap-1 px-2 py-1 text-body-small border border-outline-variant rounded-xs hover:bg-primary-container hover:border-primary-container text-on-surface-variant transition-colors">
                  <Icon icon={Visibility} size={18} /> {pdfFiles.length} PDF
                </button>
              </Tooltip>
            )}
          </div>
        );
      } },
    { key: 'actions', label: '', width: 'w-10', align: 'right', interactive: true,
      cell: inst => (
        <RowActionsMenu ariaLabel="Действия документа" actions={[
          { key: 'dup', label: 'Дублировать', icon: <Icon icon={ContentCopy} size={18} />,
            disabled: duplicateMutation.isPending,
            onSelect: () => duplicateMutation.mutate({ setId: set.id, instanceId: inst.id },
              { onError: e => toast.apiError(e, 'Не удалось дублировать документ') }) },
          { key: 'copy', label: 'Скопировать в комплект', icon: <Icon icon={FolderCopy} size={18} />,
            onSelect: () => setCopyInstance(inst) },
          { key: 'move', label: 'Перенести в комплект', icon: <Icon icon={DriveFileMove} size={18} />,
            onSelect: () => setMoveInstance(inst) },
          { key: 'del', label: 'Удалить документ', icon: <Icon icon={Delete} size={18} />, danger: true,
            disabled: deleteMutation.isPending, onSelect: () => setDeleteTarget(inst) },
        ]} />
      ) },
  ];

  const documentsContent = (
    <>
      {addError && <p className="text-body-small text-error mb-3">{addError}</p>}
      {!addError && assembleMsg && <p className="text-body-small text-on-surface-variant mb-3">{assembleMsg}</p>}

      {set.instances.length === 0 ? (
        <div className="bg-surface border border-outline-variant rounded-lg overflow-hidden">
          <EmptyState className="m-4 border-0" icon={<Icon icon={Description} size={40} />} title="В комплекте пока нет документов"
            description="Добавьте документ нужного типа — заполните реквизиты и сгенерируйте PDF."
            action={<Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAddDocOpen(true)}>Добавить документ</Button>} />
        </div>
      ) : (
        <DataGrid columns={documentColumns} rows={set.instances} rowKey={inst => inst.id} onRowOpen={openDocument}
          drag={{
            dragging: dragIndex, target: dropPos, onOver: onRowDragOver,
            onDrop: () => { if (dragIndex !== null && dropPos !== null) moveDocToPos(dragIndex, dropPos); endDrag(); },
          }} />
      )}
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
        : activePanel === 'plan'
        ? <PlanPanel setId={setId!} />
        : <div className="mx-auto max-w-5xl">
            {activePanel === 'documents' ? documentsContent
              : <SubscribersResource scope="Set" scopeId={setId!} />}
          </div>}
    </div>
  );

  return (
    <>
      <ListDetailShell title={set.name} icon={<Icon icon={FolderOpen} size={20} />} breadcrumb={contextCrumbs}
        badge={<PlanProgressBadge progress={plan?.own} />}
        actions={headerAction} nav={nav} detail={detail} backLabel="К разделам" />

      {canSend && (
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

      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить документ «${deleteTarget ? (deleteTarget.name || docTypeMap[deleteTarget.documentTypeId]?.name || deleteTarget.documentTypeId) : ''}»?`}
        confirmLabel="Удалить документ"
        onConfirm={async () => {
          if (!deleteTarget) return;
          await deleteMutation.mutateAsync({ setId: set.id, instanceId: deleteTarget.id });
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
        onRefused={purge.onRefused(set)}
      />
      {purge.dialog}
    </>
  );
}
