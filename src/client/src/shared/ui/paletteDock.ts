import type { CSSProperties } from 'react';
import { WIDE } from '@/shared/hooks/useMedia';

/**
 * Поверхность палитры — своя, а не `LAYER_SURFACE` меню ([UI-76]): палитра — search view MD3,
 * `surface-container-high` уровня 3 (sharedui-md3-15).
 */
export const PALETTE_SURFACE = 'bg-surface-container-high shadow-elevation-3';

/** Уже этого дверь ужата до значка, и встать на её место палитра не может — откат на центр. */
const MIN_ANCHOR = 240;
/** Палитра не уже этого, даже если строка поиска уже. */
const MIN_WIDTH = 560;
const EDGE = 16;

/**
 * Место docked search view: поле — на месте строки поиска, левый край — её, ширина — большее из её
 * ширины и {@link MIN_WIDTH}, не за краем окна. `null` — по центру: двери нет, она ужата до значка
 * или окно уже `WIDE` — там своё состояние оболочки. Порог — тот же запрос `WIDE`, что у оболочки, а не
 * число: разойдясь, палитра вставала бы на место строки поиска телефонной шапки.
 */
export function dockedPlace(anchor: HTMLElement | null | undefined, viewport: number,
  wide = window.matchMedia(WIDE).matches): CSSProperties | null {
  if (!anchor || !wide) return null;
  const r = anchor.getBoundingClientRect();
  if (r.width < MIN_ANCHOR) return null;
  const width = Math.min(Math.max(r.width, MIN_WIDTH), viewport - 2 * EDGE);
  const left = Math.max(EDGE, Math.min(r.left, viewport - width - EDGE));
  return { left, top: r.top, width };
}
