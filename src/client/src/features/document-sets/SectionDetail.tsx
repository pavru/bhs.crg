import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router';
import { Plus, Trash2, Pencil, FolderOpen, Layers, Database, Table2, Users } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { CatalogResource } from './catalog/CatalogResource';
import { DataSetsResource } from '@/features/datasets/DataSetsResource';
import { useProblemSummary, problemOf } from '@/shared/api/reconciliations';
import { usePlanSummary, planOf, planTitle } from '@/shared/api/plans';
import { PlanProgressBadge } from './PlanProgressBadge';
import { SubscribersResource } from './SubscribersResource';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useGetConstruction, useRenameSection, useDeleteSection, useCreateDocumentSet } from '@/shared/api/constructions';

// ── Экран раздела ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

type SectionPanel = 'catalog' | 'datasets' | 'subscribers';

export function SectionDetail() {

  const { constructionId, sectionId, panel } = useParams<{ constructionId: string; sectionId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: SectionPanel = (['datasets', 'subscribers'].includes(panel ?? '') ? panel : 'catalog') as SectionPanel;
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  const { data: docTypes = [] } = useListDocumentTypes();
  const { data: problems } = useProblemSummary('Section', sectionId);
  const { data: plan } = usePlanSummary('Section', sectionId);

  const [addSetOpen, setAddSetOpen] = useState(false);
  const [newSetName, setNewSetName] = useState('');
  const [addError, setAddError] = useState('');
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const createSet = useCreateDocumentSet();
  const renameSection = useRenameSection();
  const deleteSection = useDeleteSection();

  useDocumentTitle((() => {
    const s = construction?.sections.find(s => s.id === sectionId);
    return s ? `Раздел «${s.name}»` : null;
  })());

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-sm text-danger">Стройка не найдена</div>;
  const section = construction.sections.find(s => s.id === sectionId);
  if (!section) return <div className="p-6 text-sm text-danger">Раздел не найден</div>;

  const setsInSection = section.documentSets.length;
  const docsInSection = section.documentSets.reduce((acc, ds) => acc + (ds.documentCount ?? 0), 0);
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
      {section.documentSets.map(ds => {
        const p = problemOf(problems, ds.id);
        const done = planOf(plan, ds.id);
        return (
          <NavItem key={ds.id} icon={<FolderOpen size={17} />} label={ds.name} count={ds.documentCount ?? 0} chevron
            progress={done?.percent ?? null} progressTitle={planTitle(done)}
            alert={p?.needsAttention} alertDanger={p?.hasArithmeticProblems}
            onClick={() => navigate(`/document-sets/${constructionId}/sets/${ds.id}`)} />
        );
      })}
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
      {activePanel === 'catalog'
        ? <CatalogResource scope="Section" scopeId={sectionId ?? null} allDocTypes={docTypes} />
        : activePanel === 'datasets'
        ? <DataSetsResource scope="Section" scopeId={sectionId} />
        : <div className="mx-auto max-w-5xl">
            <SubscribersResource scope="Section" scopeId={sectionId!} />
          </div>}
    </div>
  );

  return (
    <>
      <ListDetailShell title={section.name} titleIcon={<Layers size={20} />} breadcrumb={contextCrumbs}
        titleBadge={<PlanProgressBadge progress={plan?.own} />}
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
        onConfirm={async () => {
          // mutateAsync ради видимости отказа 409 (issue #739) — см. ConstructionDetail.
          await deleteSection.mutateAsync({ id: section.id, constructionId: constructionId! });
          navigate(`/document-sets/${constructionId}`);
        }}
      />
    </>
  );
}
