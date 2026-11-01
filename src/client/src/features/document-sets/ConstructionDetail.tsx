import { useState } from 'react';
import { useNavigate, useParams, Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Apartment, Delete, Edit, Layers } from '@/shared/ui/icons';
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
import { useGetConstruction, useRenameConstruction, useDeleteConstruction, useCreateSection } from '@/shared/api/constructions';
import { useCan } from '@/shared/api/access';
import { useIdIndicators } from './useIdIndicators';
import { useScopePanels } from './scopePanels';

// ── Экран стройки ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

export function ConstructionDetail() {

  const { constructionId } = useParams<{ constructionId: string }>();
  const navigate = useNavigate();
  const { data: construction, isLoading } = useGetConstruction(constructionId!);
  // Панели «Работы» и «Эта стройка» — из реестра (plan-02): отбор по правам и пространству, панель по
  // умолчанию и перенаправление голого адреса на неё.
  const base = `/document-sets/${constructionId}`;
  const panels = useScopePanels('construction', base, { constructionId: constructionId! });
  // Пункты модуля ИД — комплекты, их счётчики, готовность и «требует разбора» — ещё и по пространству
  // (ТЗ AUTH-16.1, issue #1001): страница стройки показывает пункты модулей открытого пространства.
  const { shown: idOn, plan, problems } = useIdIndicators('Construction', constructionId);
  // Счётчик комплектов у раздела — число модуля ИД (issue #1128): без модуля его нет, а не ноль.
  // Правка стройки и её разделов — право core.constructions.edit: без него кнопки отвечали бы 403.
  const can = useCan();
  const canEdit = can.permission('core.constructions.edit');
  const [addSectionOpen, setAddSectionOpen] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [sectionError, setSectionError] = useState('');
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameVal, setRenameVal] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  // Отказ «держат данные выключенного модуля» несёт выход — удалить, потеряв ссылки (issue #1243).
  const purge = useScopePurge('construction', () => setDeleteConfirm(false), () => navigate('/document-sets'));
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

  if (isLoading) return <div className="p-6 text-body-medium text-on-surface-variant">Загрузка...</div>;
  if (!construction) return <div className="p-6 text-body-medium text-error">Стройка не найдена</div>;

  const sectionsN = construction.sections.length;
  const setsN = construction.sections.reduce((a, s) => a + s.documentSets.length, 0);
  const docsN = construction.sections.reduce((a, s) => a + s.documentSets.reduce((x, ds) => x + (ds.documentCount ?? 0), 0), 0);

  const contextCrumbs = (
    <div className="flex items-center gap-1.5 flex-wrap">
      <Link to="/document-sets" className="text-body-small text-on-surface-variant hover:text-on-surface-variant transition-colors">Стройки</Link>
      {/* Прямая ссылка на неизбранную стройку (#1218): открыта полностью, а крошка ведёт в список,
          где её нет, — строка говорит почему. */}
      <NotFavoriteNote constructionId={constructionId!} />
    </div>
  );

  const nav = (
    <div className="flex-1 overflow-y-auto px-2 pb-3 pt-2 space-y-0.5">
      {panels.workNav}
      <NavSection label="Разделы" />
      {construction.sections.length === 0 && <p className="px-3 py-1.5 text-body-small text-on-surface-variant">Нет разделов</p>}
      {construction.sections.map(s => {
        const p = problemOf(problems, s.id);
        const done = planOf(plan, s.id);
        return (
          <NavItem key={s.id} icon={<Icon icon={Layers} size={18} />} label={s.name} count={idOn ? s.documentSets.length : undefined} chevron
            progress={done?.percent ?? null} progressTitle={planTitle(done)}
            alert={p?.needsAttention} alertDanger={p?.hasArithmeticProblems}
            onClick={() => navigate(`/document-sets/${constructionId}/sections/${s.id}`)} />
        );
      })}
      {canEdit && (
        <button type="button" onClick={() => setAddSectionOpen(true)}
          className="w-full flex items-center gap-2.5 px-3 h-9 rounded-full text-left text-body-medium text-primary hover:bg-primary-container transition-colors">
          <Icon icon={Add} size={18} className="shrink-0" /> Добавить раздел
        </button>
      )}
      {panels.ownNav('Эта стройка')}
    </div>
  );

  const headerAction = (
    <>
      {canEdit && <>
        <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAddSectionOpen(true)}>Добавить раздел</Button>
        <RowActionsMenu ariaLabel="Действия стройки" actions={[
          { key: 'rename', label: 'Переименовать', icon: <Icon icon={Edit} size={18} />, onSelect: () => { setRenameVal(construction.name); setRenameOpen(true); } },
          { key: 'delete', label: 'Удалить стройку', icon: <Icon icon={Delete} size={18} />, danger: true, onSelect: () => setDeleteConfirm(true) },
        ]} />
      </>}
    </>
  );

  const detail = (
    <div className="flex-1 min-h-0 overflow-y-auto px-6 py-5">
      {panels.detail}
    </div>
  );

  return (
    <>
      <ListDetailShell title={construction.name} icon={<Icon icon={Apartment} size={20} />} breadcrumb={contextCrumbs}
        badge={<PlanProgressBadge progress={plan?.own} />}
        actions={headerAction} nav={nav} detail={detail} backLabel="К разделам" />

      <Modal open={addSectionOpen} onOpenChange={setAddSectionOpen} title="Новый раздел"
        footer={
          <div className="flex justify-end gap-2">
            <ModalExit />
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
            {sectionError && <p className="text-body-medium text-error">{sectionError}</p>}
          </form>
        )}
      </Modal>

      <Modal open={renameOpen} onOpenChange={setRenameOpen} title="Переименовать стройку"
        footer={
          <div className="flex justify-end gap-2">
            <ModalExit />
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
        onConfirm={async () => {
          // mutateAsync, а не mutate: удаление уровня может отказать 409 («на содержимое ссылаются
          // извне», issue #739). Fire-and-forget закрывал бы диалог и уводил со страницы так, будто
          // всё удалилось, — отказ не увидел бы никто. ConfirmDialog на reject остаётся открытым и
          // показывает причину, поэтому и навигация только после успеха.
          await deleteConstruction.mutateAsync(construction.id);
          navigate('/document-sets');
        }}
        onRefused={purge.onRefused(construction)}
      />
      {purge.dialog}
    </>
  );
}
