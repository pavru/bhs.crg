import { useQueryClient } from '@tanstack/react-query';
import { IconButton } from '@/shared/ui/Button';
import { StarShine } from '@/shared/ui/icons';
import type { CostsOrganization, InvoiceView } from '@/shared/api/invoices';
import { recognitionKey } from '@/shared/api/invoiceRecognition';
import { catalogRef, refEntryId } from './invoiceFields';
import { InvoicePartyNote } from './InvoicePartyNote';
import { OfferLine } from './InvoiceRecognitionNote';
import { SIDE_OF, fieldOffer } from './recognition';
import { useRecognition } from './recognitionWatch';

/** Пояснение метки «со скана» — подсказкой кнопки хвоста. */
export const SCAN_MARK_HINT = 'Со скана, не проверено. Правка снимает метку';

/** Приписка к подписи поля для диктора: метку он не видит, а пояснение под полем ушло. */
export const SCAN_MARK_SPOKEN = '(со скана, не проверено)';

/**
 * Метка «со скана, не проверено» в хвосте поля (11, D-11.4, Э7): кнопка места XS «Показать на скане» — знаком
 * со скана, подсказкой с пояснением. Высота поля не меняется: ни рамки, ни строки подсказки под ним. Щелчок
 * открывает файл счёта — сверять значение с ним.
 *
 * <p>Только у полей на `TextField`: у выбора организации и дат хвоста под кнопку нет — им знак ставит change
 * `sharedui-*` (вариант А, 07.11.2026, задача 4.1a).</p>
 */
export function ScanMark({ onShow }: { onShow: () => void }) {
  return (
    <IconButton icon={StarShine} label="Показать на скане" title={SCAN_MARK_HINT} iconClassName="text-warning"
      onClick={onShow} data-testid="scan-mark" />
  );
}

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
        <InvoicePartyNote invoiceId={view.id} side={side} party={party} chosen={chosen}
          // «Взять» найденную не повторяется, только когда под полем уже предложена ОНА ЖЕ.
          offered={offer !== null && refEntryId(offer.take) === party.match}
          onChoose={id => set(fieldKey, catalogRef(id))}
          onRetry={() => void qc.invalidateQueries({ queryKey: recognitionKey(view.id) })} />
      )}
      {/* Распознано, а про сторону в скане ничего: пустое поле иначе неотличимо от «ещё не смотрели». */}
      {side && !party && recognition.state === 'done' && recognition.parties && chosen === null && (
        <p className="mt-0.5 text-body-small text-on-surface-variant">в файле не прочитан</p>
      )}
    </>
  );
}
