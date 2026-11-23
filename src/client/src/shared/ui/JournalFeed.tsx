import type { ReactNode } from 'react';
import { formatDateTime } from '@/shared/format/format';

export interface JournalFeedItem {
  sequence: number;
  title: string;
  author: string;
  executor?: string;
  serverTime: string;
  deviceTime?: string | null;
  note?: ReactNode;
}

/** Свежие записи идут первыми по серверному номеру. Часы устройства могут отставать. */
export function JournalFeed({ entries }: { entries: readonly JournalFeedItem[] }) {
  if (!entries.length) return <p className="text-body-medium text-on-surface-variant">В журнале пока нет записей.</p>;
  return <ol className="ps-2">
    {[...entries].sort((a, b) => b.sequence - a.sequence).map(entry => (
      <li key={entry.sequence} className="relative border-s border-outline-variant pb-4 ps-4 last:border-transparent
        before:absolute before:start-0 before:top-1.5 before:size-2 before:-translate-x-1/2 before:rounded-full before:bg-primary">
        <div className="flex flex-wrap items-baseline justify-between gap-x-3">
          <p className="text-label-large">{entry.title}</p>
          <time className="text-body-small tabular-nums text-on-surface-variant" dateTime={entry.serverTime}>
            {formatDateTime(entry.serverTime)}
          </time>
        </div>
        <p className="text-body-medium text-on-surface-variant">{entry.author}</p>
        {entry.executor && <p className="text-body-small text-on-surface-variant">Выполнил · {entry.executor}</p>}
        {entry.deviceTime && <p className="text-body-small text-on-surface-variant">
          Время на устройстве · <time dateTime={entry.deviceTime}>{formatDateTime(entry.deviceTime)}</time>
        </p>}
        {entry.note && <div className="mt-1 text-body-medium whitespace-pre-wrap wrap-break-word">{entry.note}</div>}
      </li>
    ))}
  </ol>;
}
