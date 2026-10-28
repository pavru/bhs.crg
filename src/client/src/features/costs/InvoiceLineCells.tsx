import { NumberInput } from '@/shared/ui/NumberInput';
import { withArchiveWord } from '@/shared/ui/archive';
import { LOST } from './lostReferences';
import type { LineDraft } from './invoiceLines';

/** Клетки строк счёта (#1354, 2/3): поле правимой строки и позиция запертой. Колонки — `useInvoiceLineColumns`. */

/** Позиция запертой строки — словом; пометка «запомнено» тоже словом: отменять здесь нечего. */
export function LockedPosition({ draft }: { draft: LineDraft }) {
  return (
    <span className={draft.nomenclatureLost ? 'text-error' : undefined}>
      {draft.nomenclatureLost ? (draft.nomenclatureMoved ? LOST.movedPosition : LOST.position)
        : draft.nomenclatureName === null ? '—'
        : withArchiveWord(draft.nomenclatureName, !!draft.nomenclatureArchived)}
      {draft.matchedBy !== null && !draft.nomenclatureLost && <span className="text-fg4"> (запомнено)</span>}
    </span>
  );
}

/**
 * Клетка — своим полем, а не общим `TextField`: тот рисует подпись над полем, а в таблице подпись стоит
 * в шапке колонки, и повторять её двенадцать раз значит потерять таблицу.
 *
 * <p>Подпись при этом есть — невидимая (`aria-label`): без неё поле не имеет имени ни для чтения с
 * экрана, ни для живого прогона, который ищет клетку по имени.</p>
 *
 * <p>⚠️ Подсказка в пустом числовом поле — это ПОСЧИТАННОЕ значение («48,50»), а не пример. Так видно,
 * что сумму можно не набирать: её досчитают по количеству и цене. Набранное значение подсказку
 * перекрывает, и досчитанного тогда нет — присланное не пересчитывается.</p>
 */
export function Cell({ value, label, numeric, placeholder, onChange, onBlur }: {
  value: string;
  label: string;
  numeric?: boolean;
  placeholder?: string;
  onChange: (value: string) => void;
  /** Поле покинули — у текстовых клеток ключа строки (наименование, артикул). */
  onBlur?: () => void;
}) {
  if (numeric)
    return <NumberInput value={value} label={label} placeholder={placeholder} onChange={onChange} />;
  return (
    <input value={value} aria-label={label} placeholder={placeholder}
      onChange={e => onChange(e.target.value)} onBlur={onBlur}
      className="w-full rounded border border-outline-variant bg-surface px-1.5 py-1 text-xs text-on-surface
        outline-none focus:border-primary placeholder:text-fg4" />
  );
}
