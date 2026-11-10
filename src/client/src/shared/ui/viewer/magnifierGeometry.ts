/**
 * Чистая геометрия лупы просмотрщика (issue #1871, [UI-92], [UI-93]; макет А, выбор владельца 11.11.2026).
 * Без DOM и без pdf.js — проверяется vitest'ом напрямую.
 *
 * Системы координат:
 * - **область** — видимая часть прокрутки, CSS-пиксели от её левого верхнего угла: в ней указатель, листы
 *   и окно лупы;
 * - **лист** — показанный лист (масштаб и поворот показа), CSS-пиксели от его угла;
 * - **увеличенный лист** — тот же лист в масштабе лупы: лист × `k`, где `k` = масштаб лупы / масштаб листа.
 *   В ней движок рисует кусок (`renderArea` берёт область в виде листа в масштабе лупы).
 *
 * Правило окна — «точка листа под указателем показана в окне под указателем»: у края окно прижимается, а
 * содержимое не прыгает.
 */

export interface Point { x: number; y: number }
export interface Size { width: number; height: number }
export interface Rect extends Point, Size {}

/** Масштаб листа, с которого лупа недоступна: кратность там ×1,33 и меньше. */
export const LOUPE_OFF_FROM = 3;
export const LOUPE_MIN = 2;
export const LOUPE_MAX = 4;

/** Окно лупы: прямоугольник 320 × 160; в области уже 520 — полоса по ширине области высотой 140. */
export const WINDOW = { width: 320, height: 160 } as const;
export const STRIP_BELOW = 520;
export const STRIP_HEIGHT = 140;
/** Отступ окна от краёв области. */
export const EDGE = 8;
/** Дальше этого от листа окна нет; ближе — есть, и на зазоре 8 между листами оно не мигает. */
export const NEAR = 16;

const EPS = 1e-6;

export const loupeAvailable = (sheetScale: number) => sheetScale < LOUPE_OFF_FROM - EPS;

/** Масштаб лупы: max(200 %, лист × 2), не больше 400 %. */
export const loupeScale = (sheetScale: number) => Math.min(LOUPE_MAX, Math.max(LOUPE_MIN, 2 * sheetScale));

const clamp = (x: number, lo: number, hi: number) => (hi < lo ? (lo + hi) / 2 : Math.max(lo, Math.min(hi, x)));

/**
 * Окно лупы в области — у указателя, целиком внутри области. Область ниже окна с отступами (панель
 * в 150 px) окно сжимает по высоте: торчащее за край окно стоит в прокрутке и растило бы её — прокрутка
 * вниз сдвигала бы окно и снова удлиняла область.
 */
export function loupeWindow(area: Size, pointer: Point): Rect {
  const strip = area.width < STRIP_BELOW;
  const room = { width: Math.max(0, area.width - 2 * EDGE), height: Math.max(0, area.height - 2 * EDGE) };
  const width = strip ? room.width : WINDOW.width; // шире 520 окно 320 входит всегда
  const height = Math.min(strip ? STRIP_HEIGHT : WINDOW.height, room.height);
  return {
    x: clamp(pointer.x - width / 2, EDGE, area.width - width - EDGE),
    y: clamp(pointer.y - height / 2, EDGE, area.height - height - EDGE),
    width, height,
  };
}

/** Расстояние от точки до прямоугольника; внутри — 0. */
function distance(r: Rect, p: Point): number {
  const dx = Math.max(r.x - p.x, 0, p.x - (r.x + r.width));
  const dy = Math.max(r.y - p.y, 0, p.y - (r.y + r.height));
  return Math.hypot(dx, dy);
}

/** Лист, к которому относится указатель: под ним или ближайший не дальше `NEAR`. Нет такого — `-1`. */
export function sheetAt(sheets: readonly Rect[], pointer: Point): number {
  let best = -1, bestD = Infinity;
  sheets.forEach((r, i) => {
    const d = distance(r, pointer);
    if (d < bestD) { best = i; bestD = d; }
  });
  return bestD <= NEAR ? best : -1;
}

/**
 * Что показывает окно: `origin` — точка увеличенного листа в левом верхнем углу окна, `k` — во сколько раз
 * лупа крупнее листа. `sheet` — лист в области, `win` — окно в области.
 */
export function loupeView(sheet: Rect, win: Rect, pointer: Point, k: number): { origin: Point; k: number } {
  return {
    origin: {
      x: (pointer.x - sheet.x) * k - (pointer.x - win.x),
      y: (pointer.y - sheet.y) * k - (pointer.y - win.y),
    },
    k,
  };
}

/** Видимая в окне часть увеличенного листа (`sheet` — размер листа в масштабе листа); пусто — `null`. */
export function visiblePart(sheet: Size, win: Size, origin: Point, k: number): Rect | null {
  return intersect({ x: origin.x, y: origin.y, width: win.width, height: win.height }, { x: 0, y: 0, width: sheet.width * k, height: sheet.height * k });
}

function intersect(a: Rect, b: Rect): Rect | null {
  const x = Math.max(a.x, b.x), y = Math.max(a.y, b.y);
  const right = Math.min(a.x + a.width, b.x + b.width), bottom = Math.min(a.y + a.height, b.y + b.height);
  return right > x && bottom > y ? { x, y, width: right - x, height: bottom - y } : null;
}

/**
 * Кусок для движка — 3 × 3 окна вокруг видимого, в пределах увеличенного листа: пока указатель ходит
 * внутри него, движок заново не рисует ([UI-93]).
 */
export function chunkFor(sheet: Size, win: Size, origin: Point, k: number): Rect | null {
  return intersect(
    { x: origin.x - win.width, y: origin.y - win.height, width: 3 * win.width, height: 3 * win.height },
    { x: 0, y: 0, width: sheet.width * k, height: sheet.height * k },
  );
}

/** Покрывает ли нарисованный кусок всё, что видно в окне. */
export function covers(chunk: Rect, visible: Rect): boolean {
  return visible.x >= chunk.x - EPS && visible.y >= chunk.y - EPS
    && visible.x + visible.width <= chunk.x + chunk.width + EPS
    && visible.y + visible.height <= chunk.y + chunk.height + EPS;
}

/** Аффинное преобразование `[a, b, c, d, e, f]` — как у `CanvasRenderingContext2D.setTransform`. */
export type Matrix = [number, number, number, number, number, number];

export const apply = ([a, b, c, d, e, f]: Matrix, p: Point): Point => ({ x: a * p.x + c * p.y + e, y: b * p.x + d * p.y + f });

/**
 * Картинка в лист: пиксель исходного изображения → точка показанного листа при масштабе `scale` и
 * повороте показа `rotation` по часовой. Так `ImageView` и ставит картинку: повёрнута вокруг центра
 * листа повёрнутого размера ([UI-24]).
 */
export function imageToSheet(natural: Size, scale: number, rotation: number): Matrix {
  const w = natural.width * scale, h = natural.height * scale;
  const turned = rotation % 180 !== 0;
  const W = turned ? h : w, H = turned ? w : h;
  switch (((rotation % 360) + 360) % 360) {
    case 90: return [0, scale, -scale, 0, W, 0];
    case 180: return [-scale, 0, 0, -scale, W, H];
    case 270: return [0, -scale, scale, 0, 0, H];
    default: return [scale, 0, 0, scale, 0, 0];
  }
}
