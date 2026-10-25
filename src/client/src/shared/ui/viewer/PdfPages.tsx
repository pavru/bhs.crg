import { useEffect, useRef, useState } from 'react';
import { RotateCw } from 'lucide-react';
import { Button } from '../Button';
import type { PdfDocument, PageTask } from './pdfEngine';
import { CANVAS_PADDING, maxCanvasPixels, pixelRatio, type Layout } from './viewerState';
import './textLayer.css';

/**
 * Лента листов (issue #1490, [UI-12], [UI-16], [UI-18]). Контуры всех листов стоят сразу, нужного
 * размера; рисуются только листы из `range` (видимые ±2). Ушедший лист отдаёт холст и ресурсы движка.
 *
 * Лист и его контур белые в любой теме: печати, подписи и цвета счёта — часть документа, и белый лист
 * не должен вспыхивать на тёмном холсте при отрисовке. Край — тень, в тёмной теме — рамка токеном.
 */

/** Перерисовка после смены масштаба — с задержкой: до неё лист растягивает CSS. */
const RERENDER_MS = 150;

const isIOS = typeof navigator !== 'undefined'
  && (/iP(hone|ad|od)/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1));

export function PdfPages({ doc, layout, scale, rotation, range }: {
  doc: PdfDocument; layout: Layout; scale: number; rotation: number; range: [number, number];
}) {
  return (
    <div className="relative" style={{ height: layout.total, minWidth: layout.width }}>
      {layout.tops.map((top, i) => {
        const n = i + 1;
        return (
          <div key={n} role="group" aria-label={`Страница ${n} из ${layout.tops.length}`} data-page={n}
            className="absolute bg-white overflow-hidden shadow-[var(--f-shadow4)] dark:shadow-none dark:outline dark:outline-1 dark:outline-stroke"
            style={{
              top, width: layout.widths[i], height: layout.heights[i],
              left: `max(${CANVAS_PADDING}px, calc(50% - ${layout.widths[i] / 2}px))`,
            }}>
            {n >= range[0] && n <= range[1] && (
              <PdfPage doc={doc} n={n} scale={scale} rotation={rotation}
                baseWidth={layout.widths[i] / scale} baseHeight={layout.heights[i] / scale} />
            )}
          </div>
        );
      })}
    </div>
  );
}

/** `baseWidth` / `baseHeight` — лист при 100 % с поворотом показа: от масштаба не зависят. */
function PdfPage({ doc, n, scale, rotation, baseWidth, baseHeight }: {
  doc: PdfDocument; n: number; scale: number; rotation: number; baseWidth: number; baseHeight: number;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
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

  useEffect(() => {
    const el = canvas.current;
    if (!el) return;
    const ratio = pixelRatio(baseWidth * drawnScale, baseHeight * drawnScale, window.devicePixelRatio || 1, maxCanvasPixels(isIOS));
    const view = { scale: drawnScale, rotation };
    const tasks: PageTask[] = [];
    let alive = true;
    const draw = doc.render(n, el, { ...view, pixelRatio: ratio });
    tasks.push(draw);
    draw.promise.then(() => {
      if (!alive) return;
      setFailed(false);
      if (text.current) tasks.push(doc.textLayer(n, text.current, view));
    }, () => { if (alive) setFailed(true); });
    return () => {
      alive = false;
      tasks.forEach(t => t.cancel());
    };
  }, [doc, n, drawnScale, rotation, attempt, baseWidth, baseHeight]);

  // Ушёл из видимой области: холст отдаёт память (Safari без обнуления размеров её держит), движок —
  // ресурсы листа.
  useEffect(() => {
    const el = canvas.current;
    return () => {
      if (el) { el.width = 0; el.height = 0; }
      doc.release(n);
    };
  }, [doc, n]);

  const k = scale / drawnScale;
  return (
    <>
      <canvas ref={canvas} aria-hidden className="absolute inset-0 w-full h-full" />
      <div className="absolute left-0 top-0 origin-top-left" style={{ transform: k === 1 ? undefined : `scale(${k})` }}>
        <div ref={text} className="bhs-text-layer" />
      </div>
      {failed && (
        // Отказ листа — на поверхности токеном, а не на белом: в тёмной теме кнопка на белом не читалась бы.
        <div className="absolute inset-0 flex flex-col items-center justify-start gap-2 pt-12 text-center text-xs text-fg2 bg-surface">
          <span>Страница {n} не отрисовалась.</span>
          <Button size="sm" variant="outlined" icon={<RotateCw size={13} />} onClick={() => setAttempt(a => a + 1)}>
            Повторить
          </Button>
        </div>
      )}
    </>
  );
}
