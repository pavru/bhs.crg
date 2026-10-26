import type { PageSize, PageTask, PageView, PdfDocument } from './pdfEngine';
import {
  anchorOf, layout, maxCanvasPixels, pixelRatio, resolveScale, scrollTopFor, visibleRange, type Layout, type ZoomMode,
} from './viewerState';

/**
 * Подмена документа тем же ключом без пустого кадра (issue #1493, [UI-25]). Новые байты того же
 * документа — перегенерированный предпросмотр — не гасят ленту: пока новый вариант разбирается и его
 * видимые листы рисуются в холсты вне экрана, на экране прежний. Готовые холсты лист забирает в момент
 * подмены (`adopt`) — узлом, без копии, — и новый вид встаёт одним кадром.
 *
 * Якорь здесь снимается только затем, чтобы знать, КАКИЕ листы рисовать заранее. Позицию после
 * подмены ставит сам просмотрщик — якорем, снятым в момент подмены: прокрутили во время сборки —
 * останется то место, где человек был в тот момент. Листы, до которых он за это время доехал, не
 * готовы и рисуются обычным порядком — тоже вне экрана, поверх прежней картинки.
 */

/** Листы нового варианта, нарисованные вне экрана, и вид, в котором они нарисованы. */
export interface Prepared {
  doc: PdfDocument;
  view: PageView;
  /** Лист забирает свой холст — и здесь его больше нет. */
  canvases: Map<number, HTMLCanvasElement>;
}

export interface Preparing { promise: Promise<Prepared>; cancel(): void }

/** Что показано сейчас: по этому выбираются листы нового варианта, которые рисовать заранее. */
export interface Shown {
  layout: Layout;
  scrollTop: number;
  mode: ZoomMode;
  rotation: number;
  box: { width: number; height: number };
}

const isIOS = typeof navigator !== 'undefined'
  && (/iP(hone|ad|od)/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1));

/** Плотность холста листа этого размера на этом экране. */
export function canvasRatio(cssWidth: number, cssHeight: number): number {
  return pixelRatio(cssWidth, cssHeight, window.devicePixelRatio || 1, maxCanvasPixels(isIOS));
}

/** Масштаб ленты; пока область не измерена — 100 %. */
export function shownScale(mode: ZoomMode, sizes: readonly PageSize[], rotation: number, box: Shown['box']): number {
  return box.width ? resolveScale(mode, sizes, rotation, box) : 1;
}

/**
 * Рисует вне экрана листы нового варианта, видимые с той же позиции. Обещание выполняется всегда —
 * и при отказе листа (он нарисуется на месте и скажет об отказе сам), и при отмене: по нему
 * вызывающий уничтожает документ, ставший ненужным.
 */
export function prepare(doc: PdfDocument, sizes: readonly PageSize[], shown: Shown): Preparing {
  const view = { scale: shownScale(shown.mode, sizes, shown.rotation, shown.box), rotation: shown.rotation };
  const next = layout(sizes, view.scale, view.rotation);
  const top = scrollTopFor(next, anchorOf(shown.layout, shown.scrollTop));
  const [first, last] = visibleRange(next, top, shown.box.height, 0);
  const canvases = new Map<number, HTMLCanvasElement>();
  const tasks: PageTask[] = [];
  for (let n = first; n <= last; n++) {
    const canvas = document.createElement('canvas');
    const i = n - 1;
    const task = doc.render(n, canvas, { ...view, pixelRatio: canvasRatio(next.widths[i], next.heights[i]) });
    tasks.push(task);
    canvases.set(n, canvas);
    task.promise.catch(() => { freeCanvas(canvas); canvases.delete(n); });
  }
  // Отмена завершает подготовку сразу, не дожидаясь, чем ответят отменённые листы.
  let cancelled = false, stop = () => {};
  const stopped = new Promise<void>(resolve => { stop = resolve; });
  const promise = Promise.race([Promise.allSettled(tasks.map(t => t.promise)), stopped]).then(() => {
    const prepared = { doc, view, canvases };
    if (cancelled) discard(prepared);
    return prepared;
  });
  return { promise, cancel() { cancelled = true; tasks.forEach(t => t.cancel()); stop(); } };
}

/**
 * Холст листа, нарисованный заранее этим документом в этом же виде, — лист ставит его к себе узлом, без
 * копии. Нет такого (вид сменился, пока шла подготовка, лист не из подготовленных, его отрисовка
 * отказала) — `null`, и лист рисует сам: тоже вне экрана, прежняя картинка держится до готовой.
 */
export function adopt(prepared: Prepared | null, doc: PdfDocument, n: number, view: PageView): HTMLCanvasElement | null {
  if (!prepared || prepared.doc !== doc || prepared.view.scale !== view.scale || prepared.view.rotation !== view.rotation) return null;
  const from = prepared.canvases.get(n);
  if (!from) return null;
  prepared.canvases.delete(n);
  return from;
}

/** Холсты, которые никто не забрал, отдают память. */
export function discard(prepared: Prepared): void {
  prepared.canvases.forEach(freeCanvas);
  prepared.canvases.clear();
}

/** Холст отдаёт память: Safari без обнуления размеров её держит. */
export function freeCanvas(canvas: HTMLCanvasElement): void {
  canvas.width = 0;
  canvas.height = 0;
}
