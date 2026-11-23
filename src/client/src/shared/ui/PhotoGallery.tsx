import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { IconButton } from './Button';
import { ChevronLeft, ChevronRight } from './icons';
import { Modal, ModalExit } from './Modal';

/** Привязывает объектный адрес к узлу картинки и отзывает его при смене файла или закрытии. */
export function PhotoImage({ source, alt, className }: { source: Blob; alt: string; className?: string }) {
  const image = useRef<HTMLImageElement>(null);
  useLayoutEffect(() => {
    const url = URL.createObjectURL(source);
    if (image.current) image.current.src = url;
    return () => URL.revokeObjectURL(url);
  }, [source]);
  return <img ref={image} alt={alt} className={className} />;
}

export function PhotoGallery({ title, count, initialIndex, onClose, renderPhoto, fullScreen }: {
  title: string;
  count: number;
  initialIndex: number;
  onClose: () => void;
  renderPhoto: (index: number) => ReactNode;
  fullScreen?: boolean;
}) {
  const [selected, setSelected] = useState(initialIndex);
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (direction: number) => setSelected(Math.max(0, Math.min(index + direction, count - 1)));
  return <Modal open wide fullScreen={fullScreen} onOpenChange={open => { if (!open) onClose(); }} title={title}
    footer={<ModalExit>Закрыть</ModalExit>}>
    <div className="space-y-2" onKeyDown={event => {
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        event.preventDefault();
        move(event.key === 'ArrowLeft' ? -1 : 1);
      }
    }}>
      {renderPhoto(index)}
      {count > 1 && <div className="flex items-center justify-center gap-2">
        <IconButton icon={ChevronLeft} label="Предыдущее фото" aria-disabled={index === 0}
          onClick={() => move(-1)} />
        <span role="status" className="text-body-medium tabular-nums">{index + 1} из {count}</span>
        <IconButton icon={ChevronRight} label="Следующее фото" aria-disabled={index === count - 1}
          onClick={() => move(1)} />
      </div>}
    </div>
  </Modal>;
}
