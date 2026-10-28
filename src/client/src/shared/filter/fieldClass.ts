/**
 * Текстовое поле условия отбора — той же коробкой, что `Select size="sm"` рядом с ним (#1469): высота
 * h-7, радиус, рамка и кольцо фокуса. Своя рамка у поля в строке из трёх давала разную высоту и цвет.
 */
export const FIELD_CLS = 'h-7 px-2 rounded-md border border-stroke-strong bg-surface text-xs text-fg1 '
  + 'placeholder:text-fg4 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand';
