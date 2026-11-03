/**
 * Поверхность слоя над окном и страницей ([UI-76], sharedui-md3-13): меню, список `Select`, подсказки
 * поля, поповеры, палитра команд и богатая подсказка. Контейнер `surface-container` — другой, чем у окна
 * (`surface-container-high`), и своя тень уровня 2; рамки нет. Форму слой задаёт сам: у меню и списка —
 * 4, у поповера — своя. Слой и пункт меню — в `Menu.tsx`: собирать меню руками экрану незачем.
 */
export const LAYER_SURFACE = 'bg-surface-container shadow-elevation-2';

/**
 * Поверхность календаря — токен компонента docked date picker ([UI-80], md3-16): `surface-container-high`
 * и возвышение 3, радиус 16 задаёт слой. Единственное исключение из `LAYER_SURFACE`: от окна того же
 * контейнера календарь отделяет тень уровнем выше. Сторож `menu-layer.test.mjs` пропускает только её.
 */
export const DATE_PICKER_SURFACE = 'bg-surface-container-high shadow-elevation-3';
