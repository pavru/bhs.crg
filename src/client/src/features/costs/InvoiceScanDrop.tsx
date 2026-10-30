import { useState, type DragEvent, type ReactNode } from 'react';
import { Close } from '@/shared/ui/icons';
import { Button, IconButton } from '@/shared/ui/Button';
import { carriesFiles, droppedFiles, useFileDragActive } from '@/shared/ui/fileDrop';
import { ruCount } from '@/shared/utils/pluralize';
import {
  batchTitle, dismissBatch, repeatsTitle, repeatWords, retryable, shownRepeats, stopBatch, uploadLine, type ScanBatch,
} from './scanBatch';
import { Tooltip } from '@/shared/ui/Tooltip';

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
        <div className="absolute inset-1 z-10 rounded-md border-2 border-dashed border-outline-variant pointer-events-none" />
      )}
      {enabled && over !== null && (
        <div className="absolute inset-1 z-10 rounded-md border-2 border-dashed border-primary bg-primary-container/80
          flex items-center justify-center p-4 text-center pointer-events-none">
          <span className="text-body-medium font-medium text-primary">
            {over > 1
              ? `Отпустите — заведём ${ruCount(over, 'черновик', 'черновика', 'черновиков')}, по одному на файл`
              : 'Отпустите — заведём черновик из файла'}
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
export function InvoiceScanBatchBar({ batch, only, found, alive, onOnly, onShowAll, onRetry, onOpen }: {
  /** Пакет вошедшего; чужой сюда не приходит. */
  batch: ScanBatch | null;
  /** Список сужен до заведённых этой загрузкой (issue #1294). */
  only: boolean;
  /** Сколько заведённых стоит в списке; `null` — список не пришёл. */
  found: number | null;
  /** Счета, которые есть в списке; `null` — не знаем, и повторы показаны как запомнены. */
  alive: ReadonlySet<string> | null;
  onOnly: () => void;
  onShowAll: () => void;
  onRetry: () => void;
  /** Открыть счёт — новый черновик или прежний, к которому тот же файл уже приложен. */
  onOpen: (id: string) => void;
}) {
  if (!batch) return null;
  const repeated = shownRepeats(batch, alive);
  const repeats = repeatsTitle(batch, repeated);
  // Отбор живёт здесь, а не чипом в ряду отборов: чипы — постоянные очереди сервера, загрузка —
  // состояние этой вкладки. Чип, который появляется и пропадает, обещал бы постоянство, которого нет.
  const line = uploadLine(batch, found, only);

  const done = batch.phase === 'done';
  const again = retryable(batch).length;
  const bad = batch.refused !== null || batch.halted !== null;

  return (
    <div className="px-3 py-2 border-y border-outline-variant bg-surface text-body-small space-y-1.5" role="status">
      <div className="flex items-start gap-2">
        <p className={`flex-1 ${bad ? 'text-error' : 'text-on-surface-variant'}`}>{batchTitle(batch)}</p>
        {batch.phase === 'running' && <Button onClick={stopBatch}>Остановить</Button>}
        {done && <IconButton label="Закрыть" onClick={dismissBatch} icon={Close} />}
      </div>
      {!done && (
        <div className="h-1 rounded-xs bg-surface-container overflow-hidden" aria-hidden>
          <div className="h-full bg-primary transition-[width]" style={{ width: `${(batch.settled / batch.total) * 100}%` }} />
        </div>
      )}
      {/* Пакет не хранится нигде, кроме этой вкладки: уйти с неё до конца — оставить файлы неотправленными. */}
      {!done && <p className="text-on-surface-variant">Не закрывайте вкладку до конца загрузки.</p>}
      {batch.rejected.length > 0 && (
        <ul className="max-h-24 overflow-y-auto space-y-0.5">
          {batch.rejected.map((r, i) => (
            <li key={`${r.name}-${i}`} className="text-on-surface-variant">
              <span className="font-medium break-all">{r.name}</span> — {r.reason}
            </li>
          ))}
        </ul>
      )}
      {done && again > 0 && (
        <Button variant="outlined" onClick={onRetry}>Повторить непринятые ({again})</Button>
      )}
      {/* Повторы (issue #1295) — своим списком, а не среди непринятых: черновик заведён. Удаления
          здесь нет: ему нужны версия счёта и диалог с перечнем уходящего — они на форме, в одном щелчке. */}
      {repeats && (
        <div data-testid="upload-repeats">
          <p className="text-on-surface-variant">{repeats}</p>
          <ul className="max-h-24 overflow-y-auto space-y-0.5">
            {repeated.map(r => {
              const words = repeatWords(r);
              return (
                <li key={r.id} className="text-on-surface-variant">
                  <Tooltip content="Открыть новый черновик">
                    <button type="button" className="font-medium break-all underline hover:text-on-surface" onClick={() => onOpen(r.id)}>{r.name}</button>
                  </Tooltip>
                  {' — '}{words.lead}
                  <Tooltip content="Открыть прежний счёт">
                    <button type="button" className="underline hover:text-on-surface"
                      onClick={() => onOpen(r.others[0].id)}>{words.link}</button>
                  </Tooltip>
                  {words.tail}
                </li>
              );
            })}
          </ul>
        </div>
      )}
      {line && (
        <p className="text-on-surface-variant" data-testid="upload-line">
          {line.text && `${line.text} `}
          <Tooltip content={line.title}>
            <button type="button" className="underline hover:text-on-surface"
              onClick={line.action === 'only' ? onOnly : onShowAll}>{line.label}</button>
          </Tooltip>
        </p>
      )}
    </div>
  );
}
