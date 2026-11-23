import { useRef, useState } from 'react';
import type { WorkLinePhoto } from '@/shared/api/work';
import { useWorkPhotoFile, useWorkPhotoLimits } from '@/shared/api/workPhotos';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete } from '@/shared/ui/icons';
import { PhotoGallery, PhotoImage } from '@/shared/ui/PhotoGallery';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { newUuid } from '@/shared/utils/localId';
import type { PhotoUploads } from './usePhotoUploads';

export interface ReportPhotos {
  uploads: PhotoUploads;
  locked: boolean;
  closed: boolean;
  busy: boolean;
  remove: (lineId: string, photoId: string) => void;
}

export function LinePhotos({ lineId, photos, declared, controls, add }: {
  lineId: string;
  photos: WorkLinePhoto[];
  declared: WorkLinePhoto[];
  controls: ReportPhotos;
  add: (photos: WorkLinePhoto[]) => void;
}) {
  const { uploads, locked, closed, busy: disabled } = controls;
  const { transfers, queue } = uploads;
  const limits = useWorkPhotoLimits(!locked || declared.some(photo => photo.canDeliver));
  const [notice, setNotice] = useState<string[]>([]);
  const [gallery, setGallery] = useState<{ photos: WorkLinePhoto[]; index: number } | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const replacement = useRef<string | null>(null);
  const stored = photos.filter(photo => photo.state === 'stored' || transfers[photo.id]?.state === 'stored');
  const byId = new Map(declared.map(photo => [photo.id, photo]));

  function choose(id: string | null) {
    replacement.current = id;
    if (input.current) {
      input.current.multiple = id === null;
      input.current.click();
    }
  }

  function attach(files: File[]) {
    const id = replacement.current;
    if (files.length === 0 || disabled || !limits.data || (id ? !byId.get(id)?.canDeliver : locked)) return;
    const { maxBytes, count, countText, sizeText } = limits.data;
    const invalid = files.filter(file => file.size > maxBytes);
    const good = files.filter(file => file.size <= maxBytes);
    const free = id ? 1 : Math.max(0, count - photos.length);
    const items = good.slice(0, free).map(file => ({ id: id ?? newUuid(), file }));
    setNotice([...invalid.map(file => `${file.name}: ${sizeText}`), ...(good.length > free ? [countText] : [])]);
    if (items.length === 0) return;
    queue(items);
    if (!id) add(items.map(({ id, file }) => ({ id, originalName: file.name, state: 'pending' })));
  }

  return <section aria-label="Фото строки" className="mt-4 space-y-2">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h4 className="text-title-small">Фото</h4>
      {!locked && <Button variant="text" icon={<Icon icon={Add} size={18} />} disabled={disabled || !limits.data} onClick={() => choose(null)}>
        Добавить фото
      </Button>}
    </div>
    <input ref={input} type="file" hidden aria-label="Файлы фото строки" accept="image/*" multiple onChange={event => {
      attach(Array.from(event.target.files ?? []));
      event.target.value = '';
    }} />
    {limits.data && <p className="text-body-small text-on-surface-variant">
      До {limits.data.count} фото на строку · до {formatNumber(limits.data.megabytes)} МБ на файл
    </p>}
    {limits.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void limits.refetch() }]}>
      {apiError(limits.error, 'Не удалось получить пределы фото.')}
    </Banner>}
    {closed && declared.some(photo => photo.canDeliver) && <p className="text-body-small text-on-surface-variant">
      В закрытом периоде можно дослать ранее заявленное фото.
    </p>}
    {notice.length > 0 && <Banner tone="error">{notice.join(' ')}</Banner>}
    {photos.length === 0 && <p className="text-body-small text-on-surface-variant">Фото пока нет</p>}
    <ul className="flex flex-wrap items-start gap-2">
      {photos.map((photo, index) => {
        const transfer = transfers[photo.id];
        const saved = byId.get(photo.id);
        const ready = photo.state === 'stored' || transfer?.state === 'stored';
        const name = transfer?.name ?? photo.originalName ?? `Фото ${index + 1}`;
        const canSend = !disabled && saved?.canDeliver && !ready && transfer?.state !== 'uploading' && transfer?.state !== 'waiting';
        const retryFile = transfer?.state === 'error' ? transfer.file : undefined;
        return <li key={photo.id} className="flex max-w-64 flex-col items-start gap-1 rounded-md border border-outline-variant p-2">
          {ready ? <Button variant="text" aria-label={`Открыть фото: ${name}`}
            onClick={() => setGallery({ photos: stored, index: stored.indexOf(photo) })}>
            <ReportPhotoFile photo={photo} preview label="" />
          </Button> : transfer?.state === 'error' ? <div role="alert" className="text-body-small text-error">{transfer.message}</div>
            : <div role="status"><StatusText tone="neutral">{transfer?.state === 'uploading' ? 'Загружается'
              : !saved ? 'Загрузится после сохранения' : transfer?.state === 'waiting' && saved.canDeliver
                ? 'В очереди на загрузку' : 'Не загружено'}</StatusText></div>}
          <span className="break-all text-body-small">{name}</span>
          <div className="flex flex-wrap items-center">
            {canSend && retryFile && <Button variant="text"
              onClick={() => queue([{ id: photo.id, file: retryFile }])}>Повторить</Button>}
            {canSend && <Button variant="text" disabled={!limits.data} onClick={() => choose(photo.id)}>
              {transfer?.state === 'error' ? 'Выбрать другой файл' : 'Дослать'}
            </Button>}
            {!locked && <IconButton icon={Delete} label={`Удалить фото: ${name}`} disabled={disabled}
              onClick={() => controls.remove(lineId, photo.id)} />}
          </div>
        </li>;
      })}
    </ul>
    {gallery && <PhotoGallery fullScreen title="Фото строки" count={gallery.photos.length} initialIndex={gallery.index}
      onClose={() => setGallery(null)} renderPhoto={index => <ReportPhotoFile key={gallery.photos[index].id} photo={gallery.photos[index]}
        preview={false} label={`Фото ${index + 1}`} />} />}
  </section>;
}

function ReportPhotoFile({ photo, preview, label }: { photo: WorkLinePhoto; preview: boolean; label: string }) {
  const file = useWorkPhotoFile(photo.id, preview);
  if (file.data) return <PhotoImage source={file.data} alt={label}
    className={preview ? 'size-12 rounded-xs object-cover' : 'mx-auto max-h-[75vh] w-full object-contain'} />;
  if (preview) return <span>{file.isError ? 'Открыть фото' : 'Фото'}</span>;
  if (file.isError) return <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void file.refetch() }]}>
    Не удалось загрузить фото.
  </Banner>;
  return <p role="status" className="text-body-medium">Загрузка фото…</p>;
}
