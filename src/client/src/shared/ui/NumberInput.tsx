import { useId, useState } from 'react';
import { OutlinedField } from './OutlinedField';
import { fieldBox, useFieldDensity } from './fieldBox';

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
 * <p><b>Вид — по месту</b> ([UI-69], [UI-78]): в клетке сетки правки — поле клетки (`density="cell"`, его
 * отдаёт тело `EditGrid`): без рамки и черты в покое, у фокуса черта 2 px `primary`. Вне сетки — плотное поле
 * 40 с рамкой: шапка колонки с вводом (процент объекта в матрице разноски, В5), строка формы. Рамку и черту
 * рисует общая оболочка `OutlinedField`, текст — `body-medium`, единица — суффиксом.</p>
 */
export function NumberInput({ value, onChange, label, unit, placeholder, disabled, density: own, className = 'w-full' }: {
  value: string;
  onChange: (value: string) => void;
  /** Имя поля для читалки экрана: в строке таблицы подписи над полем нет. */
  label: string;
  /** Единица измерения рядом с числом: «м», «шт», «%». */
  unit?: string | null;
  placeholder?: string;
  disabled?: boolean;
  /** Плотное поле с рамкой или клетка; не задан — размер места (в теле `EditGrid` — клетка), иначе плотное. */
  density?: 'dense' | 'cell';
  className?: string;
}) {
  const density = useFieldDensity(own, 'dense');
  const [focused, setFocused] = useState(false);
  const labelId = useId();
  // Обёртка — `label`: щелчок по единице ставит курсор в поле, как и щелчок по самому полю. В клетке имя —
  // `aria-label` поля, а подписи оболочки нет: текстом она вошла бы в текст строки. Вне сетки — подпись
  // оболочки для диктора, и поле ссылается на неё.
  const cell = density === 'cell';
  return (
    <OutlinedField label={label} labelId={labelId} raise="always" density={density} focused={focused}
      containerClassName={className}>
      <label className={`flex items-center gap-1 cursor-text has-[:disabled]:cursor-default ${fieldBox(density)}`}>
        <input value={value} aria-label={cell ? label : undefined} aria-labelledby={cell ? undefined : labelId}
          placeholder={placeholder} disabled={disabled}
          inputMode="decimal" onChange={e => onChange(e.target.value)} className={INPUT}
          onFocus={() => setFocused(true)} onBlur={() => setFocused(false)} />
        {unit && <span className="shrink-0 text-body-medium text-on-surface-variant" aria-hidden="true">{unit}</span>}
      </label>
    </OutlinedField>
  );
}

const INPUT = 'min-w-0 flex-1 bg-transparent text-right text-body-medium tabular-nums text-on-surface outline-none '
  + 'placeholder:text-on-surface-variant disabled-content';
