import { useState } from 'react';
import { Check, ChevronDown, ChevronRight, CircleHelp, RefreshCw, TriangleAlert } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { apiError } from '@/shared/utils/apiError';
import { formatDate, formatMoney } from '@/shared/format/format';
import type { CorrectionLineView, CorrectionPreview, CorrectionRecordView } from '@/shared/api/invoiceCorrection';
import { correctionRows, serviceRecords, signed, type PreviewRow } from './correction';
import { placeName, usePlaces, type Places } from './places';

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
            <Button variant="filled" icon={<Check size={13} />} loading={writing} onClick={() => onWrite(shown)}>
              Записать исправление
            </Button>
          )}
        </>
      )}>
      <div className="space-y-3 text-sm">
        {pending && <p className="text-fg3">Считаем, что запишет исправление…</p>}
        {!pending && error != null && (
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-danger/30 bg-danger-subtle
            px-3 py-2 text-xs text-danger">
            <TriangleAlert size={14} className="shrink-0 mt-0.5" />
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
          <RefreshCw size={13} className="shrink-0 mt-0.5" /> {note}
        </p>
      )}
      <p className="text-fg1">
        Исправление ляжет днём <b>{formatDate(preview.day)}</b>. Версия {preview.version} уйдёт в историю.
      </p>
      <blockquote className="border-l-2 border-stroke pl-3 text-xs text-fg2">
        Причина: {reason.trim()}
      </blockquote>
      {rows.length === 0
        ? <p className="text-fg3">Деньги не меняются — запишутся только версия и причина.</p>
        : (
          <>
            <p className="text-xs text-fg3">Закрытый месяц не изменится: его поправки лягут днём исправления.</p>
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
        className="inline-flex items-center gap-1 text-fg2 hover:text-fg1">
        {open ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
        {title} — {formatDate(record.day)}, строк: {record.lines.length}
      </button>
      {open && (
        <div className="mt-2">
          <BookLines places={places}
            rows={record.lines.map((line, i): PreviewRow => ({ kind: 'line', key: `${record.kind}-${i}`, line }))} />
        </div>
      )}
    </div>
  );
}

function BookLines({ rows, places }: { rows: PreviewRow[]; places: Places }) {
  const line = (row: PreviewRow): CorrectionLineView | null => (row.kind === 'line' ? row.line : null);
  const columns: DataGridColumn<PreviewRow>[] = [
    { key: 'place', label: 'Куда', cell: row => { const l = line(row); return l ? where(l, places) : null; } },
    { key: 'day', label: 'День', cell: row => { const l = line(row); return l ? formatDate(l.day) : null; } },
    {
      key: 'gross', label: 'С НДС ±', align: 'right',
      cell: row => { const l = line(row); return l ? <span className="tabular-nums">{signed(l.gross, formatMoney)}</span> : null; },
    },
    {
      key: 'net', label: 'Без НДС ±', align: 'right',
      cell: row => {
        const l = line(row);
        if (!l) return null;
        return (
          <span className="inline-flex items-center gap-1 tabular-nums">
            {l.vatUnknown && (
              <span title="НДС неизвестен: сумма без НДС не посчитана точно" className="text-warning">
                <CircleHelp size={12} aria-label="НДС неизвестен" />
              </span>
            )}
            {signed(l.net, formatMoney)}
          </span>
        );
      },
    },
  ];
  return (
    <DataGrid density="compact" rows={rows} rowKey={row => row.key} columns={columns}
      section={row => (row.kind === 'group' ? row.title : null)} />
  );
}

/** Куда ляжет строка: стройка (с разделом) или статья; без цели — остаток счёта. */
function where(line: CorrectionLineView, places: Places): string {
  if (!line.constructionId && !line.articleId) return 'Не разнесено (остаток счёта)';
  return placeName({ construction: line.constructionId, section: line.sectionId, article: line.articleId }, places);
}
