import { Close } from '@/shared/ui/icons';
import { Button, IconButton } from '@/shared/ui/Button';
import {
  batchTally, batchTitle, dismissBatch, repeatsTitle, repeatWords, retryable, shownRepeats, stopBatch, uploadLine, type ScanBatch,
} from './scanBatch';
import { UNCHECKED_NOTE, heldWords, twinOpenLabel } from './scanIntake';
import { LinearProgress } from '@/shared/ui/LinearProgress';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Полоса пакета над списком: ход («Загружаем сканы: 3 из 10»), потом итог — и итог сам не исчезает.
 *
 * <p>Тостов у пакета нет: непринятые файлы с причинами обязаны дожить до того, как человек на них
 * посмотрит, а тост уходит через восемь секунд. Закрывает полосу крестик или следующий пакет.</p>
 */
export function InvoiceScanBatchBar({ batch, only, found, alive, onOnly, onShowAll, onRetry, onOpen, onAccept, bare = false }: {
  /** Пакет вошедшего; чужой сюда не приходит. */
  batch: ScanBatch | null;
  /** Список сужен до заведённых этой загрузкой (issue #1294). */
  only: boolean;
  /** Сколько заведённых стоит в списке; `null` — список не пришёл. */
  found: number | null;
  /** Счета, которые есть в списке; `null` — не знаем, и повторы показаны как запомнены. */
  alive: ReadonlySet<string> | null;
  /** Отбор списка «только эта загрузка» — у полосы над списком; в центре активности списка нет, и строки нет. */
  onOnly?: () => void;
  onShowAll?: () => void;
  onRetry: () => void;
  /** Открыть счёт — новый черновик или прежний, к которому тот же файл уже приложен. */
  onOpen: (id: string) => void;
  /** «Завести всё равно» у придержанного — по его отпечатку. */
  onAccept: (print: string) => void;
  /** Без своей рамки и заливки — внутри слоя центра активности. */
  bare?: boolean;
}) {
  if (!batch) return null;
  const repeated = shownRepeats(batch, alive);
  const repeats = repeatsTitle(batch, repeated);
  // Отбор живёт здесь, а не чипом в ряду отборов: чипы — постоянные очереди сервера, загрузка —
  // состояние этой вкладки. Чип, который появляется и пропадает, обещал бы постоянство, которого нет.
  const line = onOnly && onShowAll ? uploadLine(batch, found, only) : null;

  const done = batch.phase === 'done';
  const tally = batchTally(batch);
  const again = retryable(batch).length;
  const bad = batch.refused !== null || batch.halted !== null;

  return (
    <div className={`${bare ? 'px-5 py-2' : 'px-3 py-2 border-y border-outline-variant bg-surface'} text-body-small space-y-1.5`} role="status">
      <div className="flex items-start gap-2">
        <p className={`flex-1 ${bad ? 'text-error' : 'text-on-surface-variant'}`}>{batchTitle(batch)}</p>
        {batch.phase === 'running' && <Button onClick={stopBatch}>Остановить</Button>}
        {done && <IconButton label="Закрыть" onClick={dismissBatch} icon={Close} />}
      </div>
      {!done && batch.phase !== 'checking' && batch.total > 0 && (
        <LinearProgress value={batch.settled / batch.total} label="Ход пакета сканов" />
      )}
      {/* Итог по листу «Ф»: заведённые, придержанные и выбранные дважды — одной строкой. */}
      {tally && <p className="text-on-surface" data-testid="upload-tally">{tally}</p>}
      {batch.held.length > 0 && (
        <ul className="max-h-32 overflow-y-auto space-y-1" data-testid="upload-held">
          {batch.held.map(h => (
            <li key={h.print} className="text-on-surface-variant">
              <span className="font-medium break-all">{h.name}</span> — {heldWords(h.twins)}
              <span className="inline-flex flex-wrap gap-1 ml-1 align-middle">
                <Button variant="text" aria-label={twinOpenLabel(h.twins[0])} onClick={() => onOpen(h.twins[0].id)}>Открыть</Button>
                {/* Файл живёт в памяти вкладки: после перезагрузки его нет — заводить нечем. */}
                {h.file
                  ? <Button variant="text" disabled={!done} aria-label={`Завести черновик из ${h.name} всё равно`}
                      onClick={() => onAccept(h.print)}>Завести всё равно</Button>
                  : <span>— выберите файл заново</span>}
              </span>
            </li>
          ))}
        </ul>
      )}
      {batch.doubled.map(name => (
        <p key={name} className="text-on-surface-variant">{name} выбран дважды — заведён один черновик</p>
      ))}
      {batch.recovered.map(name => (
        <p key={name} className="text-on-surface-variant">{name} — заведён (ответ потерялся)</p>
      ))}
      {batch.unchecked && <p className="text-on-surface-variant" data-testid="upload-unchecked">{UNCHECKED_NOTE}</p>}
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
