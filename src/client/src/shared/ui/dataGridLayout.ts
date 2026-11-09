/**
 * Наименьшая ширина таблицы с фиксированной раскладкой (`DataGrid fixed`, `EditGrid fill`).
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

/** Варианты перед классом (`md:`, `max-lg:`) и `!` после него: ширину они не меняют. */
const CLASS = /^(?:[\w-]+:)*(w-[^:]+?)!?$/;
/** `w-36` — ширина в шагах шкалы. */
const STEPS = /^w-(\d+(?:\.\d+)?)$/;
/** `w-[13rem]`, `w-[180px]` — ширина в скобках единицей, которую CSS поймёт и в `calc`. */
const LENGTH = /^w-\[(\d+(?:\.\d+)?(?:px|rem|em))\]$/;
/** `w-2/5` — доля ширины таблицы. */
const FRACTION = /^w-(\d+)\/(\d+)$/;

/** Ширина одного класса: шаги шкалы, длина CSS или доля таблицы; `null` — не прочесть. */
type Length = { steps: number } | { css: string };
type Width = Length | { share: number };

function widthOf(token: string): Width | null {
  const cls = token.match(CLASS)?.[1];
  if (!cls) return null;
  if (cls === 'w-px') return { css: '1px' };
  const step = cls.match(STEPS);
  if (step) return { steps: Number(step[1]) };
  const length = cls.match(LENGTH);
  if (length) return { css: length[1] };
  const fraction = cls.match(FRACTION);
  if (fraction && Number(fraction[2]) > 0) return { share: Number(fraction[1]) / Number(fraction[2]) };
  return null;
}

const asCss = (w: Length) => ('css' in w ? w.css : `var(--spacing) * ${w.steps}`);

/**
 * CSS-значение `min-width` таблицы — или `undefined`, когда гибких колонок нет и сжиматься нечему.
 *
 * Ширина колонки — классы Tailwind (`width`). Понятны шаг шкалы (`w-36`), `w-px`, длина в скобках
 * (`w-[13rem]`, `w-[180px]`) и доля (`w-2/5`). Ширин у колонки несколько (`w-24 md:w-40`) — в счёт идёт
 * наибольшая: какая из них действует, решает экран, а запас лишь добавляет прокрутки. Колонка без ширины и
 * с шириной, которую не прочесть (`w-auto`, `w-[calc(…)]`), считается гибкой и получает минимум: лучше
 * лишняя прокрутка, чем колонка в ноль.
 */
export function fixedTableMinWidth(columns: { width?: string }[]): string | undefined {
  let steps = 0;
  let share = 0;
  let flexible = 0;
  const terms: string[] = [];
  for (const c of columns) {
    const lengths: Length[] = [];
    const shares: number[] = [];
    for (const token of c.width?.split(/\s+/) ?? []) {
      const w = widthOf(token);
      if (w === null) continue;
      if ('share' in w) shares.push(w.share);
      else lengths.push(w);
    }
    if (lengths.length > 0) {
      if (lengths.every(w => 'steps' in w)) steps += Math.max(...lengths.map(w => w.steps));
      else terms.push(lengths.length === 1 ? asCss(lengths[0]) : `max(${lengths.map(asCss).join(', ')})`);
    } else if (shares.length > 0) share += Math.max(...shares);
    else flexible++;
  }
  if (flexible === 0 || share >= 1) return undefined;
  const sum = [`var(--spacing) * ${steps + FLEX_COLUMN_MIN * flexible}`, ...terms].join(' + ');
  // Доли берутся от ширины самой таблицы: на остальные колонки остаётся (1 − доля) её.
  return share > 0
    ? `calc((${sum}) / ${Math.round((1 - share) * 10_000) / 10_000})`
    : `calc(${sum})`;
}
