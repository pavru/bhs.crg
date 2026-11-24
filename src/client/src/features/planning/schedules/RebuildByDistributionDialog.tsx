import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { readScheduleRebuild, scheduleRefusal, useRebuildSchedule } from '@/shared/api/planningSchedules';
import type { ScheduleRebuildPreview } from '@/shared/api/planningScheduleTypes';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid } from '@/shared/ui/DataGrid';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { sectionName, skippedText, volumeText, type ScheduleSection } from './scheduleDisplay';

type Change = { key: string; name: string; sectionId: string | null; volume: string; action: string };

function changesOf(preview: ScheduleRebuildPreview): Change[] {
  return [
    ...preview.added.map(row => ({ ...row, key: `add/${row.rowId}/${row.sectionId}`,
      action: 'Добавится', volume: volumeText(row.volume) })),
    ...preview.removed.map(row => ({ ...row, key: `remove/${row.workId}`, action: 'Будет снята', volume: volumeText(row.volume) })),
    ...preview.changed.map(row => ({ ...row, key: `change/${row.workId}`, action: 'Изменится объём',
      volume: `${volumeText(row.before)} → ${volumeText(row.after)}` })),
  ];
}

export function RebuildByDistributionDialog({ id, sections, onClose }: {
  id: string; sections: ScheduleSection[]; onClose: () => void;
}) {
  const query = useQuery({
    queryKey: ['planning', 'schedule-rebuild-preview', id],
    queryFn: ({ signal }) => readScheduleRebuild(id, signal),
    refetchOnWindowFocus: false,
  });
  const [updatedPreview, setUpdatedPreview] = useState<ScheduleRebuildPreview | null>(null);
  const [notice, setNotice] = useState('');
  const write = useRebuildSchedule(id);
  const preview = updatedPreview ?? query.data;
  const readError = query.isError && !updatedPreview ? scheduleRefusal(query.error, 'Предпросмотр не прочитался.') : null;
  const busy = query.isFetching || write.isPending;

  async function rebuild() {
    if (!preview || busy || readError) return;
    setNotice('');
    try {
      await write.mutateAsync(preview);
      onClose();
    } catch (error) {
      const refusal = scheduleRefusal(error, 'График не перестроился.');
      if (refusal.kind === 'preview-changed') {
        setUpdatedPreview(refusal.preview);
        setNotice(refusal.message);
        write.reset();
      } else if (refusal.kind === 'conflict') {
        setUpdatedPreview(null);
        const next = await query.refetch();
        if (next.isSuccess) setNotice('График тем временем изменили. Проверьте новый предпросмотр и подтвердите ещё раз.');
        write.reset();
      }
    }
  }

  return <Modal open wide title="Перестроить по распределению" busy={write.isPending} nothingToLose
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit /><ModalPrimary loading={write.isPending} disabled={!preview || busy || !!readError}
      onClick={() => void rebuild()}>Перестроить</ModalPrimary></ModalButtons>}>
    <div className="grid min-w-0 grid-cols-1 gap-4">
      {query.isFetching && <p role="status" className="text-body-medium">Читаем предпросмотр…</p>}
      {notice && <Banner tone="warning">{notice}</Banner>}
      {readError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
        {readError.message}
      </Banner>}
      {write.error && <Banner tone="error">{scheduleRefusal(write.error, 'График не перестроился.').message}</Banner>}
      {preview && <>
        <DataGrid density="compact" rows={changesOf(preview)} rowKey={row => row.key} columns={[
          { key: 'action', label: 'Изменение' },
          { key: 'name', label: 'Работа', rowHeader: true, wrap: true },
          { key: 'section', label: 'Раздел', cell: row => sectionName(row.sectionId, sections) },
          { key: 'volume', label: 'Объём' },
        ]} />
        {!!preview.removedLinks.length && <div className="space-y-2">
          <h3 className="text-title-medium">Будут удалены связи</h3>
          <DataGrid density="compact" rows={preview.removedLinks} rowKey={link => link.id} columns={[
            { key: 'predecessor', label: 'Предшественник', wrap: true, cell: link =>
              `${link.predecessor.scheduleName} · ${link.predecessor.name}` },
            { key: 'successor', label: 'Последователь', wrap: true, cell: link =>
              `${link.successor.scheduleName} · ${link.successor.name}` },
            { key: 'lag', label: 'Задержка, раб. дн.' },
          ]} />
        </div>}
        {preview.skipped.unmapped > 0 && <p className="text-body-medium">{skippedText(preview.skipped.unmapped)}</p>}
      </>}
    </div>
  </Modal>;
}
