/**
 * Вид сегментов MD3 ([UI-68]) — один у двух компонентов: `SegmentedButton` (выбор значения, радио) и
 * `Tabs variant="segmented"` (переключатель панели). Роли и клавиатура у них разные, вид — один; тот же
 * приём, что `buttonClasses` у `SplitButton`. Отдельным модулем: файл компонентов отдаёт наружу только
 * компоненты (горячая замена).
 *
 * 40 px, контур `outline`, выбранный — `secondary-container` с галочкой 18. Форма full — радиусами
 * первого и последнего сегмента, без `overflow-hidden` у ряда: он срезал бы цель касания. Соседи делят
 * контур (`-ml-px`), и цель касания у них — только по вертикали: смежные части без зазора не отбирают у
 * соседа видимое. Литералами: Tailwind не сканирует собранные строки.
 */
export const SEGMENT_ROW = 'inline-flex max-w-full';

const SEGMENT =
  'relative inline-flex items-center justify-center gap-2 h-10 min-w-12 px-3 border border-outline ' +
  'text-label-large whitespace-nowrap select-none state-layer focus-ring focus-visible:z-10 ' +
  'disabled-outlined disabled:pointer-events-none touch-target [--_touch-inset:-4px_0]';

const ON = 'bg-secondary-container text-on-secondary-container';
const OFF = 'text-on-surface';

export function segmentClass({ selected, first, last, fill = false }: {
  selected: boolean; first: boolean; last: boolean;
  /** Сегменты делят ширину ряда поровну (ряд во всю ширину). */
  fill?: boolean;
}): string {
  return `${SEGMENT} ${selected ? ON : OFF}${first ? ' rounded-l-full' : ' -ml-px'}${last ? ' rounded-r-full' : ''}${fill ? ' flex-1 min-w-0' : ''}`;
}
