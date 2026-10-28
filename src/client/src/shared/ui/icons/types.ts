/**
 * Значок набора ([UI-60]): путь контура Material Symbols в квадрате `0 -960 960 960`. Рисует его
 * `shared/ui/Icon`; значения — из `icons.generated.ts`, руками их не пишут.
 */
export interface IconDef {
  readonly d: string;
  /** Путь варианта с заливкой — только у значков с `fill` в манифесте. */
  readonly fill?: string;
}

/**
 * Шкала размеров значка ([UI-60], решение владельца 28.10.2026): 18 — в тексте, кнопке, чипе, строке
 * таблицы; 20 и 24 — кнопка-иконка и навигация; 40 и 48 — только пустые состояния. Других размеров
 * нет: их не пропускают ни тип, ни храповик `icon-size`.
 */
export const ICON_SIZES = [18, 20, 24, 40, 48] as const;
export type IconSize = typeof ICON_SIZES[number];

/** Значок с вариантом заливки: только он принимает `filled` у `Icon` — у прочих это ошибка типа. */
export interface FilledIconDef extends IconDef {
  readonly fill: string;
}
