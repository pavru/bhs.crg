import { useCallback, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { uploadWorkPhoto, workPhotoLimitsKey } from '@/shared/api/workPhotos';
import { ownReportsKey, type WorkReport } from '@/shared/api/workReports';
import { apiError } from '@/shared/utils/apiError';
import type { DraftLine } from './reportContents';

type Transfer = { name: string } & (
  | { state: 'waiting' | 'uploading'; file: File }
  | { state: 'error'; file?: File; message: string }
  | { state: 'stored' }
);

// Оставляем соединения браузера для сохранения и подачи отчёта, в том числе на HTTP/1.1.
const PARALLEL_UPLOADS = 2;

// Очередь живёт на весь визит в редактор, включая переход с /new на адрес сохранённого отчёта.
export function usePhotoUploads() {
  const [transfers, setTransfers] = useState<Record<string, Transfer>>({});
  const active = useRef(new Map<string, AbortController>());
  const cache = useQueryClient();

  useEffect(() => {
    const requests = active.current;
    return () => requests.forEach(request => request.abort());
  }, []);

  const queue = useCallback((items: { id: string; file: File }[]) => setTransfers(current => ({
    ...current, ...Object.fromEntries(items.map(({ id, file }) => [id, { state: 'waiting', file, name: file.name }])),
  })), []);

  const forget = useCallback((id: string) => {
    active.current.get(id)?.abort();
    active.current.delete(id);
    setTransfers(current => {
      const next = { ...current };
      delete next[id];
      return next;
    });
  }, []);

  const deliver = useCallback((report: WorkReport, lines: DraftLine[]) => {
    for (const line of lines) {
      const declared = report.lines.find(saved => saved.id === line.id)?.photos ?? [];
      for (const photo of declared) {
        const transfer = transfers[photo.id];
        if (transfer?.state !== 'waiting' || !line.photos.some(item => item.id === photo.id)) continue;
        if (photo.state === 'stored') { forget(photo.id); continue; }
        if (photo.canDeliver !== true || active.current.has(photo.id)) continue;
        if (active.current.size >= PARALLEL_UPLOADS) return;

        const request = new AbortController();
        active.current.set(photo.id, request);
        setTransfers(current => ({ ...current, [photo.id]: { ...transfer, state: 'uploading' } }));
        void uploadWorkPhoto(report.id, line.id, photo.id, transfer.file, request.signal).then(() => {
          if (request.signal.aborted) return;
          active.current.delete(photo.id);
          setTransfers(current => ({ ...current, [photo.id]: { state: 'stored', name: transfer.name } }));
          void cache.invalidateQueries({ queryKey: ownReportsKey });
          void cache.invalidateQueries({ queryKey: ['work', 'review-queue'] });
        }).catch((error: unknown) => {
          if (request.signal.aborted) return;
          active.current.delete(photo.id);
          const status = (error as { response?: { status?: number } })?.response?.status;
          const gone = status === 410;
          setTransfers(current => ({ ...current, [photo.id]: { state: 'error', name: transfer.name,
            file: gone ? undefined : transfer.file, message: apiError(error, 'Не удалось загрузить фото.') } }));
          if (gone) void cache.invalidateQueries({ queryKey: ['work', 'report'], predicate: query => query.queryKey[3] === report.id });
          if (status === 413) void cache.invalidateQueries({ queryKey: workPhotoLimitsKey });
        });
      }
    }
  }, [cache, forget, transfers]);

  return { transfers, queue, forget, deliver };
}

export type PhotoUploads = ReturnType<typeof usePhotoUploads>;
