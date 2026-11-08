import type { InvoiceSameFile } from '@/shared/api/invoices';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Modal, ModalAction, ModalExit } from '@/shared/ui/Modal';
import { StatusText } from '@/shared/ui/StatusText';
import { formatBytes, formatMoney } from '@/shared/format/format';
import { ruCount } from '@/shared/utils/pluralize';
import { distinctNumbers, twinMade, twinOpenLabel, twinTitle } from './scanIntake';

/**
 * Окно повтора одного файла (costs-invoice-screen-14, D4; лист «Ф», окно 6): файл уже приложен к счёту, а
 * черновик ещё не заведён.
 *
 * <p>Basic dialog и на узком экране (`question`), роль — `dialog` (Э9, 08.11.2026). Крестика нет: выход —
 * «Не заводить» подвала, и он же — по умолчанию. Начальный фокус `Modal` ставит на само окно, поэтому на
 * «Не заводить» его переводит `autoFocus`: Enter, Esc и щелчок мимо ничего не заводят.</p>
 *
 * <p>«Завести ещё один черновик» — сознательный повтор: в одном файле бывает несколько счетов. При двух и
 * более разных номерах у двойников это вероятнее, и кнопка заметнее (tonal), но по умолчанию — всё равно
 * «Не заводить».</p>
 */
export function InvoiceRepeatDialog({ file, twins, onClose, onMore, onOpen }: {
  file: { name: string; size: number };
  twins: InvoiceSameFile[];
  /** Окно закрыто без заведения — «Не заводить», Esc, щелчок мимо. */
  onClose: () => void;
  /** «Завести ещё один черновик». */
  onMore: () => void;
  /** «Открыть» у карточки двойника: уход на тот счёт — путём смены счёта, под охраной ухода. */
  onOpen: (id: string) => void;
}) {
  const numbers = distinctNumbers(twins);
  const several = numbers.length > 1;
  const title = twins.length > 1
    ? `Этот файл уже приложен к ${ruCount(twins.length, 'счёту', 'счетам', 'счетам')}`
    : 'Этот файл уже приложен к счёту';
  const hint = several
    ? `Черновик ещё не заведён. В этом файле уже разобраны счета ${andJoin(numbers)} — если в нём есть ещё один, заведите черновик и разберите в нём следующий.`
    : 'Черновик ещё не заведён. Скорее всего, файл загружают второй раз. Если в файле несколько счетов — заведите ещё один черновик и разберите в нём другой счёт.';
  return (
    <Modal open question nothingToLose title={title} onOpenChange={open => { if (!open) onClose(); }}
      footer={<>
        <ModalAction variant={several ? 'tonal' : 'text'} onClick={onMore}>Завести ещё один черновик</ModalAction>
        <ModalExit autoFocus>Не заводить</ModalExit>
      </>}>
      <div className="space-y-3">
        <p className="text-body-medium text-on-surface break-all">
          {file.name} <span className="text-on-surface-variant">· {formatBytes(file.size)}</span>
        </p>
        {twins.map(twin => (
          <Card key={twin.id} title={twinTitle(twin)} data-testid="repeat-twin"
            actions={<Button variant="text" aria-label={twinOpenLabel(twin)} onClick={() => onOpen(twin.id)}>Открыть</Button>}>
            <StatusText>{twin.state}</StatusText>
            <p>{[twin.supplierName, twin.total != null ? formatMoney(twin.total) : null].filter(Boolean).join(' · ')}</p>
            <p>{twinMade(twin)}</p>
          </Card>
        ))}
        <p className="text-body-medium text-on-surface-variant">{hint}</p>
      </div>
    </Modal>
  );
}

/** «2611 и 2612», «2611, 2612 и 2613». */
function andJoin(items: string[]): string {
  return items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} и ${items[items.length - 1]}`;
}
