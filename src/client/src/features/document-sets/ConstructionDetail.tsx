import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router';
import { Plus, Trash2, Pencil, Layers, Building2, Database, Table2, Users } from 'lucide-react';
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
import { SubscribersResource } from './SubscribersResource';
import { ruCount } from '@/shared/utils/pluralize';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useGetConstruction, useRenameConstruction, useDeleteConstruction, useCreateSection } from '@/shared/api/constructions';

// ── Экран стройки ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

type ConstructionPanel = 'catalog' | 'datasets' | 'subscribers';

export function ConstructionDetail() {

  const { constructionId, panel } = useParams<{ constructionId: string; panel?: string }>();
  const navigate = useNavigate();
  const activePanel: ConstructionPanel = (['datasets', 'subscribers'].includes(panel ?? '') ? panel : 'catalog') as ConstructionPanel;
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  const { data: docTypes = [] } = useListDocumentTypes();
  const { data: problems } = useProblemSummary('Construction', constructionId);
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

  useDocumentTitle(construction ? `Стройка «${construction.name}»` : null);

  if (isLoading) return <div className="p-6 text-sm text-fg4">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-sm text-danger">Стройка не найдена</div>;

  const base = `/document-sets/${constructionId}`;
  const goPanel = (p: ConstructionPanel) => navigate(p === 'catalog' ? base : `${base}/${p}`);
  const sectionsN = construction.sections.length;
  const setsN = construction.sections.reduce((a, s) => a + s.documentSets.length, 0);
  const docsN = construction.sections.reduce((a, s) => a + s.documentSets.reduce((x, ds) => x + (ds.documentCount ?? 0), 0), 0);

  const contextCrumbs = (
    <Link to="/document-sets" className="text-xs text-fg4 hover:text-fg2 transition-colors">Стройки</Link>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      <NavSection label="Разделы" />
      {construction.sections.length === 0 && <p className="px-3 py-1.5 text-xs text-fg4">Нет разделов</p>}
      {construction.sections.map(s => {
        const p = problemOf(problems, s.id);
        return (
          <NavItem key={s.id} icon={<Layers size={17} />} label={s.name} count={s.documentSets.length} chevron
            alert={p?.needsAttention} alertDanger={p?.hasArithmeticProblems}
            onClick={() => navigate(`/document-sets/${constructionId}/sections/${s.id}`)} />
        );
      })}
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
      {activePanel === 'catalog'
        ? <CatalogResource scope="Construction" scopeId={constructionId ?? null} allDocTypes={docTypes} />
        : activePanel === 'datasets'
        ? <DataSetsResource scope="Construction" scopeId={constructionId} />
        : <div className="mx-auto max-w-5xl">
            <SubscribersResource scope="Construction" scopeId={constructionId!} />
          </div>}
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
