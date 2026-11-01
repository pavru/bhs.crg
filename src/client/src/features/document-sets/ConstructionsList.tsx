import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Apartment, Close, Delete, Edit, Search } from '@/shared/ui/icons';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { useScopePurge } from './useScopePurge';
import { problemOf } from '@/shared/api/reconciliations';
import { planOf } from '@/shared/api/plans';
import { ruCount } from '@/shared/utils/pluralize';
import { useListConstructions, useCreateConstruction, useRenameConstruction, useDeleteConstruction } from '@/shared/api/constructions';
import { useSearchDocuments } from '@/shared/api/documentSets';
import { useCan } from '@/shared/api/access';
import { PALETTE_ACTIONS } from '@/shared/ui/paletteActions';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { usePaletteIntent } from '@/shared/ui/usePaletteIntent';
import { useIdIndicators } from './useIdIndicators';
import { HiddenByWorkspace } from '@/shared/ui/WorkspaceNav';
import type { Construction } from '@/shared/api/types';
import { DocumentStatusText } from './fields';
import { useSiteFilter, useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
import { FavoriteStar } from '@/shared/siteFilter/SiteFilterNotes';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { apiError } from '@/shared/utils/apiError';
import { PageHeader } from '@/shared/ui/PageHeader';
import { documentPath } from './documentPath';
import { StatusText } from '@/shared/ui/StatusText';

// ── Список строек и поиск по документам ────────────────────────────────────────────────────────
// Выделено из DocumentSetsPage (#488): страница была роутером и четырьмя независимыми
// экранами в одном файле на 1051 строку. Экраны ничего не разделяют между собой, кроме
// импортов, — перенос без изменения поведения.

function DocumentSearchPanel() {
  const navigate = useNavigate();
  const [text, setText] = useState('');
  const [q, setQ] = useState('');
  useEffect(() => { const t = setTimeout(() => setQ(text), 300); return () => clearTimeout(t); }, [text]);
  const { data: results = [], isFetching } = useSearchDocuments(q);
  const active = q.trim().length > 0;
  // Поиск по избранному НЕ фильтруется (#1217): ищут именно то, чего не видят. Пометка — чтобы
  // найденное в неизбранной стройке не удивило пустым списком, куда ведёт крошка «Стройки».
  const filter = useSiteFilterState();

  return (
    <div className="mb-5">
      <div className="relative">
        <Icon icon={Search} size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant pointer-events-none" />
        <input value={text} onChange={e => setText(e.target.value)}
          placeholder="Найти документ по всем комплектам (имя, тип, реквизиты)…"
          className="w-full pl-9 pr-8 py-2 text-body-medium border border-outline rounded-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
        {text && (
          <IconButton label="Очистить" onClick={() => setText('')}
            className="absolute right-1.5 top-1/2 -translate-y-1/2" icon={Close} />
        )}
      </div>
      {active && (
        <div className="mt-2 border border-outline-variant rounded-md overflow-hidden bg-surface">
          {isFetching && results.length === 0 ? (
            <div className="px-3 py-3 text-body-small text-on-surface-variant">Поиск…</div>
          ) : results.length === 0 ? (
            <div className="px-3 py-3 text-body-small text-on-surface-variant">Ничего не найдено по «{q.trim()}»</div>
          ) : (
            <ul className="divide-y divide-surface-container max-h-96 overflow-y-auto">
              {results.map(r => (
                <li key={r.instanceId}>
                  <button onClick={() => navigate(documentPath(r.constructionId, r.setId, r.instanceId))}
                    className="w-full text-left px-3 py-2 state-layer flex items-center gap-3">
                    <div className="min-w-0 flex-1">
                      <div className="text-body-medium text-on-surface truncate">{r.name || r.typeName}</div>
                      <div className="text-body-small text-on-surface-variant truncate">
                        {r.constructionName} › {r.sectionName} › {r.setName}
                        {r.name && <span> · {r.typeName}</span>}
                        {filter.active && !filter.favorites.has(r.constructionId) && <span> · ☆ не в избранном</span>}
                      </div>
                    </div>
                    <DocumentStatusText status={r.status} />
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


export function ConstructionsList() {

  const navigate = useNavigate();
  const [createOpen, setCreateOpen] = useState(false);
  // Та же кнопка из командной палитры (issue #1433).
  usePaletteIntent(PALETTE_ACTIONS.constructionNew, () => setCreateOpen(true));
  const [newName, setNewName] = useState('');
  const [createError, setCreateError] = useState('');

  const { data: constructions = [], isLoading } = useListConstructions();
  // Список строек — первый потребитель избранного (#1218): урезается, и отбор «Стройки» стоит над ним (#1432).
  const filter = useSiteFilter();
  const toast = useSnackbar();
  // Стройки, оставленные на экране после щелчка по звезде: снятая не исчезает из-под курсора, а
  // первая звезда не сжимает список до одной карточки, пока человек не ушёл с экрана (§62.2).
  const [kept, setKept] = useState<ReadonlySet<string>>(() => new Set());
  // Переключили отбор «Стройки» — это уже явное «покажи иначе», и удержание снимается: иначе отбор
  // говорил бы «Избранные» над неурезанным списком (ревью PR #1331).
  const [keptOn, setKeptOn] = useState(filter.on);
  if (keptOn !== filter.on) {
    setKeptOn(filter.on);
    setKept(new Set());
  }
  const shown = constructions.filter(c => filter.visible(c.id) || kept.has(c.id));
  // Пункты модуля ИД — комплекты, их счётчики, готовность и «требует разбора» — ещё и по пространству
  // (ТЗ AUTH-16.1, issue #1001): страница стройки показывает пункты модулей открытого пространства.
  const { shown: idOn, plan: plans, problems } = useIdIndicators('System');
  // Поиск идёт по комплектам — это модуль ИД (issue #1125). Без модуля поле отвечало бы
  // «Ничего не найдено» на любой запрос: отказ сервера выглядел бы пустым результатом.
  // Правка строек — своё право (issue #1128): «Бухгалтер» видит стройки, но не правит их, и каждая
  // кнопка правки отвечала бы ему отказом.
  const can = useCan();
  const canEdit = can.permission('core.constructions.edit');
  const createMutation = useCreateConstruction();
  const deleteMutation = useDeleteConstruction();
  const renameMutation = useRenameConstruction();
  const [editId, setEditId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [deleteTarget, setDeleteTarget] = useState<Construction | null>(null);
  const purge = useScopePurge('construction', () => setDeleteTarget(null));

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setCreateError('');
    try {
      const c = await createMutation.mutateAsync(newName);
      setCreateOpen(false);
      setNewName('');
      // Новая стройка — в избранное создателя, если оно уже непусто (#1217): иначе, вернувшись, он
      // не нашёл бы её в списке. Это вторая запись, не атомарная с созданием, — отказ говорится.
      if (filter.favorites.size > 0) {
        filter.setFavorite(c.id, true).catch(e =>
          toast.error(`Стройка создана, но в избранное не добавлена: ${apiError(e)}`));
      }
      navigate(`/document-sets/${c.id}`);
    } catch (err: unknown) { setCreateError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  async function commitRename(c: Construction) {
    if (!editName.trim() || editName === c.name) { setEditId(null); return; }
    await renameMutation.mutateAsync({ id: c.id, name: editName });
    setEditId(null);
  }

  return (
    // Под концом списка — место для плавающей кнопки, пока она есть (#1434, `--fab-clearance`).
    <div className="px-6 pt-4 pb-[calc(1rem+var(--fab-clearance,0px))]">
      {/* На телефоне «Новая стройка» — плавающей кнопкой (#1434), и здесь, и в пустом состоянии. */}
      <PageHeader title="Стройки" className="mb-4" actions={canEdit && (
        <PrimaryActionButton action={PALETTE_ACTIONS.constructionNew} icon={<Icon icon={Add} size={18} />} onClick={() => setCreateOpen(true)} />
      )} />

      {idOn ? <DocumentSearchPanel /> : <HiddenByWorkspace module="id" what="Поиск документов и комплекты строек" />}

      <div className="mb-3 flex"><SitesFilter hidden={constructions.length - shown.length} /></div>

      {isLoading || filter.status === 'loading' ? (
        <div className="text-center py-10 text-on-surface-variant text-body-medium">Загрузка...</div>
      ) : constructions.length === 0 ? (
        <EmptyState icon={<Icon icon={Apartment} size={40} />} title="Пока нет строек"
          description="Создайте первую стройку, чтобы начать вести исполнительную документацию по её разделам и комплектам."
          action={canEdit ? <PrimaryActionButton action={PALETTE_ACTIONS.constructionNew} icon={<Icon icon={Add} size={18} />}
            onClick={() => setCreateOpen(true)} /> : undefined} />
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {shown.map(c => {
            const setsCount = c.sections.reduce((acc, s) => acc + s.documentSets.length, 0);
            return (
              <div key={c.id} className={`bg-surface border border-outline-variant rounded-lg p-5 flex flex-col gap-3 hover:border-primary hover:shadow-elevation-2 transition-all group cursor-pointer ${filter.visible(c.id) ? '' : 'opacity-60'}`}
                onClick={() => editId !== c.id && navigate(`/document-sets/${c.id}`)}>
                <div className="flex items-start justify-between gap-2">
                  <FavoriteStar id={c.id} onToggled={() => setKept(new Set(shown.map(x => x.id)))} />
                  {editId === c.id ? (
                    <input value={editName} onChange={e => setEditName(e.target.value)}
                      onBlur={() => commitRename(c)}
                      onKeyDown={e => { if (e.key === 'Enter') commitRename(c); if (e.key === 'Escape') { e.stopPropagation(); setEditId(null); } }}
                      autoFocus onClick={e => e.stopPropagation()}
                      className="flex-1 text-body-large font-semibold border-b border-primary bg-transparent outline-none" />
                  ) : (
                    <h3 className="text-body-large font-semibold text-on-surface flex-1">{c.name}</h3>
                  )}
                  {canEdit && <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity shrink-0">
                    <IconButton label="Переименовать"
                      onClick={e => { e.stopPropagation(); setEditId(c.id); setEditName(c.name); }} icon={Edit} />
                    <IconButton label="Удалить" danger
                      onClick={e => { e.stopPropagation(); setDeleteTarget(c); }} icon={Delete} />
                  </div>}
                </div>
                <div className="flex items-center gap-4 text-body-small text-on-surface-variant">
                  <span>{ruCount(c.sections.length, 'раздел', 'раздела', 'разделов')}</span>
                  {/* Комплекты — модуль ИД (issue #1128): без него счётчика нет, а не «0 комплектов». */}
                  {idOn && <span>{ruCount(setsCount, 'комплект', 'комплекта', 'комплектов')}</span>}
                  {/* Строкой, а не пилюлей: красные пилюли на карточках дают «ёлку» раньше всего.
                      Готовность — там же и тем же тоном: это соседняя по смыслу цифра, а не бейдж. */}
                  {/* Оговорка «без плана» обязательна ВЕЗДЕ, где показан процент: стройка, где
                      расписан один комплект из десяти и он закрыт, иначе читалась бы как готовая
                      на 100 % — и именно на этом экране, с которого начинают работу. */}
                  {planOf(plans, c.id)?.percent != null && (
                    <span className="tabular-nums">
                      готовность: {planOf(plans, c.id)!.percent}%
                      {(planOf(plans, c.id)!.setsWithoutPlan > 0) && (
                        <span className="text-on-surface-variant">
                          {' '}(без плана: {ruCount(planOf(plans, c.id)!.setsWithoutPlan, 'комплект', 'комплекта', 'комплектов')})
                        </span>
                      )}
                    </span>
                  )}
                  {(problemOf(problems, c.id)?.needsAttention ?? 0) > 0 && (
                    <span className="text-warning">
                      требует разбора: {problemOf(problems, c.id)!.needsAttention}
                    </span>
                  )}
                </div>
                {c.sections.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {c.sections.slice(0, 4).map(s => (
                      <StatusText key={s.id}>{s.name}</StatusText>
                    ))}
                    {c.sections.length > 4 && (
                      <span className="text-body-small text-on-surface-variant">+{c.sections.length - 4} ещё</span>
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
            <ModalExit />
            <Button type="submit" form="create-construction-form" variant="filled" loading={createMutation.isPending}>
              {createMutation.isPending ? 'Создание…' : 'Создать'}
            </Button>
          </div>
        }>
        {createOpen && (
          <form id="create-construction-form" onSubmit={handleCreate} className="space-y-4">
            <TextField label="Название стройки" value={newName} onChange={e => setNewName(e.target.value)}
              required autoFocus hint="например: ЖК Северный, корпус 1" />
            {createError && <p className="text-body-medium text-error">{createError}</p>}
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
          const docsN = deleteTarget.sections.reduce((acc, s) => acc + s.documentSets.reduce((a, ds) => a + (ds.documentCount ?? 0), 0), 0);
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
        onConfirm={async () => {
          // mutateAsync ради видимости отказа 409 (issue #739) — см. ConstructionDetail.
          if (deleteTarget) await deleteMutation.mutateAsync(deleteTarget.id);
        }}
        onRefused={purge.onRefused(deleteTarget)}
      />
      {purge.dialog}
    </div>
  );
}
