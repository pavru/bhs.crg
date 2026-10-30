/**
 * Общие классы кнопки-действия: их собирают `Button` и обе половины `SplitButton` ([UI-83]). Здесь нет
 * ни формы, ни тени — у половин split-кнопки они свои: `rounded-full` в одном `class` с `rounded-r-xs`
 * решал бы угол порядком утилит в собранном CSS, а тень на каждой половине легла бы швом в зазор.
 * Форму и тень filled добавляет `Button` сам. Отдельным модулем, а не в `Button.tsx`: файл компонентов
 * отдаёт наружу только компоненты (горячая замена).
 *
 * Высота кнопки одна — 40 px ([UI-68]): кнопкам плотность MD3 не применяется (D7 эпика sharedui-md3),
 * и размера у кнопки нет. Здесь — единственное место этого числа.
 */
export type ButtonVariant = 'filled' | 'tonal' | 'outlined' | 'text';

// Состояния — слоем MD3 ([UI-64]): наведение, фокус и нажатие — цвет подписи поверх заливки
// (`state-layer`), кольцо — общее (`focus-ring`), отключённое — `disabled-container` / `disabled-content`.
// Своих цветов наведения у вариантов нет.
export const BASE =
  'inline-flex items-center justify-center gap-2 select-none ' +
  'state-layer focus-ring disabled:pointer-events-none';

/** Высота кнопки — отдельно от полей: стрелке split-кнопки нужна высота без полей. Литералами:
 *  Tailwind не сканирует собранные строки. */
export const HEIGHT = 'h-10';
/** Многострочная кнопка растёт при переносе: высота — нижняя граница. */
export const MIN_HEIGHT = 'min-h-10';
/** Подпись — label-large MD3 (14/20, 500). */
export const LABEL = 'text-label-large';

/**
 * Поля по MD3: 24 px, со стороны знака 16; у text — 12, со стороны, противоположной знаку, 16. Функция,
 * а не таблица: поля зависят от вида и от того, с какой стороны знак.
 */
export function padding(variant: ButtonVariant, { leading = false, trailing = false } = {}): string {
  if (variant === 'text') return `${leading && !trailing ? 'pl-3 pr-4' : trailing && !leading ? 'pl-4 pr-3' : 'px-3'}`;
  return `${leading ? 'pl-4' : 'pl-6'} ${trailing ? 'pr-4' : 'pr-6'}`;
}

/** Тень filled-кнопки — только при наведении (уровень 1, MD3). Её ставит только `Button`: у
 *  split-кнопки тени нет и при наведении ([UI-83]). */
export const FILLED_HOVER_SHADOW = 'hover:shadow-elevation-1';

/** Цвет и отключённое варианта — без формы и тени. */
export function variantClass(variant: ButtonVariant, danger = false): string {
  switch (variant) {
    case 'filled':
      return (danger ? 'bg-error text-on-error' : 'bg-primary text-on-primary') + ' disabled-container';
    case 'tonal':
      return 'bg-secondary-container text-on-secondary-container disabled-container';
    case 'outlined':
      return `border border-outline ${danger ? 'text-error' : 'text-primary'} disabled-outlined`;
    case 'text':
      return `${danger ? 'text-error' : 'text-primary'} disabled-content`;
  }
}
