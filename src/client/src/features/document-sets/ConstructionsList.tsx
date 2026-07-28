import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { Plus, Trash2, Pencil, Building2, Search, X } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog, CascadeList } from '@/shared/ui/ConfirmDialog';
import { useProblemSummary, problemOf } from '@/shared/api/reconciliations';
import { ruCount } from '@/shared/utils/pluralize';
import { useListConstructions, useCreateConstruction, useRenameConstruction, useDeleteConstruction } from '@/shared/api/constructions';
import { useSearchDocuments } from '@/shared/api/documentSets';
import type { Construction } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from './fields';

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

export function ConstructionsList() {

  const navigate = useNavigate();
  const [createOpen, setCreateOpen] = useState(false);
  const [newName, setNewName] = useState('');
  const [createError, setCreateError] = useState('');

  const { data: constructions = [], isLoading } = useListConstructions();
  const { data: problems } = useProblemSummary('System');
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
                  {/* Строкой, а не пилюлей: красные пилюли на карточках дают «ёлку» раньше всего. */}
                  {(problemOf(problems, c.id)?.needsAttention ?? 0) > 0 && (
                    <span className="text-warning">
                      требует разбора: {problemOf(problems, c.id)!.needsAttention}
                    </span>
                  )}
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
        onConfirm={() => { if (deleteTarget) deleteMutation.mutate(deleteTarget.id); }}
      />
    </div>
  );
}
