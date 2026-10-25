import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { openPdf, ViewerFailure, type PageSize, type PdfDocument } from './pdfEngine';
import { printPdf } from './printPdf';
import { PdfPages } from './PdfPages';
import { NARROW_PX, ViewerToolbar } from './ViewerToolbar';
import { ViewerMessage, type ViewerState } from './ViewerMessage';
import {
  anchorOf, currentPage, layout, MAX_SCALE, MIN_SCALE, resolveScale, rotateClockwise, scrollTopFor,
  scrollTopOf, visibleRange, zoomIn, zoomOut, type Layout, type ZoomMode,
} from './viewerState';
import { handleViewerKey, isEditable, readZoom, useZoomInput, writeZoom, zoomAnnouncement, type ZoomDir } from './viewerInput';
import type { PdfViewerProps } from './PdfViewer';

/**
 * Ядро просмотрщика PDF — ленивый кусок, его грузит `PdfViewer` (issue #1490). Здесь: загрузка
 * документа, ключ документа, масштаб и поворот с якорем позиции, клавиши, Ctrl+P, панель снизу.
 *
 * - **Ключ документа** новый — показ с первой страницы, масштаб — запомненный режим места, поворот
 *   сброшен. Тот же ключ с новыми байтами — страница, масштаб и поворот остаются, лист перерисовывается
 *   на месте: пока новые байты разбираются, виден прежний документ.
 * - **Документ уничтожается ровно раз**: при замене другим и при уходе со страницы.
 */

export default function PdfViewerImpl({ source, documentKey, title, actions, rememberZoomAs }: PdfViewerProps) {
  const [shownKey, setShownKey] = useState(documentKey);
  const [mode, setModeState] = useState<ZoomMode>(() => readZoom(rememberZoomAs));
  const [rotation, setRotation] = useState(0);
  const [state, setState] = useState<ViewerState | 'ready'>('loading');
  const [doc, setDoc] = useState<PdfDocument | null>(null);
  const [sizes, setSizes] = useState<PageSize[] | null>(null);
  const [box, setBox] = useState({ width: 0, height: 0 });
  const [scrollTop, setScrollTop] = useState(0);
  const [page, setPage] = useState(1);
  const [announce, setAnnounce] = useState('');
  const scroller = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);

  // Новый документ — сброс показа прямо при отрисовке, а не эффектом: иначе мелькнул бы прежний.
  if (documentKey !== shownKey) {
    setShownKey(documentKey);
    setDoc(null);
    setSizes(null);
    setState('loading');
    setRotation(0);
    setPage(1);
    setModeState(readZoom(rememberZoomAs));
  }

  // Документ уничтожается, когда его сменил другой (или null) и когда просмотрщик убран.
  useEffect(() => () => { void doc?.destroy(); }, [doc]);

  // Ключ — тоже повод открыть: новый ключ при тех же байтах сбросил показ выше, и без нового открытия
  // просмотрщик так и стоял бы «загружается».
  useEffect(() => {
    if (!source) return;
    let alive = true, resolved = false;
    const loading = openPdf(source);
    loading.promise.then(d => {
      resolved = true;
      if (!alive) { void d.destroy(); return; }
      // Документ встаёт вместе со своими размерами: новые байты того же ключа с раскладкой прежних
      // рисовали бы чужие контуры и листы, которых в новом документе нет. До тех пор виден прежний.
      d.pageSizes().then(s => {
        if (!alive) { void d.destroy(); return; }
        setDoc(d);
        setSizes(s);
        setState('ready');
      }, () => {
        void d.destroy();
        if (!alive) return;
        setDoc(null);
        setState('broken');
      });
    }, (e: unknown) => {
      resolved = true;
      if (!alive) return;
      setDoc(null);
      setState(e instanceof ViewerFailure ? e.kind : 'broken');
    });
    return () => {
      alive = false;
      if (!resolved) loading.cancel();
    };
  }, [source, documentKey]);

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

  const scale = sizes && box.width ? resolveScale(mode, sizes, rotation, box) : 1;
  const l = useMemo(() => (sizes ? layout(sizes, scale, rotation) : null), [sizes, scale, rotation]);

  // Смена масштаба, поворота, ширины или байтов того же документа: держим ту же точку листа вверху.
  const shown = useRef<Layout | null>(null);
  useLayoutEffect(() => {
    const el = scroller.current;
    const prev = shown.current;
    shown.current = l;
    if (!el || !l) return;
    el.scrollTop = prev ? scrollTopFor(l, anchorOf(prev, el.scrollTop)) : 0;
    setScrollTop(el.scrollTop);
    // Новые байты того же ключа короче прежних: номер не должен остаться за концом документа.
    setPage(p => Math.min(p, l.tops.length));
  }, [l]);

  const numPages = sizes?.length ?? 0;

  const setMode = useCallback((next: ZoomMode) => {
    setModeState(next);
    writeZoom(rememberZoomAs, next);
    setAnnounce(zoomAnnouncement(next));
  }, [rememberZoomAs]);

  const zoom = useCallback((dir: ZoomDir) => {
    setMode(dir === 'fit' ? 'width' : dir === 'in' ? zoomIn(scale) : zoomOut(scale));
  }, [scale, setMode]);

  const goTo = useCallback((n: number) => {
    const el = scroller.current;
    if (!el || !l) return;
    const target = Math.min(Math.max(n, 1), numPages);
    el.scrollTop = scrollTopOf(l, target);
    setScrollTop(el.scrollTop);
    setPage(target);
    setAnnounce(`Страница ${target} из ${numPages}`);
  }, [l, numPages]);

  const print = useCallback(() => { if (source) printPdf(source); }, [source]);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (handleViewerKey(e, { print, zoom, ready: state === 'ready' })) return;
    const mod = e.ctrlKey || e.metaKey;
    if (!mod && !e.altKey && !e.shiftKey && (e.key === 'Home' || e.key === 'End') && !isEditable(e.target) && numPages) {
      e.preventDefault();
      goTo(e.key === 'Home' ? 1 : numPages);
    }
  };

  const hover = useZoomInput(root, scroller, zoom, state === 'ready');

  const frame = useRef(0);
  const onScroll = () => {
    cancelAnimationFrame(frame.current);
    frame.current = requestAnimationFrame(() => {
      const el = scroller.current;
      if (!el || !l) return;
      setScrollTop(el.scrollTop);
      // При прокрутке номер меняется молча: озвучка — только после явного перехода.
      setPage(currentPage(l, el.scrollTop, el.clientHeight));
    });
  };

  const ready = state === 'ready' && doc && l;
  const range = l ? visibleRange(l, scrollTop, box.height) : ([0, -1] as [number, number]);

  return (
    <div ref={root} className="h-full w-full flex flex-col min-h-0 bg-base" onKeyDown={onKeyDown} {...hover}>
      <div ref={scroller} role="region" aria-label={title} tabIndex={0} onScroll={onScroll} data-testid="viewer-region"
        className="relative flex-1 min-h-0 overflow-auto bg-base focus-visible:outline-2 focus-visible:outline-brand focus-visible:-outline-offset-2">
        {ready
          ? <PdfPages doc={doc} layout={l} scale={scale} rotation={rotation} range={range} />
          : <ViewerMessage state={state === 'ready' ? 'loading' : state} source={source} actions={actions} />}
      </div>
      {ready && (
        <ViewerToolbar numPages={numPages} page={page} onPage={goTo} mode={mode} onMode={setMode}
          onZoomIn={() => zoom('in')} onZoomOut={() => zoom('out')}
          canZoomIn={scale < MAX_SCALE} canZoomOut={scale > MIN_SCALE}
          onRotate={() => { setRotation(rotateClockwise); setAnnounce('Лист повёрнут'); }}
          narrow={box.width > 0 && box.width < NARROW_PX} />
      )}
      <div aria-live="polite" className="sr-only" data-testid="viewer-live">{announce}</div>
    </div>
  );
}
