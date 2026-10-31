import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Icon } from '../Icon';
import { RotateRight } from '../icons';
import { Button } from '../Button';
import type { PdfDocument, PageTask } from './pdfEngine';
import { CANVAS_PADDING, type Layout } from './viewerState';
import { adopt, canvasRatio, freeCanvas, type Prepared } from './swapDocument';
import './textLayer.css';
import './sheet.css';

/**
 * Лента листов (issue #1490, [UI-12], [UI-16], [UI-18]). Контуры всех листов стоят сразу, нужного
 * размера; рисуются только листы из `range` (видимые ±2). Ушедший лист отдаёт холст и ресурсы движка.
 *
 * Лист и его контур белые в любой теме: печати, подписи и цвета счёта — часть документа, и белый лист
 * не должен вспыхивать на тёмном холсте при отрисовке. Край — тень, в тёмной теме — рамка токеном.
 *
 * Лист никогда не рисуется в холст, который на экране: каждая отрисовка — в новый холст вне экрана,
 * и он встаёт на место узлом, уже нарисованный. До тех пор виден прежний (растянутый CSS при смене
 * масштаба, прежний вариант при подмене документа, [UI-25]) — движок обнулял бы размеры видимого холста
 * в начале отрисовки, и лист белел бы до её конца. Холст, нарисованный до подмены (`prepared`), лист
 * забирает без отрисовки.
 */

/** Перерисовка после смены масштаба — с задержкой: до неё лист растягивает CSS. */
const RERENDER_MS = 150;

export function PdfPages({ doc, layout, scale, rotation, range, prepared }: {
  doc: PdfDocument; layout: Layout; scale: number; rotation: number; range: [number, number];
  /** Листы этого документа, нарисованные до подмены. */
  prepared: Prepared | null;
}) {
  return (
    <div className="relative" style={{ height: layout.total, minWidth: layout.width }}>
      {layout.tops.map((top, i) => {
        const n = i + 1;
        return (
          <div key={n} role="group" aria-label={`Страница ${n} из ${layout.tops.length}`} data-page={n}
            className="absolute bhs-sheet overflow-hidden"
            style={{
              top, width: layout.widths[i], height: layout.heights[i],
              left: `max(${CANVAS_PADDING}px, calc(50% - ${layout.widths[i] / 2}px))`,
            }}>
            {n >= range[0] && n <= range[1] && (
              <PdfPage doc={doc} n={n} scale={scale} rotation={rotation} prepared={prepared}
                baseWidth={layout.widths[i] / scale} baseHeight={layout.heights[i] / scale} />
            )}
          </div>
        );
      })}
    </div>
  );
}

/** `baseWidth` / `baseHeight` — лист при 100 % с поворотом показа: от масштаба не зависят. */
function PdfPage({ doc, n, scale, rotation, prepared, baseWidth, baseHeight }: {
  doc: PdfDocument; n: number; scale: number; rotation: number; prepared: Prepared | null;
  baseWidth: number; baseHeight: number;
}) {
  // Держатель холста: холст в нём ставит и меняет лист, не React — узел меняется целиком, готовым.
  const holder = useRef<HTMLDivElement>(null);
  const text = useRef<HTMLDivElement>(null);
  // Масштаб, в котором лист нарисован. Пока он отстаёт от показанного, холст и слой текста растянуты.
  const [drawnScale, setDrawnScale] = useState(scale);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (drawnScale === scale) return;
    const t = setTimeout(() => setDrawnScale(scale), RERENDER_MS);
    return () => clearTimeout(t);
  }, [scale, drawnScale]);

  // До отрисовки кадра, а не после: забранный холст обязан встать в том же кадре, что и новая лента.
  useLayoutEffect(() => {
    const box = holder.current;
    if (!box) return;
    const view = { scale: drawnScale, rotation };
    const tasks: PageTask[] = [];
    let alive = true;
    const show = (next: HTMLCanvasElement) => {
      const was = box.firstElementChild as HTMLCanvasElement | null;
      next.className = 'absolute inset-0 w-full h-full';
      box.replaceChildren(next);
      if (was) freeCanvas(was);
      setFailed(false);
      if (!text.current) return;
      const layer = doc.textLayer(n, text.current, view);
      tasks.push(layer);
      // Слой текста отменяется сменой масштаба и уходом листа; лист нарисован и без него.
      layer.promise.catch(() => {});
    };
    const ready = adopt(prepared, doc, n, view);
    if (ready) {
      show(ready);
    } else {
      const next = document.createElement('canvas');
      const draw = doc.render(n, next, { ...view, pixelRatio: canvasRatio(baseWidth * drawnScale, baseHeight * drawnScale) });
      tasks.push(draw);
      draw.promise.then(() => {
        if (alive) show(next); else freeCanvas(next);
      }, () => {
        freeCanvas(next);
        if (alive) setFailed(true);
      });
    }
    return () => {
      alive = false;
      tasks.forEach(t => t.cancel());
    };
  }, [doc, n, drawnScale, rotation, attempt, baseWidth, baseHeight, prepared]);

  // Движок отдаёт ресурсы листа, когда лист ушёл или документ сменился.
  useEffect(() => () => doc.release(n), [doc, n]);

  // Ушёл из видимой области — холст отдаёт память (Safari без обнуления размеров её держит).
  useEffect(() => {
    const box = holder.current;
    return () => { box?.querySelectorAll('canvas').forEach(freeCanvas); };
  }, []);

  const k = scale / drawnScale;
  return (
    <>
      <div ref={holder} aria-hidden className="absolute inset-0" />
      <div className="absolute left-0 top-0 origin-top-left" style={{ transform: k === 1 ? undefined : `scale(${k})` }}>
        <div ref={text} className="bhs-text-layer" />
      </div>
      {failed && (
        // Отказ листа — на поверхности токеном, а не на белом: в тёмной теме кнопка на белом не читалась бы.
        <div className="absolute inset-0 flex flex-col items-center justify-start gap-2 pt-12 text-center text-body-small text-on-surface-variant bg-surface">
          <span>Страница {n} не отрисовалась.</span>
          <Button variant="outlined" icon={<Icon icon={RotateRight} size={18} />} onClick={() => setAttempt(a => a + 1)}>
            Повторить
          </Button>
        </div>
      )}
    </>
  );
}
