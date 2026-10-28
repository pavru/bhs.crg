import { Copy } from 'lucide-react';
import type { InvoiceView } from '@/shared/api/invoices';
import { DRAFT, useInvoiceDelete } from './invoiceDelete';
import { K, duplicateLabel, sameFileLabel, twinsOf } from './invoiceFields';

/**
 * Оговорки «у этого счёта есть двойник» — обе СО ССЫЛКОЙ и БЕЗ запрета (ТЗ COST-6.2):
 * тот же файл (issue #1295) и те же поставщик, номер и дата.
 *
 * Один «другой» счёт называется один раз: совпавший и файлом, и реквизитами стоит в оговорке о файле
 * — она сильнее, содержимое совпало побайтно. И действие удаления на форме одно: две двери в один
 * диалог рядом читались бы как два разных удаления.
 */
export function InvoiceTwinNotes({ view, onOpenInvoice }: { view: InvoiceView; onOpenInvoice: (id: string) => void }) {
  const askDelete = useInvoiceDelete();
  const { sameFile, both, duplicates } = twinsOf(view);
  const link = (id: string, text: string) => (
    <button type="button" className="underline hover:no-underline font-medium" onClick={() => onOpenInvoice(id)}>
      {text}
    </button>
  );
  // У разобранного, отклонённого и оплаченного пара с одним файлом — скорее всего законная (в файле
  // несколько счетов): вечное жёлтое предупреждение на ней только приучало бы его не читать. Но не
  // когда совпали ещё и поставщик, номер и дата: это самый сильный признак дубля, и тише оговорки
  // об одних реквизитах он звучать не вправе — у разобранного счёта это риск оплатить дважды.
  const loud = view.requisites[K.state] === DRAFT || both.size > 0;

  return (
    <>
      {sameFile.length > 0 && (loud ? (
        <div className="flex items-start gap-2 rounded-lg border border-warning-border bg-warning-container px-3 py-2"
          data-testid="same-file-note">
          <Copy size={14} className="text-warning shrink-0 mt-0.5" />
          <div className="min-w-0 text-xs text-warning">
            <span>{sameFile.length > 1 ? 'Этот файл уже приложен к другим счетам: ' : 'Этот файл уже приложен к другому счёту: '}</span>
            {sameFile.map((s, i) => (
              <span key={s.id}>
                {/* Точка с запятой: внутри подписи запятые свои. */}
                {i > 0 && '; '}
                {link(s.id, sameFileLabel(s))}
                {both.has(s.id) && ' — поставщик, номер и дата тоже совпадают'}
              </span>
            ))}
            <span>. Если в файле несколько счетов — так и оставьте.</span>
            {/* Только у счёта, который удалить можно (issue #1282). */}
            {askDelete && (
              <button type="button" className="ml-1 underline hover:no-underline font-medium"
                onClick={() => askDelete('same-file')}>
                Загружен дважды — удалить этот черновик…
              </button>
            )}
          </div>
        </div>
      ) : (
        <p className="text-xs text-fg3" data-testid="same-file-note">
          {sameFile.length > 1 ? 'Тот же файл приложен ещё к счетам: ' : 'Тот же файл приложен ещё к счёту: '}
          {sameFile.map((s, i) => <span key={s.id}>{i > 0 && '; '}{link(s.id, sameFileLabel(s))}</span>)}.
        </p>
      ))}
      {duplicates.length > 0 && (
        <div className="flex items-start gap-2 rounded-lg border border-warning-border bg-warning-container px-3 py-2">
          <Copy size={14} className="text-warning shrink-0 mt-0.5" />
          <div className="min-w-0 text-xs text-warning">
            <span>Похоже на дубликат — у этого поставщика уже есть счёт с тем же номером и датой: </span>
            {duplicates.map((d, i) => <span key={d.id}>{i > 0 && ', '}{link(d.id, duplicateLabel(d))}</span>)}
            <span>. Сохранить всё равно можно: одинаковые номера у поставщика бывают.</span>
            {/* Короткий путь (issue #1282) — только у счёта, который удалить можно, и только когда
                то же действие не стоит уже в оговорке о файле. */}
            {askDelete && sameFile.length === 0 && (
              <button type="button" className="ml-1 underline hover:no-underline font-medium"
                onClick={() => askDelete('duplicate')}>
                Это дубль — удалить этот счёт…
              </button>
            )}
          </div>
        </div>
      )}
    </>
  );
}
