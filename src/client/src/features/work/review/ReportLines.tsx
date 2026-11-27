import { useState } from 'react';
import { useAccess } from '@/shared/api/access';
import { useReportDetailsSchema } from '@/shared/api/workReports';
import { useReviewPhoto, type ReviewFullCard, type ReviewReportLine } from '@/shared/api/workReview';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { PhotoGallery, PhotoImage } from '@/shared/ui/PhotoGallery';
import { StatusText } from '@/shared/ui/StatusText';
import { formatFieldValue } from '@/shared/utils/fieldDisplay';
import { detailValues } from '../reportDetails';
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
  const hasDetails = card.report.lines.some(line => Object.keys(detailValues(line.details)).length > 0);
  const rights = useAccess(hasDetails).data?.permissions;
  const schema = useReportDetailsSchema(hasDetails && !!rights?.some(right => right === 'work.report.review' || right === 'work.report.own')).data;
  const columns: DataGridColumn<ReviewReportLine>[] = [
    { key: 'work', label: 'Работа и место', rowHeader: true, wrap: true, cell: line => <>
      <span className="font-medium">{workLabel(card, line)}</span>
      <span className="block text-body-small text-on-surface-variant">
        {[line.sectionId && card.sections[line.sectionId], line.location].filter(Boolean).join(' · ')}
      </span>
      {line.comment && <span className="block whitespace-pre-wrap text-body-small">{line.comment}</span>}
      <FilledDetails details={line.details} schema={schema} />
      {!!line.missingFields?.length && <div className="mt-1">
        <StatusText tone="warning">Нужно к приёмке</StatusText>
        <ul className="list-disc ps-5 text-body-small">
          {line.missingFields.map(field => <li key={field.key}>{field.title}</li>)}
        </ul>
      </div>}
    </> },
    { key: 'volume', label: 'Объём', align: 'right', cell: line => `${formatNumber(line.volume, 4)} ${card.units[line.unitId] ?? ''}`.trim() },
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

function FilledDetails({ details, schema }: {
  details: unknown; schema: ReturnType<typeof useReportDetailsSchema>['data'];
}) {
  const entries = Object.entries(detailValues(details)).filter(([, value]) => value != null && value !== '');
  return entries.length > 0 && <dl className="mt-1 space-y-1 text-body-small">
    {entries.map(([key, value]) => {
      const field = schema?.fields.find(item => item.key === key);
      return <div className="flex flex-wrap gap-x-2 break-words" key={key}>
        <dt className="text-on-surface-variant">{field?.title || key}</dt>
        <dd className="min-w-0 whitespace-pre-wrap">{value && typeof value === 'object'
          ? detailSummary(value) : field ? formatFieldValue(field, value, schema) : String(value)}</dd>
      </div>;
    })}
  </dl>;
}

function detailSummary(value: object): string {
  if (Array.isArray(value)) return value.map(item => item && typeof item === 'object' ? detailSummary(item) : String(item ?? '')).join(', ');
  const info = value as Record<string, unknown>;
  if (typeof info.displayName === 'string') return info.displayName;
  if (typeof info.fileName === 'string') return info.fileName;
  return JSON.stringify(value);
}
