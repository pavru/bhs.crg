import { useState } from 'react';
import { isAxiosError } from 'axios';
import { useCan } from '@/shared/api/access';
import {
  downloadAttachmentFile, useDeleteAttachment, type EstimateAttachment,
} from '@/shared/api/planningConjuncture';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { AttachFile, Delete, Download } from '@/shared/ui/icons';
import { LIST_CONTAINER, ListItem } from '@/shared/ui/ListItem';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { StatusText } from '@/shared/ui/StatusText';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { apiError } from '@/shared/utils/apiError';
import {
  attachedWords, attachmentKey, attachmentSummary, attachmentTitle, KIND_LABEL, KIND_NAME, partialWords,
} from './attachmentRows';
import { ConjunctureView } from './ConjunctureView';
import { useAttachmentsOf } from './useAttachmentsOf';
import { useEstimateEdit } from './useEstimateEdit';
import { VolumeStatementView } from './VolumeStatementView';

/**
 * Приложения сметы — панель «Приложения» карточки (change `plan-11-conjuncture`, макет П11, вариант 1): список (КА и
 * ВОР вместе, новые сверху), под ним раскрыто выбранное. Только чтение, скачивание и удаление: система приложения
 * хранит и показывает, а не ведёт.
 *
 * - Скачать файл КА — только с `plan.estimate.cost`: в нём все цены (сервер без права отвечает 403). ВОР — всем,
 *   кто читает смету. Недоступное скрыто.
 * - Удалить — с `plan.estimate.edit` на широком экране, подтверждением с именем, видом, числом позиций и кто
 *   приложил; «уже удалили» (404) — не ошибка, а снэкбар.
 * - Что выбрано, решает карточка (адрес); здесь — только `selected` и `onSelect`. Не выбрано — раскрыто новое.
 *   Выбранного по адресу в списке нет (удалили, ссылка устарела) — не раскрыто ничего, и так и сказано: подставить
 *   другое приложение под чужую ссылку значило бы показать не тот документ, о котором шла речь (ревью PR #1845).
 */
export function EstimateAttachments({ estimateId, selected, onSelect, attach }: {
  estimateId: string;
  /** Ключ выбранного приложения (`ka:<id>`), `null` — не выбрано. */
  selected: string | null;
  onSelect: (key: string | null) => void;
  /** Кнопка «Приложить файл…» — её даёт карточка, когда окно загрузки приложений готово; нет — кнопки нет. */
  attach?: () => void;
}) {
  const { all, error } = useAttachmentsOf(estimateId);
  const editable = useEstimateEdit();
  const can = useCan();
  const toast = useSnackbar();
  const del = useDeleteAttachment();
  const [deleting, setDeleting] = useState<EstimateAttachment | null>(null);

  if (error) return <p role="alert" className="text-body-medium text-error">{apiError(error, 'Приложения сметы не прочитались')}</p>;
  if (!all) return <p className="text-body-medium text-on-surface-variant">Читаем приложения сметы…</p>;

  const found = all.find(a => attachmentKey(a) === selected) ?? null;
  const stale = selected !== null && found === null;
  const opened = stale ? null : found ?? all[0] ?? null;
  const downloadable = (a: EstimateAttachment) => a.kind === 'vor' || can.permission('plan.estimate.cost');
  const download = (a: EstimateAttachment) => {
    downloadAttachmentFile(a).catch(e => toast.apiError(e, 'Файл не скачался'));
  };
  const actions = (a: EstimateAttachment): RowAction[] => [
    ...(downloadable(a) ? [{ key: 'download', label: 'Скачать файл', icon: Download, onSelect: () => download(a) }] : []),
    ...(editable ? [{ key: 'delete', label: 'Удалить…', icon: Delete, danger: true, onSelect: () => setDeleting(a) }] : []),
  ];
  const remove = async (a: EstimateAttachment) => {
    try {
      await del.mutateAsync(a);
      toast.success(`Приложение «${attachmentTitle(a)}» удалено`);
    } catch (e) {
      if (!(isAxiosError(e) && e.response?.status === 404)) throw e;
      toast.info('Это приложение уже удалили');
    }
    if (opened && attachmentKey(opened) === attachmentKey(a)) onSelect(null);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <p className="flex-1 min-w-0 text-body-small text-on-surface-variant">
          КА и ВОР из ГРАНД-Сметы хранятся рядом с редакциями. На план стройки и «Ход работ» они не влияют.
        </p>
        {attach && editable && (
          <Button variant="text" icon={<Icon icon={AttachFile} size={18} aria-hidden />} onClick={attach}>Приложить файл…</Button>
        )}
      </div>

      {all.length === 0 ? (
        <EmptyState icon={<Icon icon={AttachFile} size={48} aria-hidden />} title="Приложений нет"
          description="Конъюнктурный анализ и ведомость объёмов работ из ГРАНД-Сметы прикладываются к смете файлом." />
      ) : (
        <ul aria-label="Приложения сметы" className={`${LIST_CONTAINER} rounded-md border border-outline-variant`}>
          {all.map(a => {
            const partial = partialWords(a);
            const menu = actions(a);
            return (
              <li key={attachmentKey(a)}>
                <ListItem onClick={() => onSelect(attachmentKey(a))} selected={opened !== null && attachmentKey(opened) === attachmentKey(a)}
                  leading={<span className="w-10 shrink-0 text-label-large text-on-surface-variant">{KIND_LABEL[a.kind]}</span>}
                  headline={attachmentTitle(a)} supporting={`${attachmentSummary(a)} · ${attachedWords(a)}`}
                  trailing={partial ? <StatusText tone="warning">{partial}</StatusText> : undefined}
                  action={menu.length ? <RowActionsMenu actions={menu} ariaLabel={`Действия с «${attachmentTitle(a)}»`} /> : undefined} />
              </li>
            );
          })}
        </ul>
      )}

      {stale && all.length > 0 && (
        <p role="status" className="text-body-medium text-on-surface-variant">
          Приложения, на которое ведёт ссылка, у сметы нет — возможно, его удалили. Выберите приложение в списке.
        </p>
      )}

      {opened && (
        <section aria-label={`${KIND_NAME[opened.kind]} «${attachmentTitle(opened)}»`} className="space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="flex-1 min-w-0 text-title-small">
              {KIND_NAME[opened.kind]}
              {opened.programVersion && <span className="text-body-small text-on-surface-variant"> · программа {opened.programVersion}</span>}
              {opened.kind === 'vor' && <span className="text-body-small text-on-surface-variant"> · на план и «Ход работ» не влияет</span>}
            </h3>
            {downloadable(opened) && (
              <Button variant="text" icon={<Icon icon={Download} size={18} aria-hidden />} onClick={() => download(opened)}>Скачать файл</Button>
            )}
          </div>
          {opened.kind === 'ka' ? <ConjunctureView analysisId={opened.id} /> : <VolumeStatementView statementId={opened.id} />}
        </section>
      )}

      {deleting && (
        <ConfirmDialog open onOpenChange={open => { if (!open) setDeleting(null); }}
          title={`Удалить ${KIND_LABEL[deleting.kind]}?`} confirmLabel="Удалить" confirmDanger
          description={(
            <span className="flex flex-col gap-1">
              <span>«{attachmentTitle(deleting)}» — {KIND_NAME[deleting.kind].toLowerCase()}, {attachmentSummary(deleting)}.</span>
              <span className="text-on-surface-variant">{attachedWords(deleting)}. Файл удаляется вместе с приложением; редакции сметы и план стройки не меняются.</span>
            </span>
          )}
          onConfirm={() => remove(deleting)} />
      )}
    </div>
  );
}
