import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, Apartment, Star } from '@/shared/ui/icons';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Card } from '@/shared/ui/Card';
import { SearchInput } from '@/shared/ui/SearchInput';
import { TextField } from '@/shared/ui/TextField';
import { EmptyState } from '@/shared/ui/EmptyState';
import { problemOf } from '@/shared/api/reconciliations';
import { planOf } from '@/shared/api/plans';
import { ruCount } from '@/shared/utils/pluralize';
import { useListConstructions, useCreateConstruction } from '@/shared/api/constructions';
import { useSearchDocuments } from '@/shared/api/documentSets';
import { useCan } from '@/shared/api/access';
import { PALETTE_ACTIONS } from '@/shared/ui/paletteActions';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { usePaletteIntent } from '@/shared/ui/usePaletteIntent';
import { useIdIndicators } from './useIdIndicators';
import { HiddenByWorkspace } from '@/shared/ui/WorkspaceNav';
import { DocumentStatusText } from './fields';
import { useSiteFilter, useSiteFilterState } from '@/shared/siteFilter/siteFilterContext';
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
      <SearchInput value={text} onChange={setText} ariaLabel="Поиск документа"
        placeholder="Найти документ по всем комплектам (имя, тип, реквизиты)…" />
      {active && (
        <Card flush className="mt-2">
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
        </Card>
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
  // Звезды на карточке нет (Р7, sharedui-md3-17): избранное отмечают в шапке стройки, и удерживать на
  // экране снятую из-под курсора карточку (§62.2) больше нечего — список урезан отбором как есть.
  const shown = constructions.filter(c => filter.visible(c.id));
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
              // Карточка — одна цель (Р7, sharedui-md3-17): щелчок открывает стройку. Избранное, переименование и
              // удаление — в шапке стройки; здесь только знак «в избранном».
              <Card key={c.id} to={`/document-sets/${c.id}`} title={c.name} data-testid="construction-card">
                <div className="flex flex-col gap-3">
                {filter.favorites.has(c.id) && (
                  <span className="flex items-center gap-1 text-body-small text-on-surface-variant">
                    <Icon icon={Star} size={18} className="text-primary" /> в избранном
                  </span>
                )}
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
              </Card>
            );
          })}
        </div>
      )}

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новая стройка"
        footer={
          <>
            <ModalExit />
            <ModalPrimary type="submit" form="create-construction-form" loading={createMutation.isPending}>
              {createMutation.isPending ? 'Создание…' : 'Создать'}
            </ModalPrimary>
          </>}>
        {createOpen && (
          <form id="create-construction-form" onSubmit={handleCreate} className="space-y-4">
            <TextField label="Название стройки" value={newName} onChange={e => setNewName(e.target.value)}
              required autoFocus hint="например: ЖК Северный, корпус 1" />
            {createError && <p className="text-body-medium text-error">{createError}</p>}
          </form>
        )}
      </Modal>
    </div>
  );
}
