import { Loader2, RefreshCw, ScanLine, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { useStartRecognition, type InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import { failureNote, unusedLinesNote, type FieldOffer } from './recognition';

/**
 * Распознавание скана в форме счёта (issue #1077): чип «Распознаётся…», полоса отказа, замечания.
 *
 * <p>Сторож задачи — отказ выглядит отказом: у черновика, чей скан не прочитан, поля пусты так же,
 * как у счёта, которого никто не трогал, и отличает их только эта полоса с названной причиной.</p>
 */

/** В строке чипов: скан читается, форму при этом можно заполнять. */
export function RecognitionChip({ recognition }: { recognition: InvoiceRecognition | undefined }) {
  if (recognition?.state !== 'running') return null;
  return (
    <span className="inline-flex items-center gap-1 text-xs text-fg2" role="status">
      <Loader2 size={12} className="animate-spin" aria-hidden /> Распознаётся…
      {recognition.progress && <span className="text-fg3">{recognition.progress}</span>}
    </span>
  );
}

/**
 * Полоса в шапке: распознавание не удалось — с причиной и повтором; либо его состояние не пришло.
 *
 * @param canEdit есть ли право вводить счета: без него повтор ответил бы отказом, и кнопки нет.
 */
export function RecognitionBanner({ invoiceId, recognition, unread, onRetryRead, canEdit }: {
  invoiceId: string;
  recognition: InvoiceRecognition | undefined;
  /** Состояние распознавания не пришло. ⚠️ Это не «не распознан» и не «не запускалось». */
  unread: boolean;
  onRetryRead: () => void;
  canEdit: boolean;
}) {
  const start = useStartRecognition();
  const toast = useToast();

  if (unread) {
    return (
      <Strip quiet>
        <span className="flex-1">Состояние распознавания не пришло — это не «не распознан».</span>
        <Button size="sm" variant="outlined" icon={<RefreshCw size={12} />} onClick={onRetryRead}>Повторить</Button>
      </Strip>
    );
  }
  // Распознанное остаётся в счёте как было — полоса только говорит, что вид на экране уже другой.
  if (recognition?.state === 'done' && recognition.byFormerImage) {
    return (
      <Strip quiet>
        <span className="flex-1" data-testid="by-former-image">
          Распознано по прежнему виду файла: вид для чтения с тех пор построен заново. Прочитанное
          осталось в счёте как было.
          {canEdit && !recognition.canStart && recognition.whyNot && ` Распознать ещё раз нельзя: ${recognition.whyNot}.`}
        </span>
        {canEdit && recognition.canStart && (
          <Button size="sm" variant="outlined" icon={<RefreshCw size={12} />} loading={start.isPending}
            onClick={() => start.mutateAsync(invoiceId).catch(e => toast.apiError(e, 'Распознавание не запущено'))}>
            Распознать ещё раз
          </Button>
        )}
      </Strip>
    );
  }
  if (recognition?.state !== 'failed') return null;

  const note = failureNote(recognition);
  return (
    <Strip quiet={note.quiet}>
      <span className="flex-1">
        {note.text}
        {/* Кнопки, которая откажет, нет: причину называет сервер, заранее. */}
        {canEdit && !recognition.canStart && recognition.whyNot && ` Распознать ещё раз нельзя: ${recognition.whyNot}.`}
      </span>
      {canEdit && recognition.canStart && (
        <Button size="sm" variant="outlined" icon={<RefreshCw size={12} />} loading={start.isPending}
          onClick={() => start.mutateAsync(invoiceId).catch(e => toast.apiError(e, 'Распознавание не запущено'))}>
          Распознать ещё раз
        </Button>
      )}
    </Strip>
  );
}

/**
 * «Распознать скан» — у счёта, чей скан приложен, а распознавание не запускалось (скан приложили к
 * счёту, заведённому руками). Нельзя — причина словами сервера, а не молчание.
 */
export function RecognitionStart({ invoiceId, recognition, explain }: {
  invoiceId: string; recognition: InvoiceRecognition | undefined;
  /** Называть ли причину, когда распознать нельзя. У счёта со строками она была бы шумом в каждой
   *  шапке («счёт уже не черновик»); у пустого счёта со сканом — единственное объяснение, почему
   *  кнопки нет. */
  explain: boolean;
}) {
  const start = useStartRecognition();
  const toast = useToast();
  if (recognition?.state !== 'none') return null;
  if (!recognition.canStart)
    return explain && recognition.whyNot ? <span className="text-xs text-fg3">Не распознаётся: {recognition.whyNot}</span> : null;
  return (
    <Button size="sm" variant="outlined" icon={<ScanLine size={13} />} loading={start.isPending}
      onClick={() => start.mutateAsync(invoiceId).catch(e => toast.apiError(e, 'Распознавание не запущено'))}>
      Распознать файл
    </Button>
  );
}

/** Замечания распознавания и строки, которые в счёт не легли, — первым блоком прокручиваемой части. */
export function RecognitionNotes({ recognition }: { recognition: InvoiceRecognition | undefined }) {
  const notes = recognition?.state === 'done' ? recognition.notes : [];
  const lines = unusedLinesNote(recognition);
  if (notes.length === 0 && !lines) return null;
  return (
    <section className="space-y-1 text-xs text-warning">
      <h2 className="text-sm font-medium text-fg2">Замечания распознавания</h2>
      <ul className="list-disc pl-4 space-y-0.5">
        {notes.map(note => <li key={note}>{note}</li>)}
        {lines && <li>{lines}</li>}
      </ul>
    </section>
  );
}

/** Строка под полем: «В скане: … · Взять». «Взять» кладёт значение в правки формы и не сохраняет. */
export function OfferLine({ offer, onTake }: { offer: FieldOffer; onTake: (value: unknown) => void }) {
  return (
    <p className="mt-0.5 flex items-baseline gap-1 text-xs text-fg3 min-w-0" title={offer.title ?? `В файле: ${offer.text}`}>
      <span className="truncate">В файле: {offer.text}</span>
      {offer.take !== undefined
        ? (
          <button type="button" className="shrink-0 text-brand hover:underline" onClick={() => onTake(offer.take)}>
            Взять
          </button>
        )
        // Значение не разобрано или его нечем положить в поле: показать — честно, предложить «Взять» — нет.
        : <span className="shrink-0">— впишите вручную</span>}
    </p>
  );
}

function Strip({ quiet, children }: { quiet: boolean; children: React.ReactNode }) {
  return (
    <div role={quiet ? 'status' : 'alert'}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-xs ${quiet
        ? 'border-stroke bg-surface2 text-fg2'
        : 'border-danger-border bg-danger-subtle text-danger'}`}>
      {quiet ? <ScanLine size={14} className="shrink-0 mt-0.5 text-fg3" /> : <TriangleAlert size={14} className="shrink-0 mt-0.5" />}
      {children}
    </div>
  );
}
