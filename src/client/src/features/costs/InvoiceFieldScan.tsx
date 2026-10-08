import { useQueryClient } from '@tanstack/react-query';
import type { CostsOrganization, InvoiceView } from '@/shared/api/invoices';
import { recognitionKey } from '@/shared/api/invoiceRecognition';
import { catalogRef, refEntryId } from './invoiceFields';
import { InvoicePartyNote } from './InvoicePartyNote';
import { OfferLine } from './InvoiceRecognitionNote';
import { SIDE_OF, fieldOffer } from './recognition';
import { useRecognition } from './recognitionWatch';

/**
 * Что скан говорит о поле шапки — строками под полем (issue #1077): предложение «В скане: … · Взять»
 * и, у поставщика с плательщиком, что справочник знает об организации из скана.
 *
 * <p>Рисуется только у счёта, который правят: читателю и запертому счёту предлагать нечего. Стороны
 * сервер присылает только черновику — у разобранного счёта выбирать сторону поздно.</p>
 */
export function InvoiceFieldScan({ fieldKey, view, edits, set, organizations }: {
  fieldKey: string; view: InvoiceView; edits: Readonly<Record<string, unknown>>;
  set: (key: string, next: unknown) => void; organizations: CostsOrganization[];
}) {
  const recognition = useRecognition();
  const qc = useQueryClient();
  if (!recognition) return null;

  const side = SIDE_OF[fieldKey];
  const party = side ? recognition.parties?.[side] ?? null : null;
  const offer = fieldOffer(fieldKey, view, edits, recognition,
    id => organizations.find(o => o.id === id)?.name
      ?? party?.candidates.find(c => c.id === id)?.name ?? null);
  const chosen = refEntryId(fieldKey in edits ? edits[fieldKey] : view.requisites[fieldKey]);

  return (
    <>
      {offer && <OfferLine offer={offer} onTake={value => set(fieldKey, value)} />}
      {side && party && (
        <InvoicePartyNote invoiceId={view.id} side={side} party={party} chosen={chosen} offered={offer !== null}
          onChoose={id => set(fieldKey, catalogRef(id))}
          onRetry={() => void qc.invalidateQueries({ queryKey: recognitionKey(view.id) })} />
      )}
      {/* Распознано, а про сторону в скане ничего: пустое поле иначе неотличимо от «ещё не смотрели». */}
      {side && !party && recognition.state === 'done' && recognition.parties && chosen === null && (
        <p className="mt-0.5 text-xs text-fg4">в скане не прочитан</p>
      )}
    </>
  );
}
