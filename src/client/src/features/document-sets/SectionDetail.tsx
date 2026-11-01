import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, Edit, FolderOpen, Layers } from '@/shared/ui/icons';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { useScopePurge } from './useScopePurge';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { ListDetailShell, NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { NotFavoriteNote } from '@/shared/siteFilter/SiteFilterNotes';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { problemOf } from '@/shared/api/reconciliations';
import { planOf, planTitle } from '@/shared/api/plans';
import { PlanProgressBadge } from './PlanProgressBadge';
import { ruCount } from '@/shared/utils/pluralize';
import { useGetConstruction, useRenameSection, useDeleteSection, useCreateDocumentSet } from '@/shared/api/constructions';
import { useCan } from '@/shared/api/access';
import { useIdIndicators } from './useIdIndicators';
import { HiddenByWorkspace } from '@/shared/ui/WorkspaceNav';
import { useScopePanels } from './scopePanels';

// ── Экран раздела ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

export function SectionDetail() {

  const { constructionId, sectionId } = useParams<{ constructionId: string; sectionId: string }>();
  const navigate = useNavigate();
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  // Панели «Работы» и «Этот раздел» — из того же реестра, что у карточки стройки (plan-02).
  const base = `/document-sets/${constructionId}/sections/${sectionId}`;
  const panels = useScopePanels('section', base, { constructionId: constructionId!, sectionId: sectionId! });
  // Пункты модуля ИД — комплекты, их счётчики, готовность и «требует разбора» — ещё и по пространству
  // (ТЗ AUTH-16.1, issue #1001): страница стройки показывает пункты модулей открытого пространства.
  const { shown: idOn, plan, problems } = useIdIndicators('Section', sectionId);
  // Комплекты — модуль ИД, а раздел — ядро (issue #1128). Без модуля блок комплектов и кнопки
  // «Добавить комплект» не рисуются ВОВСЕ, а не пустым «Нет комплектов»: каждая дверь открывалась
  // отказом 403, а пустой список читался бы как «комплектов нет». Пока доступ не пришёл — тоже
  // не рисуем (NO_ACCESS): показать и убрать хуже, чем показать позже.
  const can = useCan();
  // Создать комплект — право правки документов ИД, а не доступ к модулю: у роли с одним чтением
  // кнопка отвечала бы 403. Правка раздела — право правки строек, по той же причине.
  const canAddSet = idOn && can.permission('id.document.edit');
  const canEdit = can.permission('core.constructions.edit');

  const [addSetOpen, setAddSetOpen] = useState(false);
  const [newSetName, setNewSetName] = useState('');
  const [addError, setAddError] = useState('');
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const purge = useScopePurge('section', () => setDeleteConfirm(false),
    () => navigate(`/document-sets/${constructionId}`));
  const createSet = useCreateDocumentSet();
  const renameSection = useRenameSection();
  const deleteSection = useDeleteSection();

  useDocumentTitle((() => {
    const s = construction?.sections.find(s => s.id === sectionId);
    return s ? `Раздел «${s.name}»` : null;
  })());

  if (isLoading) return <div className="p-6 text-body-medium text-on-surface-variant">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-body-medium text-error">Стройка не найдена</div>;
  const section = construction.sections.find(s => s.id === sectionId);
  if (!section) return <div className="p-6 text-body-medium text-error">Раздел не найден</div>;

  const setsInSection = section.documentSets.length;
  const docsInSection = section.documentSets.reduce((acc, ds) => acc + (ds.documentCount ?? 0), 0);

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
      <Link to="/document-sets" className="text-body-small text-on-surface-variant hover:text-on-surface-variant transition-colors">Стройки</Link>
      <Link to={`/document-sets/${constructionId}`}
        className="text-label-small px-2 py-0.5 rounded-full border border-outline-variant text-on-surface-variant hover:border-outline hover:text-on-surface transition-colors">
        Стройка: {construction.name}
      </Link>
      <NotFavoriteNote constructionId={constructionId!} />
    </div>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      {panels.workNav}
      <HiddenByWorkspace module="id" what="Комплекты раздела" />
      {idOn && <>
        <NavSection label="Комплекты" />
        {section.documentSets.length === 0 && <p className="px-3 py-1.5 text-body-small text-on-surface-variant">Нет комплектов</p>}
        {section.documentSets.map(ds => {
          const p = problemOf(problems, ds.id);
          const done = planOf(plan, ds.id);
          return (
            <NavItem key={ds.id} icon={<Icon icon={FolderOpen} size={18} />} label={ds.name} count={ds.documentCount ?? 0} chevron
              progress={done?.percent ?? null} progressTitle={planTitle(done)}
              alert={p?.needsAttention} alertDanger={p?.hasArithmeticProblems}
              onClick={() => navigate(`/document-sets/${constructionId}/sets/${ds.id}`)} />
          );
        })}
        {canAddSet && (
          <button type="button" onClick={() => setAddSetOpen(true)}
            className="w-full flex items-center gap-2.5 px-3 h-9 rounded-full text-left text-body-medium text-primary hover:bg-primary-container transition-colors">
            <Icon icon={Add} size={18} className="shrink-0" /> Добавить комплект
          </button>
        )}
      </>}
      {panels.ownNav('Этот раздел')}
    </div>
  );

  const headerAction = (
    <>
      {canAddSet && <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAddSetOpen(true)}>Добавить комплект</Button>}
      {canEdit && (
        <RowActionsMenu ariaLabel="Действия раздела" actions={[
          { key: 'rename', label: 'Переименовать', icon: <Icon icon={Edit} size={18} />, onSelect: () => { setRenameVal(section.name); setRenameOpen(true); } },
          { key: 'delete', label: 'Удалить раздел', icon: <Icon icon={Delete} size={18} />, danger: true, onSelect: () => setDeleteConfirm(true) },
        ]} />
      )}
    </>
  );

  const detail = (
    <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
      {panels.detail}
    </div>
  );

  return (
    <>
      <ListDetailShell title={section.name} icon={<Icon icon={Layers} size={20} />} breadcrumb={contextCrumbs}
        badge={<PlanProgressBadge progress={plan?.own} />}
        actions={headerAction} nav={nav} detail={detail} backLabel="К разделам" />

      <Modal open={addSetOpen} onOpenChange={setAddSetOpen} title="Новый комплект"
        footer={
          <div className="flex justify-end gap-2">
            <ModalExit />
            <Button type="submit" form="add-set-form" variant="filled" loading={createSet.isPending}>
              {createSet.isPending ? 'Создание…' : 'Создать'}
            </Button>
          </div>
        }>
        {addSetOpen && (
          <form id="add-set-form" onSubmit={handleAddSet} className="space-y-4">
            <TextField label="Наименование" value={newSetName} onChange={e => setNewSetName(e.target.value)}
              required autoFocus hint="например: Кабельный журнал, секция 2" />
            {addError && <p className="text-body-medium text-error">{addError}</p>}
          </form>
        )}
      </Modal>

      <Modal open={renameOpen} onOpenChange={setRenameOpen} title="Переименовать раздел"
        footer={
          <div className="flex justify-end gap-2">
            <ModalExit />
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
        onRefused={purge.onRefused(section)}
      />
      {purge.dialog}
    </>
  );
}
