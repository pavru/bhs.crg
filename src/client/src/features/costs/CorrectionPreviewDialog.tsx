import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Check, ChevronRight, KeyboardArrowDown, Refresh, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { apiError } from '@/shared/utils/apiError';
import { formatDate } from '@/shared/format/format';
import type { CorrectionPreview, CorrectionRecordView } from '@/shared/api/invoiceCorrection';
import { correctionRows, plainRows, serviceRecords, vatShiftText } from './correction';
import { usePlaces, type Places } from './places';
import { BookLines } from './BookLines';

/**
 * Предпросмотр исправления (E2, issue #1100, PR 6b-1): что запишет исправление — день, версия и строки
 * книги, — до записи. «Записать исправление» шлёт отметку ЭТОГО ответа: сервер запишет ровно показанное
 * или откажет, если с тех пор оно изменилось.
 *
 * <p>Строки самого исправления — двумя группами: «днём исправления» (закрытый месяц не меняется — его
 * поправка ложится этим днём) и «своим днём» (период открыт). Открытие книги и выравнивание допуском —
 * служебные записи: они свёрнуты, а не спрятаны, — без них строки исправления не сложились бы с
 * тем, что человек видит в отчёте.</p>
 */
export function CorrectionPreviewDialog({ preview, pending, error, reason, note, writing, onBack, onWrite }: {
  preview: CorrectionPreview | undefined;
  pending: boolean;
  /** Отказ предпросмотра — словами сервера: «не хуже, чем было», нет права разноски. */
  error: unknown;
  reason: string;
  /** Предпросмотр пересчитан после отказа записи: сказать, что числа другие. */
  note: string | null;
  writing: boolean;
  onBack: () => void;
  onWrite: (preview: CorrectionPreview) => void;
}) {
  const places = usePlaces();
  const shown = !pending && !error ? preview : undefined;

  return (
    <Modal open onOpenChange={o => { if (!o) onBack(); }} title="Проверка исправления" wide busy={writing}
      nothingToLose
      footer={(
        <>
          <ModalExit>Вернуться к правке</ModalExit>
          {shown && (
            <Button variant="filled" icon={<Icon icon={Check} size={18} />} loading={writing} onClick={() => onWrite(shown)}>
              Записать исправление
            </Button>
          )}
        </>
      )}>
      <div className="space-y-3 text-sm">
        {pending && <p className="text-on-surface-variant">Считаем, что запишет исправление…</p>}
        {!pending && error != null && (
          <div role="alert" className="flex items-start gap-2 rounded-md border border-error/30 bg-error-container
            px-3 py-2 text-xs text-on-error-container">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
            <span>{apiError(error, 'Исправление не посчитано')}</span>
          </div>
        )}
        {shown && <Shown preview={shown} reason={reason} note={note} places={places} />}
      </div>
    </Modal>
  );
}

function Shown({ preview, reason, note, places }: {
  preview: CorrectionPreview; reason: string; note: string | null; places: Places;
}) {
  const rows = correctionRows(preview, formatDate);
  return (
    <>
      {note && (
        <p role="status" className="flex items-start gap-2 text-xs text-warning">
          <Icon icon={Refresh} size={18} className="shrink-0 mt-0.5" /> {note}
        </p>
      )}
      <p className="text-on-surface">
        Исправление ляжет днём <b>{formatDate(preview.day)}</b>. Версия {preview.version} уйдёт в историю.
      </p>
      <blockquote className="border-l-2 border-outline-variant pl-3 text-xs text-on-surface-variant">
        Причина: {reason.trim()}
      </blockquote>
      {preview.vatMode && <p className="text-on-surface">{vatShiftText(preview.vatMode)}</p>}
      {rows.length === 0
        ? (
          <p className="text-on-surface-variant">
            Деньги не меняются — запишутся только версия{preview.vatMode ? ', причина и режим НДС' : ' и причина'}.
          </p>
        )
        : (
          <>
            <p className="text-xs text-on-surface-variant">Закрытый месяц не изменится: его поправки лягут днём исправления.</p>
            <BookLines rows={rows} places={places} />
          </>
        )}
      {serviceRecords(preview).map((record, index) => (
        <ServiceRecord key={`${record.kind}-${index}`} record={record} places={places} />
      ))}
    </>
  );
}

/**
 * Служебная запись — свёрнутой строкой: открытие книги (как деньги лежали до первой записи) и выравнивание
 * допуском (сдвиг, не записанный раньше). Раскрывается щелчком.
 */
function ServiceRecord({ record, places }: { record: CorrectionRecordView; places: Places }) {
  const [open, setOpen] = useState(false);
  const title = record.kind === 'opening' ? 'Открытие книги: деньги счёта, как они лежали до исправлений'
    : record.kind === 'alignment' ? 'Выравнивание допуском: сдвиг, не записанный раньше'
    : record.kind;
  return (
    <div className="text-xs">
      <button type="button" aria-expanded={open} onClick={() => setOpen(o => !o)}
        className="inline-flex items-center gap-1 text-on-surface-variant hover:text-on-surface">
        {open ? <Icon icon={KeyboardArrowDown} size={18} /> : <Icon icon={ChevronRight} size={18} />}
        {title} — {formatDate(record.day)}, строк: {record.lines.length}
      </button>
      {open && (
        <div className="mt-2">
          <BookLines places={places} rows={plainRows(record.kind, record.lines)} />
        </div>
      )}
    </div>
  );
}
