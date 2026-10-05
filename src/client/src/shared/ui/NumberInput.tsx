/**
 * Поле ввода числа в строке таблицы: сумма, количество, процент (задача N2 этапа 2, issue #1103;
 * ТЗ COST-7.2).
 *
 * <p>Заведено потому, что таких полей было шесть и они разошлись: одно забыло `tabular-nums`, и цифры
 * в колонке стояли не друг под другом; у другого единицей измерения служила подсказка «%», которая
 * пропадала с первой набранной цифрой — ровно тогда, когда становилась нужна.</p>
 *
 * <p><b>Значение — текст, а не число.</b> Пока человек набирает «12,», это ещё не число, и переписывать
 * поле на каждый удар по клавише нельзя: курсор прыгнет. Число из базы в текст превращает
 * `formatInput` — тот же форматтер, каким оно показано в таблице.</p>
 *
 * <p>Единица стоит ВНУТРИ рамки поля и не входит в значение: её не выделить и не стереть, и вставка
 * из Excel не принесёт вторую.</p>
 */
export function NumberInput({ value, onChange, label, unit, placeholder, disabled, className = 'w-full' }: {
  value: string;
  onChange: (value: string) => void;
  /** Имя поля для читалки экрана: в строке таблицы подписи над полем нет. */
  label: string;
  /** Единица измерения рядом с числом: «м», «шт», «%». */
  unit?: string | null;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
}) {
  const field = (
    <input value={value} aria-label={label} placeholder={placeholder} disabled={disabled}
      inputMode="decimal" onChange={e => onChange(e.target.value)}
      className={unit ? INNER : `${BARE} ${className}`} />
  );
  if (!unit) return field;

  // Обёртка — `label`, а не `span`: щелчок по единице ставит курсор в поле, как и щелчок по рамке.
  return (
    <label className={`${WRAP} ${className}`}>
      {field}
      <span className="shrink-0 text-xs text-fg4" aria-hidden="true">{unit}</span>
    </label>
  );
}

const BOX = 'rounded border border-stroke bg-surface px-1.5 py-1';
const TEXT = 'text-right text-xs tabular-nums text-fg outline-none placeholder:text-fg4 disabled:text-fg3';

/** Поле без единицы: рамка на самом поле. */
const BARE = `${BOX} ${TEXT} focus:border-primary disabled:bg-surface2`;

/** Рамка вокруг поля с единицей: состояние поля она узнаёт через `focus-within` и `has-[:disabled]`. */
const WRAP = `${BOX} flex items-baseline gap-1 focus-within:border-primary has-[:disabled]:bg-surface2`;

/** Поле внутри рамки с единицей: рамку и отступы держит обёртка. */
const INNER = `min-w-0 flex-1 bg-transparent ${TEXT}`;
