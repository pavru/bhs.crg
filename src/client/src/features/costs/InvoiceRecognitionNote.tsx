import type { InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import { hasRecognitionNotes, unusedLinesNote, type FieldOffer } from './recognition';
import { Tooltip } from '@/shared/ui/Tooltip';
import { UnplacedReads } from './UnplacedReads';
import { READINESS_ATTR } from './invoiceReadiness';

/**
 * Распознавание скана в форме счёта (issue #1077): замечания и «В файле: … Взять» у поля. «Распознаётся…»,
 * отказ с причиной и «по прежнему виду» — пункты индикатора состояния (costs-invoice-screen-11, D-11.2): отказ
 * выглядит отказом и там — пунктом «Мешает работе» с причиной и banner.
 */

/**
 * Замечания распознавания, строки, которые в счёт не легли, и прочитанное без места в счёте (#1484) —
 * первым блоком прокручиваемой части. Блок показан и ради одного непустого списка: распознавание без
 * замечаний, но с колонкой «для наборов данных», иначе молчало бы.
 */
export function RecognitionNotes({ recognition }: { recognition: InvoiceRecognition | undefined }) {
  if (!hasRecognitionNotes(recognition) || recognition?.state !== 'done') return null;
  const notes = recognition.notes;
  const lines = unusedLinesNote(recognition);
  const unplaced = recognition.unplaced;
  return (
    // Сюда ведёт пункт индикатора «Замечания распознавания — Показать» (`part:who`, 11).
    <section className="space-y-1 text-body-small" {...{ [READINESS_ATTR]: 'part:who' }}>
      <h2 className="text-body-medium font-medium text-on-surface-variant">Замечания распознавания</h2>
      {(notes.length > 0 || lines) && (
        <ul className="list-disc pl-4 space-y-0.5 text-warning">
          {notes.map(note => <li key={note}>{note}</li>)}
          {lines && <li>{lines}</li>}
        </ul>
      )}
      <UnplacedReads list={unplaced} />
    </section>
  );
}

/** Строка под полем: «В скане: … · Взять». «Взять» кладёт значение в правки формы и не сохраняет. */
export function OfferLine({ offer, onTake }: { offer: FieldOffer; onTake: (value: unknown) => void }) {
  return (
    <Tooltip content={offer.title ?? `В файле: ${offer.text}`}>
      <p className="mt-0.5 flex items-baseline gap-1 text-body-small text-on-surface-variant min-w-0">
        <span className="truncate">В файле: {offer.text}</span>
        {offer.take !== undefined
          ? (
            <button type="button" className="shrink-0 text-primary hover:underline" onClick={() => onTake(offer.take)}>
              Взять
            </button>
          )
          // Значение не разобрано или его нечем положить в поле: показать — честно, предложить «Взять» — нет.
          : <span className="shrink-0">— впишите вручную</span>}
      </p>
    </Tooltip>
  );
}
