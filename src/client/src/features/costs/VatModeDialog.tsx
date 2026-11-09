import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { formatMoney } from '@/shared/format/format';
import type { VatMode } from '@/shared/api/invoices';
import type { ModePreview } from './invoiceVat';

/**
 * Смена режима НДС — явный шаг (costs-invoice-screen-12-autosave, D-12.6, ТЗ COST-6.5): строки черновика
 * пишутся сами, и переключатель, пишущий молча, менял бы деньги на стройках одним щелчком. Окно показывает,
 * сколько строк пересчитается и какой станет сумма строк, и последствия для разноски; запись — по
 * «Пересчитать», «Отмена» оставляет прежний режим.
 */
export function VatModeDialog({ to, preview, consequences, onConfirm, onCancel }: {
  /** Режим, который выбран; `null` — окно закрыто. */
  to: VatMode | null;
  preview: ModePreview | null;
  /** Что смена сделает с разноской (`modeConsequences`); `null` — строк в счёте нет. */
  consequences: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const changed = preview?.changed ?? 0;
  const touched = changed + (preview?.unknown ?? 0);
  return (
    <ConfirmDialog open={to !== null && preview !== null} onOpenChange={open => { if (!open) onCancel(); }}
      title={to === 'OnTop' ? 'Цены без НДС, НДС сверху?' : 'НДС в сумме строк?'}
      confirmDanger={false}
      confirmLabel={touched > 0 ? `Пересчитать ${rows(touched)}` : 'Сменить режим'}
      description={preview && (
        <div className="space-y-2" data-testid="vat-mode-preview">
          <p>
            {touched > 0
              ? `Пересчитать ${rows(touched)}: сумма строк станет ${formatMoney(preview.after)} (сейчас ${formatMoney(preview.before)}).`
              : 'Суммы строк не изменятся: пересчитывать нечего.'}
          </p>
          {preview.unknown > 0 && (
            <p>
              {`У ${preview.unknown === 1 ? 'одной строки' : `${preview.unknown} строк`} нет ставки НДС: суммы с НДС у `
                + `${preview.unknown === 1 ? 'неё' : 'них'} не будет, пока ставку не укажут, — в сумму строк ${preview.unknown === 1 ? 'она не вошла' : 'они не вошли'}.`}
            </p>
          )}
          {consequences && <p>{consequences}</p>}
          <p>Режим и строки запишутся сразу после подтверждения.</p>
        </div>
      )}
      onConfirm={onConfirm} />
  );
}

/** «1 строку», «3 строки», «6 строк» — винительный падеж после «пересчитать». */
function rows(count: number): string {
  const tens = count % 100;
  const last = count % 10;
  const word = last === 1 && tens !== 11 ? 'строку'
    : last >= 2 && last <= 4 && (tens < 12 || tens > 14) ? 'строки' : 'строк';
  return `${count} ${word}`;
}
