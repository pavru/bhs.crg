/**
 * Текстовое поле условия отбора — той же коробкой, что плотный `Select density="dense"` рядом с ним (#1469,
 * [UI-69]): 40, `body-medium`, рамка формы 4, обводка 2 px при фокусе (второй пиксель — кольцом внутрь, текст
 * не сдвигается). Своя рамка у поля в строке из трёх давала разную высоту и цвет.
 */
export const FIELD_CLS = 'h-10 px-3 rounded-xs border border-outline bg-transparent text-body-medium text-on-surface '
  + 'placeholder:text-on-surface-variant outline-none [&:hover:not(:focus)]:border-on-surface '
  + 'focus:border-primary focus:inset-ring focus:inset-ring-primary';
