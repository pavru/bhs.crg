/**
 * Наименьшая ширина таблицы с фиксированной раскладкой (`DataGrid fixed`).
 *
 * При `table-fixed` колонки с шириной получают её всегда, а колонки без ширины делят остаток. Когда
 * сумма ширин больше экрана (телефон), остатка нет: гибкая колонка — обычно наименование —
 * сжимается до нуля, и текст исчезает, хотя обёртка умеет прокручиваться вбок. Таблица с этой
 * наименьшей шириной не уже суммы своих колонок и минимума гибких — и на узком экране прокручивается
 * (docs/UI.md, «Таблицы»: таблица шире окна прокручивается вбок сама), а на широком растягивается
 * как прежде: лишнее место по-прежнему уходит гибким колонкам.
 */

/** Наименьшая ширина гибкой колонки — в шагах `--spacing` (48 × 4 px = 192 px). */
export const FLEX_COLUMN_MIN = 48;

/** `w-36` — ширина в шагах шкалы. */
const STEPS = /^w-(\d+(?:\.\d+)?)$/;
/** `w-2/5` — доля ширины таблицы. */
const FRACTION = /^w-(\d+)\/(\d+)$/;

/**
 * CSS-значение `min-width` таблицы — или `undefined`, когда гибких колонок нет и сжиматься нечему.
 *
 * Ширина колонки — класс Tailwind (`width`). Шаг шкалы (`w-36`) и доля (`w-2/5`) понятны и в счёт идут
 * точно; колонка без ширины, `w-auto` и всякая ширина, которую не прочесть (`w-[…]`), считаются гибкими
 * и получают минимум: лучше лишняя прокрутка, чем колонка в ноль.
 */
export function fixedTableMinWidth(columns: { width?: string }[]): string | undefined {
  let steps = 0;
  let share = 0;
  let flexible = 0;
  for (const c of columns) {
    const width = c.width?.split(/\s+/).find(cls => cls.startsWith('w-'));
    const step = width?.match(STEPS);
    const fraction = width?.match(FRACTION);
    if (step) steps += Number(step[1]);
    else if (fraction && Number(fraction[2]) > 0) share += Number(fraction[1]) / Number(fraction[2]);
    else flexible++;
  }
  if (flexible === 0 || share >= 1) return undefined;
  const fixedPart = `var(--spacing) * ${steps + FLEX_COLUMN_MIN * flexible}`;
  // Доли берутся от ширины самой таблицы: на шаги и гибкие колонки остаётся (1 − доля) её.
  return share > 0
    ? `calc(${fixedPart} / ${Math.round((1 - share) * 10_000) / 10_000})`
    : `calc(${fixedPart})`;
}
