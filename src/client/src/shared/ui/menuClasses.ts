/**
 * Поверхность слоя над окном и страницей ([UI-76], sharedui-md3-13): меню, список `Select`, подсказки
 * поля и поповеры. Контейнер `surface-container` — другой, чем у окна (`surface-container-high`), и своя
 * тень уровня 2; рамки нет. Форму слой задаёт сам: у меню и списка — 4, у поповера — своя.
 */
export const LAYER_SURFACE = 'bg-surface-container shadow-elevation-2';

/** Слой меню и списка: радиус 4 и отступ 8 сверху и снизу — пункты от края. */
export const MENU_SURFACE = `rounded-xs py-2 ${LAYER_SURFACE}`;

// Подсветка пункта — слоем on-surface ([UI-64]), мышью и стрелкой одинаково (data-highlighted Radix).
export const MENU_ITEM = 'cursor-pointer select-none outline-none state-layer data-[disabled]:pointer-events-none';
