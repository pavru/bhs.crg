import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Close, EditNote, Logout, SearchCheck, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useToast } from '@/shared/ui/Toast';
import { formatDate } from '@/shared/format/format';
import { useFreshInvoice, type InvoiceView } from '@/shared/api/invoices';
import {
  useCorrectInvoice, usePreviewCorrection, type CorrectionPreview,
} from '@/shared/api/invoiceCorrection';
import { conflictKind, correctionBody, readyToCheck, REASON_LENGTH, type CorrectionContent } from './correction';
import { CorrectionPreviewDialog } from './CorrectionPreviewDialog';
import { StaleInvoiceNotice } from './StaleInvoiceNotice';
import type { CorrectionDraft } from './useCorrectionDraft';


const status = (error: unknown) => (error as { response?: { status?: number } })?.response?.status;

/**
 * Полоса режима «Исправление» (E2, issue #1100, PR 6b-1): что сейчас происходит, причина, проверка и
 * запись — одним телом за всю форму.
 *
 * <p><b>Причина — до предпросмотра</b>: сервер требует её и у него (она ляжет в версию и в журнал), и
 * кнопка без неё не нажимается, а не получает отказ.</p>
 *
 * <p><b>Версия черновика — с которой он собран.</b> Счёт поменялся под набранным (оплатили, приложили
 * документ, исправил сосед) — исправление по прежнему виду записать нельзя, и полоса говорит это ДО
 * нажатия. Без набранного версия переезжает молча: черновика, который устарел бы, ещё нет.</p>
 *
 * <p><b>Отказ 409 разбирается по свежему виду</b> (`conflictKind`): счёт больше не заперт — «правьте как
 * обычно»; версия сменилась — «перечитайте»; та же версия — устарел предпросмотр (полночь, закрыли
 * период, сменили допуск), и он пересчитывается сам, а записать пересчитанное человек решает снова.</p>
 */
export function CorrectionBar({ view, draft, edits, onResetHeader }: {
  view: InvoiceView;
  draft: CorrectionDraft;
  /** Правки шапки — они живут в форме: поля шапки в обоих режимах одни и те же. */
  edits: Readonly<Record<string, unknown>>;
  /** Выбросить правки шапки: отмена, запись, «Перечитать счёт». */
  onResetHeader: () => void;
}) {
  const toast = useToast();
  const previewing = usePreviewCorrection();
  const correct = useCorrectInvoice();
  const readFresh = useFreshInvoice();
  const [open, setOpen] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const content: CorrectionContent = {
    reason: draft.reason, edits, lines: draft.lines, vatMode: draft.vatMode, allocation: draft.allocation,
  };
  const touched = Object.keys(edits).length > 0 || draft.lines !== null || draft.allocation !== null;
  const [seen, setSeen] = useState(view.version);
  // Правится в рендере, как у `useDraftBase`: эффект нарисовал бы кадр с «устарело» без причины.
  if (!touched && seen !== view.version) setSeen(view.version);
  const stale = seen !== view.version;

  function cancel() {
    onResetHeader();
    draft.stop();
  }

  function reread() {
    onResetHeader();
    draft.reread();
    setSeen(view.version);
  }

  async function ask(): Promise<CorrectionPreview | null> {
    try {
      return await previewing.mutateAsync({ id: view.id, seen, body: correctionBody(view, content) });
    } catch (e) {
      // Отказ про форму (устарела, исправление не нужно) — окно закрывается: ответ уже на полосе.
      const kind = await conflict(e);
      if (kind === 'stale' || kind === 'unneeded') setOpen(false);
      return null;
    }
  }

  function check() {
    previewing.reset();
    setNote(null);
    setOpen(true);
    void ask();
  }

  /** Что значит отказ — по свежему виду счёта; `null` — отказ не 409 или счёт не перечитался. */
  async function conflict(e: unknown) {
    if (status(e) !== 409) return null;
    try {
      return conflictKind(await readFresh(view.id), seen);
    } catch {
      return null;
    }
  }

  async function write(preview: CorrectionPreview) {
    try {
      await correct.mutateAsync({ id: view.id, seen, body: correctionBody(view, content), stamp: preview.stamp });
      setOpen(false);
      cancel();
      toast.success(`Исправление записано: версия ${preview.version} ушла в историю.`);
    } catch (e) {
      const kind = await conflict(e);
      if (kind === 'stamp') {
        const fresh = await ask();
        if (fresh)
          setNote(`Предпросмотр пересчитан: с прошлого подсчёта изменилось то, что ляжет в книгу. Исправление ` +
            `ляжет днём ${formatDate(fresh.day)} — проверьте и запишите заново.`);
      } else if (kind) {
        setOpen(false);
      } else {
        toast.apiError(e, 'Исправление не записано');
      }
    }
  }

  // Закрытие отменили — исправлять больше нечего: набранное становится обычной правкой.
  if (view.payment.lockedBy === null)
    return (
      <div role="alert" className="flex items-start gap-2 rounded-md border border-outline-variant bg-warning-container
        px-3 py-2 text-body-small text-on-warning-container">
        <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
        <p className="flex-1">
          Исправление больше не нужно — правьте как обычно: счёт больше не заперт. Набранное в шапке и строках
          останется несохранённым — сохраните его обычными кнопками; разноску из исправления наберите заново.
        </p>
        <Button size="sm" variant="outlined" icon={<Icon icon={Logout} size={18} />} onClick={draft.release}>
          Выйти из исправления
        </Button>
      </div>
    );

  return (
    <div className="space-y-2" data-testid="correction-bar">
      <div role="status" className="flex items-start gap-2 rounded-md border border-outline-variant bg-warning-container
        px-3 py-2 text-body-small text-on-warning-container">
        <Icon icon={EditNote} size={18} className="shrink-0 mt-0.5" />
        <p className="flex-1">
          Исправление закрытого периода: изменения лягут в книгу, закрытый месяц не изменится. Правьте шапку,
          строки и разноску — запишется всё одним исправлением.
        </p>
      </div>
      {stale && <StaleInvoiceNotice what="исправление" onReread={reread} />}
      <div className="flex items-end gap-2 flex-wrap">
        <TextAreaField label="Причина исправления" required rows={2} maxLength={REASON_LENGTH}
          containerClassName="flex-1 min-w-64" value={draft.reason}
          hint="Ляжет в версию счёта и в журнал: через год исправление объяснит только она"
          onChange={e => draft.setReason(e.target.value)} />
        <Button variant="text" icon={<Icon icon={Close} size={18} />} onClick={cancel}>Отменить исправление</Button>
        <Button variant="filled" icon={<Icon icon={SearchCheck} size={18} />} disabled={!readyToCheck(content) || stale}
          onClick={check}>
          Проверить исправление…
        </Button>
      </div>
      {open && (
        <CorrectionPreviewDialog preview={previewing.data} pending={previewing.isPending} error={previewing.error}
          reason={draft.reason} note={note} writing={correct.isPending}
          onBack={() => setOpen(false)} onWrite={preview => void write(preview)} />
      )}
    </div>
  );
}
