import { useState } from 'react';
import { Archive, ArchiveRestore, Check, Pencil, Plus, Trash2, X } from 'lucide-react';
import { ArchivedRows } from '@/shared/ui/ArchivedRows';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog, type RefusalExit } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import {
  useCostsArticles, useDeleteArticle, useSaveArticle, useSetArticleArchive, type CostsArticle,
} from '@/shared/api/articles';

/**
 * Справочник статей вне строек — «Склад», «Общие расходы» (задача F3, issue #1087, ТЗ COST-10.1).
 *
 * <p>Ведёт его тот, у кого `costs.articles.edit` — бухгалтер, — и ему не нужен ради этого раздел «Общие
 * данные» с правом править все справочники. Статьи появляются в выборе цели разноски отдельной группой
 * «Вне строек».</p>
 *
 * <p>Убрать статью, на которую уже что-то разнесено, сервер не даст — и скажет, сколько частей мешает.
 * Отказ показывается в том же диалоге подтверждения, а не всплывашкой мимо, и предлагает выход:
 * отправить статью в архив (issue #1185). Архивная статья из выбора цели уходит, в старой разноске
 * остаётся; лежит она внизу, под «В архиве: N», и возвращается одной кнопкой.</p>
 */
export function ArticlesDialog({ onClose }: { onClose: () => void }) {
  const articles = useCostsArticles();
  const save = useSaveArticle();
  const remove = useDeleteArticle();
  const archive = useSetArticleArchive();
  const toast = useToast();

  const [name, setName] = useState('');
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const [deleting, setDeleting] = useState<CostsArticle | null>(null);

  const live = articles.data?.filter(a => !a.archived) ?? [];
  const gone = articles.data?.filter(a => a.archived) ?? [];

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

  /** Подтверждения нет: действие обратимо, статья остаётся в этом же окне — в «В архиве: N». */
  async function setArchived(article: CostsArticle, archived: boolean) {
    try {
      await archive.mutateAsync({ id: article.id, archived });
    } catch (e) {
      toast.apiError(e, archived ? 'Не удалось отправить в архив.' : 'Не удалось вернуть из архива.');
      return;
    }
    if (!archived) { toast.success(`Статья «${article.name}» возвращена из архива.`); return; }
    toast.success(`Статья «${article.name}» — в архиве. В выборе цели разноски её больше не будет.`, {
      duration: 8000,
      action: { label: 'Вернуть', onClick: () => void setArchived(article, false) },
    });
  }

  /**
   * Отказ удалить действующую статью — конфликт: на неё разнесено. Выход — архив. У архивной выхода
   * нет: она уже там, и звать туда значило бы обмануть.
   */
  function refusalExit(e: unknown): RefusalExit | null {
    const article = deleting;
    const status = (e as { response?: { status?: number } })?.response?.status;
    if (!article || article.archived || status !== 409) return null;
    return {
      note: 'В архиве статья исчезнет из выбора цели, а в счетах, где на неё разнесено, останется.',
      action: {
        label: 'Отправить в архив', errorTitle: 'В архив отправить не удалось',
        onConfirm: () => archive.mutateAsync({ id: article.id, archived: true }),
      },
    };
  }

  const row = (article: CostsArticle) => (
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
          <span className={`flex-1 ${article.archived ? 'text-fg3' : ''}`}>{article.name}</span>
          <button type="button" title={`Переименовать «${article.name}»`}
            onClick={() => setEditing({ id: article.id, name: article.name })}
            className="text-fg3 hover:text-primary p-1"><Pencil size={14} /></button>
          {article.archived ? (
            <button type="button" title={`Вернуть «${article.name}» из архива`} disabled={archive.isPending}
              onClick={() => void setArchived(article, false)}
              className="text-fg3 hover:text-primary p-1"><ArchiveRestore size={14} /></button>
          ) : (
            <button type="button" title={`В архив «${article.name}»`} disabled={archive.isPending}
              onClick={() => void setArchived(article, true)}
              className="text-fg3 hover:text-primary p-1"><Archive size={14} /></button>
          )}
          <button type="button" title={`Удалить «${article.name}»`} onClick={() => setDeleting(article)}
            className="text-fg3 hover:text-danger p-1"><Trash2 size={14} /></button>
        </>
      )}
    </li>
  );

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Статьи вне строек" isDirty={!!name.trim() || !!editing}>
      <p className="text-xs text-fg3 mb-3">
        Цели разноски, которые не стройка: склад, общие расходы. В выборе «куда» они идут группой «Вне строек».
      </p>

      {articles.isError && <p className="text-xs text-danger mb-2">Справочник не загрузился — {String(articles.error)}</p>}

      <div className="border-y border-stroke text-sm mb-3">
        <ul className="divide-y divide-stroke">
          {articles.data && live.length === 0 && (
            <li className="py-2 text-fg4 text-xs">{gone.length ? 'Действующих статей нет.' : 'Статей пока нет.'}</li>
          )}
          {live.map(row)}
        </ul>
        {/* Раскрытие — рядом со списком, а не внутри него: кнопка не пункт списка. */}
        <ArchivedRows count={gone.length}>
          <ul className="divide-y divide-stroke border-t border-stroke">{gone.map(row)}</ul>
        </ArchivedRows>
      </div>

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
        title={`Удалить статью «${deleting?.name ?? ''}»?`}
        description="Статья будет удалена совсем. Если на неё уже что-то разнесено, удалить не получится — её отправляют в архив."
        confirmLabel="Удалить статью" errorTitle="Статью удалить нельзя"
        onConfirm={() => remove.mutateAsync(deleting!.id)} onRefused={refusalExit} />
    </Modal>
  );
}

const FIELD = `flex-1 rounded border border-stroke bg-surface px-2 py-1 text-sm text-fg outline-none focus:border-primary`;
