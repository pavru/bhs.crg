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
 * <p>Единица стоит ВНУТРИ поля и не входит в значение: её не выделить и не стереть, и вставка
 * из Excel не принесёт вторую.</p>
 *
 * <p><b>Вид — плотное поле клетки</b> ([UI-69], sharedui-md3-11): 40 px, текст `body-medium`, без рамки —
 * подчёркивание, у фокуса 2 px `primary`, единица суффиксом. Своей рамки у поля нет: в сетке рамку
 * рисуют линии строк, и рамка поля была бы рамкой в рамке.</p>
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
  // Обёртка — `label`: щелчок по единице ставит курсор в поле, как и щелчок по самому полю.
  return (
    <label className={`${WRAP} ${className}`}>
      <input value={value} aria-label={label} placeholder={placeholder} disabled={disabled}
        inputMode="decimal" onChange={e => onChange(e.target.value)} className={INPUT} />
      {unit && <span className="shrink-0 text-body-medium text-on-surface-variant" aria-hidden="true">{unit}</span>}
    </label>
  );
}

/**
 * Подчёркивание — псевдоэлемент обёртки, а не нижняя граница: граница 2 px у фокуса сдвигала бы текст
 * на пиксель. Состояние поля обёртка узнаёт через `focus-within` и `has-[:disabled]`.
 *
 * Наведение исключает фокус и отключённое в самом селекторе, как у рамки `OutlinedField`: `hover:` идёт в
 * собранном CSS позже `focus-within:` при равном весе, и наведённое поле в фокусе теряло бы `primary`.
 */
const WRAP = 'relative flex h-10 items-center gap-1 px-2 cursor-text '
  + "after:pointer-events-none after:absolute after:inset-x-0 after:bottom-0 after:h-px after:bg-outline after:content-[''] "
  + '[&:hover:not(:focus-within):not(:has(:disabled))]:after:bg-on-surface focus-within:after:h-0.5 focus-within:after:bg-primary '
  + 'has-[:disabled]:cursor-default has-[:disabled]:after:bg-on-surface/38';

const INPUT = 'min-w-0 flex-1 bg-transparent text-right text-body-medium tabular-nums text-on-surface outline-none '
  + 'placeholder:text-on-surface-variant disabled-content';
