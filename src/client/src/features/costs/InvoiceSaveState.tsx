import { Button, IconButton } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { StatusText } from '@/shared/ui/StatusText';
import { Tooltip } from '@/shared/ui/Tooltip';
import { CloudDone, CloudOff } from '@/shared/ui/icons';
import { sayStatus, type SaveStatus } from './invoiceSave';

/**
 * Носитель записи в сводке счёта (costs-invoice-screen-12-autosave, D-12.1, ТЗ COST-6.5; макет Г, лист 0, 3.2):
 * вместо кнопки «Сохранить» — правда о записи словами. Четыре состояния:
 * - «Сохранено · 12:04» — очередь пуста;
 * - «Сохраняю…» — запись в полёте;
 * - «Не сохранено — повторить» — не дошла связь (или сервер упал), с «Повторить»;
 * - «Не сохранено: <отказ сервера>» — словами сервера; набранное на месте, исправленное значение пишется само.
 *
 * <p>Живого региона нет нарочно: носитель меняется на каждом уходе с поля. Отказ слышен — его читает подсказка
 * и сам текст, а не объявление.</p>
 *
 * <p>Узкая сводка (рядом список и файл) оставляет значок — полный текст подсказкой: подпись вытеснила бы
 * основное действие под соседнюю панель (D-10.6).</p>
 *
 * <p>Где запись явная (режим «Изменить», оплаченный черновик — D-12.4, D-12.5), пятое состояние — «Не сохранено»,
 * пока правка набрана и не записана; у оплаченного оно называет, где «Сохранить», и ведёт туда.</p>
 */
export function InvoiceSaveState({ status, savedAt, onRetry, narrow = false, pending = null }: {
  status: SaveStatus;
  /** Когда счёт записан — по сохранённому (`updatedAt`), временем. */
  savedAt: string;
  onRetry: () => void;
  /** Сводка узкая — значок с подсказкой вместо подписи. */
  narrow?: boolean;
  /** Явная запись с набранной правкой: что сказать и куда вести (`onGo`). */
  pending?: { text: string; onGo?: () => void } | null;
}) {
  if (status.kind === 'idle' && pending) return <Pending text={pending.text} onGo={pending.onGo} narrow={narrow} />;
  const text = sayStatus(status, savedAt);
  if (status.kind === 'saving')
    return (
      <Tooltip content={narrow ? text : undefined}>
        <span className="shrink-0 inline-flex items-center gap-1 text-label-medium text-on-surface-variant whitespace-nowrap"
          data-testid="invoice-save-state" data-state="saving">
          <CircularProgress size={18} label={narrow ? text : undefined} />
          {!narrow && text}
        </span>
      </Tooltip>
    );
  if (status.kind === 'idle')
    return (
      <span data-testid="invoice-save-state" data-state="saved" className="contents">
        <StatusText tone="quiet" icon={CloudDone} title={narrow ? text : undefined}>
          {narrow ? <span className="sr-only">{text}</span> : text}
        </StatusText>
      </span>
    );
  // У сбоя связи «повторить» — само действие: подпись не повторяет его словом рядом с кнопкой.
  const said = status.kind === 'failed' ? 'Не сохранено' : text;
  return (
    <span className="min-w-0 inline-flex items-center gap-1 text-label-medium text-error"
      data-testid="invoice-save-state" data-state={status.kind}>
      <Tooltip content={text}>
        <span className="min-w-0 inline-flex items-center gap-1">
          <Icon icon={CloudOff} size={18} className="shrink-0" />
          {narrow ? <span className="sr-only">{text}</span> : <span className="min-w-0 max-w-64 truncate">{said}</span>}
        </span>
      </Tooltip>
      {status.kind === 'failed' && <Button variant="text" onClick={onRetry}>Повторить</Button>}
    </span>
  );
}

/** Правка набрана, запись явная: «Не сохранено» — и, если запись в другом месте, переход туда. */
function Pending({ text, onGo, narrow }: { text: string; onGo?: () => void; narrow: boolean }) {
  if (onGo)
    return (
      <span className="min-w-0 inline-flex" data-testid="invoice-save-state" data-state="unsaved">
        {narrow
          ? <IconButton icon={CloudOff} label={text} title={text} onClick={onGo} />
          // Сжимается вместе со сводкой: без min-w-0 кнопка держала бы ширину подписи и на 1024 наезжала на «Файл».
          : <Button variant="text" className="min-w-0 max-w-full" icon={<Icon icon={CloudOff} size={18} className="shrink-0" />} onClick={onGo} title={text}>
            <span className="min-w-0 max-w-64 truncate">{text}</span>
          </Button>}
      </span>
    );
  return (
    <span data-testid="invoice-save-state" data-state="unsaved" className="contents">
      <StatusText tone="quiet" icon={CloudOff} title={narrow ? text : undefined}>
        {narrow ? <span className="sr-only">{text}</span> : text}
      </StatusText>
    </span>
  );
}
