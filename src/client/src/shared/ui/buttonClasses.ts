/**
 * Общие классы кнопки-действия: их собирают `Button` и обе половины `SplitButton` ([UI-83]). Здесь нет
 * ни формы, ни тени — у половин split-кнопки они свои: `rounded-full` в одном `class` с `rounded-r-xs`
 * решал бы угол порядком утилит в собранном CSS, а тень на каждой половине легла бы швом в зазор.
 * Форму и тень filled добавляет `Button` сам. Отдельным модулем, а не в `Button.tsx`: файл компонентов
 * отдаёт наружу только компоненты (горячая замена).
 */
export type ButtonVariant = 'filled' | 'tonal' | 'outlined' | 'text';
export type ButtonSize = 'sm' | 'md' | 'lg';

// Состояния — слоем MD3 ([UI-64]): наведение, фокус и нажатие — цвет подписи поверх заливки
// (`state-layer`), кольцо — общее (`focus-ring`), отключённое — `disabled-container` / `disabled-content`.
// Своих цветов наведения у вариантов нет.
export const BASE =
  'inline-flex items-center justify-center gap-2 font-medium select-none ' +
  'state-layer focus-ring disabled:pointer-events-none';

/** Высота кнопки по размеру — отдельно от полей: стрелке split-кнопки нужна высота без полей. */
export const HEIGHT: Record<ButtonSize, string> = { sm: 'h-8', md: 'h-9', lg: 'h-10' };

export const SIZE: Record<ButtonSize, string> = {
  sm: `${HEIGHT.sm} px-3 text-body-small`,
  md: `${HEIGHT.md} px-4 text-body-medium`,
  lg: `${HEIGHT.lg} px-6 text-body-medium`,
};

/** Тень filled-кнопки. Её ставит только `Button`: у split-кнопки тени нет (design D0). */
export const FILLED_SHADOW = 'shadow-elevation-1';

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
