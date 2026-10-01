import { useState } from 'react';
import { Check, Pencil, Plus, Trash2, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { useCostsArticles, useDeleteArticle, useSaveArticle, type CostsArticle } from '@/shared/api/articles';

/**
 * Справочник статей вне строек — «Склад», «Общие расходы» (задача F3, issue #1087, ТЗ COST-10.1).
 *
 * <p>Ведёт его тот, у кого `costs.articles.edit` — бухгалтер, — и ему не нужен ради этого раздел «Общие
 * данные» с правом править все справочники. Статьи появляются в выборе цели разноски отдельной группой
 * «Вне строек».</p>
 *
 * <p>Убрать статью, на которую уже что-то разнесено, сервер не даст — и скажет, сколько частей мешает.
 * Отказ показывается в том же диалоге подтверждения, а не всплывашкой мимо.</p>
 */
export function ArticlesDialog({ onClose }: { onClose: () => void }) {
  const articles = useCostsArticles();
  const save = useSaveArticle();
  const remove = useDeleteArticle();
  const toast = useToast();

  const [name, setName] = useState('');
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const [deleting, setDeleting] = useState<CostsArticle | null>(null);

  async function add() {
    if (!name.trim()) return;
    try {
      await save.mutateAsync({ name });
      setName('');
    } catch (e) { toast.apiError(e, 'Статья не заведена'); }
  }

  async function rename() {
    if (!editing) return;
    try {
      await save.mutateAsync(editing);
      setEditing(null);
    } catch (e) { toast.apiError(e, 'Статья не переименована'); }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Статьи вне строек" isDirty={!!name.trim() || !!editing}>
      <p className="text-xs text-fg3 mb-3">
        Цели разноски, которые не стройка: склад, общие расходы. В выборе «куда» они идут группой «Вне строек».
      </p>

      {articles.isError && <p className="text-xs text-danger mb-2">Справочник не загрузился — {String(articles.error)}</p>}

      <ul className="divide-y divide-stroke border-y border-stroke text-sm mb-3">
        {articles.data?.length === 0 && <li className="py-2 text-fg4 text-xs">Статей пока нет.</li>}
        {articles.data?.map(article => (
          <li key={article.id} className="flex items-center gap-2 py-1.5">
            {editing?.id === article.id ? (
              <>
                <input value={editing.name} autoFocus aria-label={`Название статьи «${article.name}»`}
                  onChange={e => setEditing({ ...editing, name: e.target.value })}
                  onKeyDown={e => { if (e.key === 'Enter') void rename(); if (e.key === 'Escape') setEditing(null); }}
                  className={FIELD} />
                <button type="button" title="Сохранить название" onClick={() => void rename()}
                  className="text-fg3 hover:text-primary p-1"><Check size={14} /></button>
                <button type="button" title="Отменить" onClick={() => setEditing(null)}
                  className="text-fg3 hover:text-fg p-1"><X size={14} /></button>
              </>
            ) : (
              <>
                <span className="flex-1">{article.name}</span>
                <button type="button" title={`Переименовать «${article.name}»`}
                  onClick={() => setEditing({ id: article.id, name: article.name })}
                  className="text-fg3 hover:text-primary p-1"><Pencil size={14} /></button>
                <button type="button" title={`Убрать «${article.name}»`} onClick={() => setDeleting(article)}
                  className="text-fg3 hover:text-danger p-1"><Trash2 size={14} /></button>
              </>
            )}
          </li>
        ))}
      </ul>

      <div className="flex items-center gap-2">
        <input value={name} placeholder="Например, «Склад»" aria-label="Новая статья"
          onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') void add(); }}
          className={FIELD} />
        <Button size="sm" variant="outlined" icon={<Plus size={14} />} disabled={!name.trim()}
          loading={save.isPending && !editing} onClick={() => void add()}>
          Добавить
        </Button>
      </div>

      <ConfirmDialog open={deleting !== null} onOpenChange={o => { if (!o) setDeleting(null); }}
        title={`Убрать статью «${deleting?.name ?? ''}»?`}
        description="Из выбора цели разноски она исчезнет. Статью, на которую уже что-то разнесено, убрать нельзя."
        confirmLabel="Убрать статью" errorTitle="Статью убрать нельзя"
        onConfirm={() => remove.mutateAsync(deleting!.id)} />
    </Modal>
  );
}

const FIELD = `flex-1 rounded border border-stroke bg-surface px-2 py-1 text-sm text-fg outline-none focus:border-primary`;
