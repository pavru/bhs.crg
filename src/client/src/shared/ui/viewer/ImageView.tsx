import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { printPdf } from './printPdf';
import { NARROW_PX, ViewerToolbar } from './ViewerToolbar';
import { ViewerMessage } from './ViewerMessage';
import {
  anchorOf, CANVAS_PADDING, layout, MAX_SCALE, MIN_SCALE, resolveScale, rotateClockwise, scrollTopFor,
  zoomIn, zoomOut, type Layout, type ZoomMode,
} from './viewerState';
import { handleViewerKey, readZoom, useZoomInput, writeZoom, zoomAnnouncement, type ZoomDir } from './viewerInput';
import type { PdfViewerProps } from './PdfViewer';

/**
 * Режим картинки просмотрщика (issue #1491, [UI-24]): фото счёта той же панелью, что PDF, — масштаб и
 * поворот есть, нумерации страниц нет. pdf.js не грузится: картинку браузер рисует сам, `<img>` из
 * объектного адреса (`img-src blob:` в CSP поставки есть).
 *
 * Поворот — `transform` картинки внутри листа повёрнутого размера: на 90° и 270° «по ширине» считается
 * по высоте картинки, и снятый боком счёт встаёт в колонку целиком. Поворот только для показа и
 * сбрасывается с новым ключом документа, как у PDF.
 */

interface Size { width: number; height: number }

export function ImageView({ source, documentKey, title, actions, rememberZoomAs }: Omit<PdfViewerProps, 'source' | 'overlay'> & { source: Blob }) {
  const [shownKey, setShownKey] = useState(documentKey);
  const [mode, setModeState] = useState<ZoomMode>(() => readZoom(rememberZoomAs));
  const [rotation, setRotation] = useState(0);
  const [natural, setNatural] = useState<{ source: Blob; size: Size } | null>(null);
  const [failedOf, setFailedOf] = useState<Blob | null>(null);
  const [box, setBox] = useState({ width: 0, height: 0 });
  const [announce, setAnnounce] = useState('');
  const scroller = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const img = useRef<HTMLImageElement>(null);

  // Новый документ — сброс показа прямо при отрисовке, а не эффектом: иначе мелькнул бы прежний поворот.
  if (documentKey !== shownKey) {
    setShownKey(documentKey);
    setRotation(0);
    setModeState(readZoom(rememberZoomAs));
  }

  // Адрес — свой на каждые байты и отзывается, когда байты сменились или просмотрщик убран. Картинке
  // он ставится прямо, без состояния: адрес, переживший отзыв в состоянии, показал бы пустую рамку.
  useEffect(() => {
    const url = URL.createObjectURL(source);
    if (img.current) img.current.src = url;
    return () => URL.revokeObjectURL(url);
  }, [source]);

  // Размеры и отказ — только своих байтов: у прежних они уже ничего не значат.
  const size = natural?.source === source ? natural.size : null;
  const failed = failedOf === source;

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const measure = () => setBox(b => (b.width === el.clientWidth && b.height === el.clientHeight
      ? b : { width: el.clientWidth, height: el.clientHeight }));
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const pages = useMemo(() => (size ? [size] : null), [size]);
  // Число масштаба области не ждёт; «по ширине» без измеренной области — пока 100 %.
  const scale = pages && (box.width || typeof mode === 'number') ? resolveScale(mode, pages, rotation, box) : 1;
  const l = useMemo(() => (pages ? layout(pages, scale, rotation) : null), [pages, scale, rotation]);

  // Смена масштаба или поворота держит ту же долю картинки вверху — как лента PDF.
  const shown = useRef<Layout | null>(null);
  useLayoutEffect(() => {
    const el = scroller.current;
    const prev = shown.current;
    shown.current = l;
    if (!el || !l) return;
    el.scrollTop = prev ? scrollTopFor(l, anchorOf(prev, el.scrollTop)) : 0;
  }, [l]);

  const setMode = useCallback((next: ZoomMode) => {
    setModeState(next);
    writeZoom(rememberZoomAs, next);
    setAnnounce(zoomAnnouncement(next));
  }, [rememberZoomAs]);

  const zoom = useCallback((dir: ZoomDir) => {
    setMode(dir === 'fit' ? 'width' : dir === 'in' ? zoomIn(scale) : zoomOut(scale));
  }, [scale, setMode]);

  const ready = !failed && !!(size && l);
  const print = useCallback(() => printPdf(source), [source]);
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => { handleViewerKey(e, { print, zoom, ready }); };
  const hover = useZoomInput(root, scroller, zoom, ready);

  // Картинка внутри листа повёрнутого размера: свои ширина и высота, по центру, повёрнута вокруг центра.
  const imgW = size ? size.width * scale : 0;
  const imgH = size ? size.height * scale : 0;
  const sheet = l ? { width: l.widths[0], height: l.heights[0] } : null;

  return (
    <div ref={root} className="h-full w-full flex flex-col min-h-0 bg-base" onKeyDown={onKeyDown} {...hover}>
      <div ref={scroller} role="region" aria-label={title} tabIndex={0} data-testid="viewer-region" data-view="image"
        className="relative flex-1 min-h-0 overflow-auto bg-base focus-visible:outline-2 focus-visible:outline-brand focus-visible:-outline-offset-2">
        {failed && <ViewerMessage state="broken" source={source} actions={actions} />}
        {!failed && !ready && <ViewerMessage state="loading" source={source} />}
        {/* Картинка в DOM всегда: адрес ей ставит эффект, а размеры она сообщает, загрузившись. */}
        <div className={ready ? 'relative' : 'absolute invisible w-0 h-0 overflow-hidden'} style={l ? { height: l.total, minWidth: l.width } : undefined}>
          <div data-page={1} data-testid="viewer-image-sheet"
            className="absolute bg-white overflow-hidden shadow-[var(--f-shadow4)] dark:shadow-none dark:outline dark:outline-1 dark:outline-stroke"
            style={sheet
              ? { top: CANVAS_PADDING, width: sheet.width, height: sheet.height, left: `max(${CANVAS_PADDING}px, calc(50% - ${sheet.width / 2}px))` }
              : undefined}>
            <img ref={img} alt={title} draggable={false}
              onLoad={e => {
                const { naturalWidth: width, naturalHeight: height } = e.currentTarget;
                if (width && height) setNatural({ source, size: { width, height } });
                else setFailedOf(source);
              }}
              onError={() => setFailedOf(source)}
              className="absolute max-w-none select-none"
              style={size && sheet
                ? {
                  width: imgW, height: imgH,
                  left: (sheet.width - imgW) / 2, top: (sheet.height - imgH) / 2,
                  transform: rotation ? `rotate(${rotation}deg)` : undefined,
                }
                : undefined} />
          </div>
        </div>
      </div>
      {ready && (
        <ViewerToolbar numPages={1} page={1} onPage={() => {}} mode={mode} onMode={setMode}
          onZoomIn={() => zoom('in')} onZoomOut={() => zoom('out')}
          canZoomIn={scale < MAX_SCALE} canZoomOut={scale > MIN_SCALE}
          onRotate={() => { setRotation(rotateClockwise); setAnnounce('Картинка повёрнута'); }}
          narrow={box.width > 0 && box.width < NARROW_PX} />
      )}
      <div aria-live="polite" className="sr-only" data-testid="viewer-live">{announce}</div>
    </div>
  );
}
