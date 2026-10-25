/**
 * Чистая часть просмотрщика PDF (issue #1490, [UI-11], [UI-16]): масштаб, раскладка ленты, видимые
 * листы, якорь позиции, плотность холста. Без DOM и без pdf.js — проверяется vitest'ом напрямую.
 */

import type { PageSize } from './pdfEngine';

/** Режим масштаба: «по ширине», «страница целиком» или число (1 = 100 %). Его и запоминает место. */
export type ZoomMode = 'width' | 'page' | number;

/** Пункты меню масштаба после двух режимов. */
export const ZOOM_PRESETS = [0.5, 0.75, 1, 1.5, 2] as const;

/** Ступени `−` / `+` и Ctrl±; границы масштаба — крайние ступени. */
export const ZOOM_LADDER = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4] as const;
export const MIN_SCALE = ZOOM_LADDER[0];
export const MAX_SCALE = ZOOM_LADDER[ZOOM_LADDER.length - 1];

/** Отступ холста вокруг листов и зазор между листами, CSS-пиксели. */
export const CANVAS_PADDING = 10;
export const PAGE_GAP = 8;

const EPS = 1e-6;

export const clampScale = (scale: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));

/** Следующая ступень крупнее текущего масштаба (а не «текущий × шаг»: «По ширине» бывает 0,83). */
export function zoomIn(scale: number): number {
  return ZOOM_LADDER.find(step => step > scale + EPS) ?? MAX_SCALE;
}

export function zoomOut(scale: number): number {
  return [...ZOOM_LADDER].reverse().find(step => step < scale - EPS) ?? MIN_SCALE;
}

/** Лист после поворота показа: на 90° и 270° ширина и высота меняются местами. */
export function rotated(size: PageSize, rotation: number): PageSize {
  return rotation % 180 === 0 ? size : { width: size.height, height: size.width };
}

/** Число масштаба для режима. `page` — размер листа при 100 %, `box` — видимая область холста. */
export function resolveScale(mode: ZoomMode, page: PageSize, rotation: number, box: { width: number; height: number }): number {
  if (typeof mode === 'number') return clampScale(mode);
  const { width, height } = rotated(page, rotation);
  const freeW = Math.max(1, box.width - 2 * CANVAS_PADDING);
  const freeH = Math.max(1, box.height - 2 * CANVAS_PADDING);
  const byWidth = freeW / width;
  return clampScale(mode === 'width' ? byWidth : Math.min(byWidth, freeH / height));
}

/** Подпись чипа масштаба. */
export function zoomLabel(mode: ZoomMode): string {
  if (mode === 'width') return 'По ширине';
  if (mode === 'page') return 'Страница целиком';
  return `${Math.round(mode * 100)} %`;
}

export interface Layout {
  /** Верх каждого листа от начала ленты. */
  tops: number[];
  widths: number[];
  heights: number[];
  /** Высота ленты целиком. */
  total: number;
  /** Ширина самого широкого листа с отступами — по ней лента прокручивается вбок. */
  width: number;
}

/** Раскладка ленты: контуры всех листов нужного размера ещё до отрисовки — лента не прыгает. */
export function layout(sizes: readonly PageSize[], scale: number, rotation: number): Layout {
  const tops: number[] = [], widths: number[] = [], heights: number[] = [];
  let y = CANVAS_PADDING, widest = 0;
  for (const size of sizes) {
    const { width, height } = rotated(size, rotation);
    tops.push(y);
    widths.push(width * scale);
    heights.push(height * scale);
    widest = Math.max(widest, width * scale);
    y += height * scale + PAGE_GAP;
  }
  return { tops, widths, heights, total: sizes.length ? y - PAGE_GAP + CANVAS_PADDING : 0, width: widest + 2 * CANVAS_PADDING };
}

/** Индекс последнего листа, чей верх не ниже `y` (бинарный поиск: листов бывает 200 и больше). */
function pageAt(tops: readonly number[], y: number): number {
  let lo = 0, hi = tops.length - 1, found = 0;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (tops[mid] <= y) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

/**
 * Листы, которые надо держать нарисованными: видимые и по `margin` с каждой стороны. Номера с 1,
 * границы включительно; пустая лента — `[0, -1]`.
 */
export function visibleRange(l: Layout, scrollTop: number, viewport: number, margin = 2): [number, number] {
  const n = l.tops.length;
  if (!n) return [0, -1];
  const first = pageAt(l.tops, scrollTop);
  const last = pageAt(l.tops, scrollTop + viewport);
  // Лист, чей верх выше видимой области, мог уже кончиться — тогда он не виден.
  const firstVisible = l.tops[first] + l.heights[first] < scrollTop && first < n - 1 ? first + 1 : first;
  return [Math.max(1, firstVisible + 1 - margin), Math.min(n, last + 1 + margin)];
}

/** Номер «текущей» страницы для панели: лист, занимающий верхнюю треть видимой области. */
export function currentPage(l: Layout, scrollTop: number, viewport: number): number {
  if (!l.tops.length) return 1;
  return pageAt(l.tops, scrollTop + viewport / 3) + 1;
}

/** Где начать прокрутку, чтобы лист `page` стоял вверху. */
export function scrollTopOf(l: Layout, page: number): number {
  const i = Math.min(Math.max(page, 1), l.tops.length) - 1;
  return i < 0 ? 0 : Math.max(0, l.tops[i] - CANVAS_PADDING);
}

/** Якорь позиции: лист и доля его высоты у верха видимой области. Переживает смену масштаба. */
export interface Anchor { page: number; fraction: number }

export function anchorOf(l: Layout, scrollTop: number): Anchor {
  if (!l.tops.length) return { page: 1, fraction: 0 };
  const i = pageAt(l.tops, scrollTop);
  const fraction = l.heights[i] ? (scrollTop - l.tops[i]) / l.heights[i] : 0;
  return { page: i + 1, fraction: Math.min(1, Math.max(0, fraction)) };
}

export function scrollTopFor(l: Layout, anchor: Anchor): number {
  const i = Math.min(Math.max(anchor.page, 1), l.tops.length) - 1;
  return i < 0 ? 0 : Math.max(0, l.tops[i] + anchor.fraction * l.heights[i]);
}

/**
 * Потолок пикселей холста: iOS роняет вкладку на холсте больше 16 Мп (скан 300 dpi × 200 % × DPR).
 * Остальным — вдвое больше.
 */
export function maxCanvasPixels(isIOS: boolean): number {
  return isIOS ? 2 ** 24 : 2 ** 25;
}

/** Плотность холста листа: DPR, но не выше потолка пикселей; дальше лист растягивает CSS. */
export function pixelRatio(cssWidth: number, cssHeight: number, dpr: number, maxPixels: number): number {
  const area = Math.max(1, cssWidth * cssHeight);
  return Math.max(0.1, Math.min(dpr || 1, Math.sqrt(maxPixels / area)));
}

/** Номер из поля страницы: число в пределах документа, иначе `null` (поле вернёт текущий). */
export function normalizePage(input: string, numPages: number): number | null {
  const text = input.trim();
  if (!/^\d+$/.test(text)) return null;
  const n = Number(text);
  return n >= 1 && n <= numPages ? n : null;
}

/** Поворот по часовой на 90°. */
export const rotateClockwise = (rotation: number) => (rotation + 90) % 360;

/** Режим масштаба из хранилища места; чужое значение — «по ширине». */
export function parseZoomMode(raw: string | null): ZoomMode {
  if (raw === 'width' || raw === 'page') return raw;
  const n = Number(raw);
  return raw && Number.isFinite(n) && n >= MIN_SCALE && n <= MAX_SCALE ? n : 'width';
}
