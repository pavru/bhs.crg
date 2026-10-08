import { useState, type DragEvent, type ReactNode } from 'react';
import { X } from 'lucide-react';
import { Button, IconButton } from '@/shared/ui/Button';
import { carriesFiles, droppedFiles, useFileDragActive } from '@/shared/ui/fileDrop';
import { ruCount } from '@/shared/utils/pluralize';
import { batchTitle, dismissBatch, retryable, stopBatch, type ScanBatch } from './scanBatch';

/**
 * Колонка списка счетов как цель перетаскивания сканов (задача D4, issue #1093).
 *
 * <p>Цель — колонка списка, и только она: слева «новые счета», справа «этот счёт». Файл, брошенный на
 * открытый счёт, мог бы значить и «новый счёт», и «заменить скан» — поэтому правая часть не цель
 * вовсе, а не цель с догадкой.</p>
 *
 * <p>Пока над окном тащат файл, колонка обведена пунктиром — её видно раньше, чем до неё дошёл курсор.
 * Красного «нельзя» до отпускания нет: вид файла браузер называет ненадёжно, и рамка врала бы на
 * годных. Что не подошло, названо после — в полосе пакета.</p>
 */
export function InvoiceScanDrop({ enabled, onFiles, children }: {
  /** Цели нет у того, кто счета не заводит, и пока идёт прежняя загрузка. */
  enabled: boolean;
  onFiles: (files: File[], folders: string[]) => void;
  children: ReactNode;
}) {
  const dragging = useFileDragActive();
  const [over, setOver] = useState<number | null>(null);

  const onDragOver = (e: DragEvent) => {
    if (!enabled || !carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    setOver(e.dataTransfer.items.length);
  };
  const onDragLeave = (e: DragEvent) => {
    if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(null);
  };
  const onDrop = (e: DragEvent) => {
    setOver(null);
    if (!enabled || !carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    const { files, folders } = droppedFiles(e.dataTransfer);
    if (files.length + folders.length > 0) onFiles(files, folders);
  };

  return (
    <div className="relative flex-1 min-h-0 flex flex-col" onDragOver={onDragOver} onDragLeave={onDragLeave} onDrop={onDrop}>
      {children}
      {enabled && dragging && over === null && (
        <div className="absolute inset-1 z-10 rounded-lg border-2 border-dashed border-stroke pointer-events-none" />
      )}
      {enabled && over !== null && (
        <div className="absolute inset-1 z-10 rounded-lg border-2 border-dashed border-brand bg-brand-subtle/80
          flex items-center justify-center p-4 text-center pointer-events-none">
          <span className="text-sm font-medium text-brand">
            {over > 1
              ? `Отпустите — заведём ${ruCount(over, 'черновик', 'черновика', 'черновиков')}, по одному на файл`
              : 'Отпустите — заведём черновик из скана'}
          </span>
        </div>
      )}
    </div>
  );
}

/**
 * Полоса пакета над списком: ход («Загружаем сканы: 3 из 10»), потом итог — и итог сам не исчезает.
 *
 * <p>Тостов у пакета нет: непринятые файлы с причинами обязаны дожить до того, как человек на них
 * посмотрит, а тост уходит через восемь секунд. Закрывает полосу крестик или следующий пакет.</p>
 */
export function InvoiceScanBatchBar({ batch, narrowed, onShowAll, onRetry }: {
  /** Пакет вошедшего; чужой сюда не приходит. */
  batch: ScanBatch | null;
  /** Список сужен отбором или поиском — новые черновики под ним могут быть не видны. */
  narrowed: boolean;
  onShowAll: () => void;
  onRetry: () => void;
}) {
  if (!batch) return null;

  const done = batch.phase === 'done';
  const again = retryable(batch).length;
  const bad = batch.refused !== null || batch.halted !== null;

  return (
    <div className="px-3 py-2 border-y border-stroke bg-surface text-xs space-y-1.5" role="status">
      <div className="flex items-start gap-2">
        <p className={`flex-1 ${bad ? 'text-danger' : 'text-fg2'}`}>{batchTitle(batch)}</p>
        {batch.phase === 'running' && <Button size="sm" onClick={stopBatch}>Остановить</Button>}
        {done && <IconButton label="Закрыть" size="sm" onClick={dismissBatch}><X size={14} /></IconButton>}
      </div>
      {!done && (
        <div className="h-1 rounded bg-surface2 overflow-hidden" aria-hidden>
          <div className="h-full bg-brand transition-[width]" style={{ width: `${(batch.settled / batch.total) * 100}%` }} />
        </div>
      )}
      {/* Пакет не хранится нигде, кроме этой вкладки: уйти с неё до конца — оставить файлы неотправленными. */}
      {!done && <p className="text-fg4">Не закрывайте вкладку до конца загрузки.</p>}
      {batch.rejected.length > 0 && (
        <ul className="max-h-24 overflow-y-auto space-y-0.5">
          {batch.rejected.map((r, i) => (
            <li key={`${r.name}-${i}`} className="text-fg2">
              <span className="font-medium break-all">{r.name}</span> — {r.reason}
            </li>
          ))}
        </ul>
      )}
      {done && again > 0 && (
        <Button variant="outlined" size="sm" onClick={onRetry}>Повторить непринятые ({again})</Button>
      )}
      {done && batch.created.length > 0 && narrowed && (
        <p className="text-fg3">
          Под отбором или поиском новых черновиков может быть не видно.{' '}
          <button type="button" className="underline hover:text-fg1" onClick={onShowAll}>Показать все счета</button>
        </p>
      )}
    </div>
  );
}
