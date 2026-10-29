/**
 * Слой меню и его пункт — общие у `RowActionsMenu` и `SplitButton`, пока общего `Menu` нет
 * (`sharedui-md3-13-containers`): правка вида пункта не должна обходить одно из меню.
 */
export const MENU_SURFACE = 'rounded-sm border border-outline-variant bg-surface py-1 shadow-elevation-2';

// Подсветка пункта — слоем on-surface ([UI-64]), мышью и стрелкой одинаково (data-highlighted Radix).
export const MENU_ITEM = 'cursor-pointer select-none outline-none state-layer data-[disabled]:pointer-events-none';

/** Отключённое — содержимое on-surface 38 % ([UI-64]): у частей пункта свои цвета, поэтому — частям. */
export const MENU_ITEM_OFF = 'text-on-surface/38';
