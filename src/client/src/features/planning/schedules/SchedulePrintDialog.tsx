import { useQuery } from '@tanstack/react-query';
import { saveBlob } from '@/shared/api/attachments';
import { previewSchedulePrint, scheduleRefusal } from '@/shared/api/planningSchedules';
import type { Schedule } from '@/shared/api/planningScheduleTypes';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { PdfViewerOverlay } from '@/shared/ui/viewer/PdfViewer';

export function SchedulePrintDialog({ schedule, onClose }: { schedule: Schedule; onClose: () => void }) {
  const title = `Печать списка · ${schedule.name}`;
  const query = useQuery({
    queryKey: ['planning', 'schedule-print', schedule.id],
    queryFn: ({ signal }) => previewSchedulePrint(schedule.id, signal),
    gcTime: 0, retry: false, refetchOnWindowFocus: false,
  });
  const changeOpen = (open: boolean) => { if (!open) onClose(); };
  if (query.isError) {
    const refusal = scheduleRefusal(query.error, 'Не удалось подготовить список работ к печати.');
    return <Modal open title={title} nothingToLose onOpenChange={changeOpen}>
      <Banner tone="error" actions={refusal.kind === 'forbidden' ? undefined : [
        { label: 'Повторить', onClick: () => void query.refetch(), loading: query.isFetching },
      ]}>{refusal.message}</Banner>
    </Modal>;
  }
  const pdf = query.data ?? null;
  return <PdfViewerOverlay open onOpenChange={changeOpen} source={pdf} title={title} documentKey={schedule.id}
    note={pdf && <Button variant="text" onClick={() => saveBlob(pdf, `${schedule.name}.pdf`)}>Скачать PDF</Button>} />;
}
