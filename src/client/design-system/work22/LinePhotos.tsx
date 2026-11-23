import { useRef, useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Add, Delete, ErrorIcon } from '@/shared/ui/icons';
import { PhotoGallery } from '@/shared/ui/PhotoGallery';
import { StatusText } from '@/shared/ui/StatusText';
import { newLocalId } from '@/shared/utils/localId';
import { examplePhoto, photoLimits, type Photo } from './model';

export function LinePhotos({ photos, locked, closed, onChange }: {
  photos: Photo[]; locked: boolean; closed: boolean; onChange: (photos: Photo[]) => void;
}) {
  const [index, setIndex] = useState<number | null>(null);
  const [notice, setNotice] = useState('');
  const file = useRef<HTMLInputElement>(null);
  const stored = photos.filter(photo => photo.state === 'stored' && photo.src);

  function replace(id: string, patch: Partial<Photo>) {
    onChange(photos.map(photo => photo.id === id ? { ...photo, ...patch } : photo));
  }

  function addFiles(files: FileList | null) {
    if (!files?.length) return;
    const free = photoLimits.count - photos.length;
    setNotice(files.length > free ? `Не больше ${photoLimits.count} фото на строку.` : '');
    onChange([...photos, ...Array.from(files).slice(0, free).map(item => ({
      id: newLocalId(), name: item.name, state: 'pending' as const, src: examplePhoto.src,
    }))]);
    if (file.current) file.current.value = '';
  }

  return <section className="space-y-2" aria-label="Фото строки">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h4 className="text-title-small">Фото</h4>
      {!locked && <Button variant="text" icon={<Icon icon={Add} size={18} />} onClick={() => file.current?.click()}>
        Добавить фото
      </Button>}
      <input ref={file} type="file" hidden multiple accept="image/*" onChange={event => addFiles(event.target.files)} />
    </div>
    {!locked && <p className="text-body-small text-on-surface-variant">
      До {photoLimits.count} фото на строку · до {photoLimits.size} МБ на файл
    </p>}
    {closed && <p className="text-body-small text-on-surface-variant">В закрытом периоде можно дослать ранее заявленное фото.</p>}
    {notice && <p role="alert" className="text-body-small text-error">{notice}</p>}
    {photos.length === 0 && <p className="text-body-small text-on-surface-variant">Фото пока нет</p>}
    <ul className="work22-photos">
      {photos.map(photo => <li key={photo.id} className="work22-photo">
        {photo.state === 'stored' && photo.src
          ? <button className="work22-thumbnail focus-visible:outline-2 focus-visible:outline-primary" type="button"
              aria-label={`Открыть фото: ${photo.name}`} onClick={() => setIndex(stored.indexOf(photo))}>
              <img alt="" src={photo.src} />
            </button>
          : <div className="work22-photo-state">
              {photo.state === 'error'
                ? <StatusText tone="error" icon={ErrorIcon}>{photo.error}</StatusText>
                : <span role="status">{closed ? 'Не загружено' : 'Загружается'}</span>}
            </div>}
        <span className="break-words text-body-small">{photo.name}</span>
        {photo.state === 'error' && !locked && <Button variant="text" onClick={() => replace(photo.id, { state: 'pending' })}>Повторить</Button>}
        {photo.state === 'pending' && closed && <Button variant="tonal" onClick={() => replace(photo.id, { state: 'stored' })}>Дослать</Button>}
        {!locked && <IconButton icon={Delete} label={`Удалить фото: ${photo.name}`}
          onClick={() => onChange(photos.filter(item => item.id !== photo.id))} />}
      </li>)}
    </ul>
    {index !== null && <PhotoGallery fullScreen title="Фото строки" count={stored.length} initialIndex={index}
      onClose={() => setIndex(null)} renderPhoto={at => <img className="work22-gallery-image"
        src={stored[at]?.src} alt={stored[at]?.name ?? ''} />} />}
  </section>;
}
