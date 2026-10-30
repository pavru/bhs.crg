import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Archive, Check, Close, Delete, Edit, Unarchive } from '@/shared/ui/icons';
import { ArchivedRows } from '@/shared/ui/ArchivedRows';
import { Button, IconButton } from '@/shared/ui/Button';
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
            onKeyDown={e => { if (e.key === 'Enter') void rename(); if (e.key === 'Escape') { e.stopPropagation(); setEditing(null); } }}
            className={FIELD} />
          <IconButton label="Сохранить название" onClick={() => void rename()} icon={Check} />
          <IconButton label="Отменить" onClick={() => setEditing(null)} icon={Close} />
        </>
      ) : (
        <>
          <span className={`flex-1 ${article.archived ? 'text-on-surface-variant' : ''}`}>{article.name}</span>
          <IconButton label={`Переименовать «${article.name}»`}
            onClick={() => setEditing({ id: article.id, name: article.name })} icon={Edit} />
          {article.archived ? (
            <IconButton label={`Вернуть «${article.name}» из архива`}
              disabled={archive.isPending}
              onClick={() => void setArchived(article, false)} icon={Unarchive} />
          ) : (
            <IconButton label={`В архив «${article.name}»`}
              disabled={archive.isPending}
              onClick={() => void setArchived(article, true)} icon={Archive} />
          )}
          <IconButton label={`Удалить «${article.name}»`}
            danger
            onClick={() => setDeleting(article)} icon={Delete} />
        </>
      )}
    </li>
  );

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Статьи вне строек" isDirty={!!name.trim() || !!editing}>
      <p className="text-body-small text-on-surface-variant mb-3">
        Цели разноски, которые не стройка: склад, общие расходы. В выборе «куда» они идут группой «Вне строек».
      </p>

      {articles.isError && <p className="text-body-small text-error mb-2">Справочник не загрузился — {String(articles.error)}</p>}

      <div className="border-y border-outline-variant text-body-medium mb-3">
        <ul className="divide-y divide-outline-variant">
          {articles.data && live.length === 0 && (
            <li className="py-2 text-on-surface-variant text-body-small">{gone.length ? 'Действующих статей нет.' : 'Статей пока нет.'}</li>
          )}
          {live.map(row)}
        </ul>
        {/* Раскрытие — рядом со списком, а не внутри него: кнопка не пункт списка. */}
        <ArchivedRows count={gone.length}>
          <ul className="divide-y divide-outline-variant border-t border-outline-variant">{gone.map(row)}</ul>
        </ArchivedRows>
      </div>

      <div className="flex items-center gap-2">
        <input value={name} placeholder="Например, «Склад»" aria-label="Новая статья"
          onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') void add(); }}
          className={FIELD} />
        <Button variant="outlined" icon={<Icon icon={Add} size={18} />} disabled={!name.trim()}
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

const FIELD = `flex-1 rounded-xs border border-outline-variant bg-surface px-2 py-1 text-body-medium text-on-surface outline-none focus:border-primary`;
