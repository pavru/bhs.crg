import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { claimWindowDrop, type WindowDropOver } from '@/shared/ui/fileDrop';
import { DropCover } from './InvoiceDropCover';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { InvoiceRepeatDialog } from './InvoiceRepeatDialog';
import { holdFile, releaseHeld, startBatch, type ScanBatch } from './scanBatch';
import { useBatchPort } from './useBatchPort';
import { LATE_MS, UNCHECKED_NOTE, checkDrop, twinShort, typing, type HeldFile } from './scanIntake';

/**
 * Приём файлов страницей счетов (costs-invoice-screen-14, D1, D3, D4): бросок на всё окно, кнопка «Счёт из
 * файла» и палитра команд идут одним путём — проверки → отпечатки → вопрос → решение → отправка.
 *
 * <p>Один файл: двойники — окно повтора, проверка не ответила — заводим и говорим «повтор не проверен».
 * Несколько — пакет (`scanBatch`): экран не двигается, повторы придержаны в его итоге.</p>
 *
 * <p>Окно не открывается тому, кто печатает (D4): ответ пришёл позже {@link LATE_MS} после броска, а фокус в
 * поле — вместо окна снэкбар «Решить» с обычным сроком, а запасное место решения — придержанная строка в
 * полосе пакета (Э16, 08.11.2026). Окно, всплывшее посреди ввода, забрало бы Enter у поля.</p>
 */

interface Asking { held: HeldFile; fromHold: boolean }

export function useInvoiceFileIntake({ enabled, owner, batch, opened, listShown, sending, onSingle, onOpen, onBatchStart }: {
  /** Заводить счета можно: без права захвата и покрытия нет вовсе. */
  enabled: boolean;
  owner: string;
  batch: ScanBatch | null;
  /** Номер открытого счёта — покрытие обещает, что он не изменится; `null` — счёт не открыт. */
  opened: { number: string | null } | null;
  /** Виден ли список счетов — покрытие говорит, где искать ход пакета. */
  listShown: boolean;
  /** Идёт заведение одного файла. */
  sending: boolean;
  /** Завести черновик из одного файла и открыть его — под охраной ухода. */
  onSingle: (file: File, acceptRepeat: boolean) => Promise<void>;
  /** Открыть счёт двойника — путём смены счёта. */
  onOpen: (id: string) => void;
  /** Начинается пакет — экрану снять отбор прежней загрузки. */
  onBatchStart: () => void;
}) {
  const toast = useSnackbar();
  const [checking, setChecking] = useState(false);
  const [asking, setAsking] = useState<Asking | null>(null);
  const [over, setOver] = useState<WindowDropOver | null>(null);

  const port = useBatchPort(owner);

  const running = batch !== null && batch.phase !== 'done';
  const loading = sending || checking || running;

  async function single(file: File, at: number) {
    setChecking(true);
    let checked;
    try { checked = await checkDrop([file], [], port); } finally { setChecking(false); }
    const [refused] = checked.refused;
    if (refused) { toast.error(`Счёт из файла не заведён: «${refused.name}» — ${refused.reason}`); return; }
    const [held] = checked.held;
    if (held) {
      if (performance.now() - at > LATE_MS && typing()) {
        holdFile(held, owner);
        toast.info(`Файл уже у ${twinShort(held.twins[0])}`, {
          action: { label: 'Решить', onClick: () => setAsking({ held, fromHold: true }) },
        });
      } else setAsking({ held, fromHold: false });
      return;
    }
    await onSingle(file, false);
    if (checked.unchecked) toast.info(UNCHECKED_NOTE);
  }

  /** Один файл — свой черновик; несколько или с папками — пакет. Решает число выбранных, а не годных. */
  function addScans(files: File[], folders: string[] = []) {
    if (files.length === 1 && folders.length === 0) void single(files[0], performance.now());
    else { onBatchStart(); startBatch(files, folders, port); }
  }

  // Захват окна — один на страницу; колбэки свежие через ссылку, а не пересозданием захвата на каждый кадр.
  const live = useRef({ loading, addScans });
  useLayoutEffect(() => { live.current = { loading, addScans }; });
  useEffect(() => {
    if (!enabled) return;
    return claimWindowDrop({
      state: () => (live.current.loading ? 'busy' : 'accept'),
      onFiles: (files, folders) => live.current.addScans(files, folders),
      onOver: next => setOver(prev => (prev?.count === next?.count && prev?.state === next?.state ? prev : next)),
    });
  }, [enabled]);

  /** Окно закрыто, а открывшего элемента у него нет (открыл бросок): фокус — к сводке счёта, явно. */
  const settle = (held: Asking) => {
    if (held.fromHold) releaseHeld(held.held.print);
    setAsking(null);
    requestAnimationFrame(() => {
      if (document.activeElement === document.body)
        document.querySelector<HTMLElement>('[data-testid="invoice-header"]')?.focus();
    });
  };

  const layer: ReactNode = <>
    {enabled && over && <DropCover over={over} opened={opened} batch={batch} checking={checking} listShown={listShown} />}
    {asking && (
      <InvoiceRepeatDialog file={asking.held.file} twins={asking.held.twins}
        onClose={() => settle(asking)}
        onMore={() => { settle(asking); void onSingle(asking.held.file, true); }}
        onOpen={id => { setAsking(null); onOpen(id); }} />
    )}
  </>;

  return { addScans, loading, port, layer };
}
