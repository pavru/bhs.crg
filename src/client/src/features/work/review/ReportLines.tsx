import { useState } from 'react';
import { useReviewPhoto, type ReviewFullCard, type ReviewReportLine } from '@/shared/api/workReview';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { PhotoGallery, PhotoImage } from '@/shared/ui/PhotoGallery';
import { StatusText } from '@/shared/ui/StatusText';
import { workLabel } from './workLabel';

type Photo = ReviewReportLine['photos'][number];

function ReportPhoto({ card, photo, preview, label }: { card: ReviewFullCard; photo: Photo; preview: boolean; label: string }) {
  const file = useReviewPhoto(photo, card.id, card.viewedVersion, preview);
  if (file.data) return <PhotoImage source={file.data} alt={preview ? '' : label}
    className={preview ? 'size-12 rounded-xs object-cover' : 'mx-auto max-h-[65vh] w-full object-contain'} />;
  if (preview) return <span>{file.isError ? 'Открыть фото' : 'Фото'}</span>;
  if (file.isError) return <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void file.refetch() }]}>
    Не удалось загрузить фото.
  </Banner>;
  return <p role="status" className="text-body-medium">Загрузка фото…</p>;
}

export function ReportLines({ card }: { card: ReviewFullCard }) {
  const [gallery, setGallery] = useState<{ line: ReviewReportLine; index: number } | null>(null);
  const columns: DataGridColumn<ReviewReportLine>[] = [
    { key: 'work', label: 'Работа и место', rowHeader: true, wrap: true, cell: line => <>
      <span className="font-medium">{workLabel(card, line)}</span>
      <span className="block text-body-small text-on-surface-variant">
        {[line.sectionId && card.sections[line.sectionId], line.location].filter(Boolean).join(' · ')}
      </span>
      {line.comment && <span className="block whitespace-pre-wrap text-body-small">{line.comment}</span>}
    </> },
    { key: 'volume', label: 'Объём', align: 'right', cell: line => `${formatNumber(line.volume)} ${card.units[line.unitId] ?? ''}`.trim() },
    { key: 'photos', label: 'Фото', wrap: true, interactive: true, cell: line => {
      const stored = line.photos.filter(photo => photo.state === 'stored');
      const pending = line.photos.length - stored.length;
      return line.photos.length === 0 ? '—' : <div className="flex flex-wrap items-center gap-2">
        {stored.map((photo, index) => <Button key={photo.id} variant="text" className="px-1"
          aria-label={`Открыть фото ${index + 1}: ${workLabel(card, line)}`} onClick={() => setGallery({ line, index })}>
          <ReportPhoto photo={photo} card={card} preview label="" />
        </Button>)}
        {pending > 0 && <StatusText tone="neutral">фото не загружены: {pending}</StatusText>}
      </div>;
    } },
  ];
  const photos = gallery?.line.photos.filter(photo => photo.state === 'stored') ?? [];
  return <>
    <DataGrid columns={columns} rows={card.report.lines} rowKey={line => line.id} density="compact" />
    {gallery && <PhotoGallery title={`Фото · ${workLabel(card, gallery.line)}`} count={photos.length}
      initialIndex={gallery.index} onClose={() => setGallery(null)} renderPhoto={index =>
        <ReportPhoto key={photos[index].id} photo={photos[index]} card={card} preview={false} label={`Фото ${index + 1}`} />} />}
  </>;
}
