import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { openPdf, ViewerFailure, type PageSize, type PdfDocument } from './pdfEngine';
import { printPdf } from './printPdf';
import { PdfPages } from './PdfPages';
import { discard, prepare, shownScale, type Prepared, type Preparing, type Shown } from './swapDocument';
import { NARROW_PX, ViewerToolbar } from './ViewerToolbar';
import { ViewerMessage, type ViewerState } from './ViewerMessage';
import {
  anchorOf, currentPage, layout, MAX_SCALE, MIN_SCALE, rotateClockwise, scrollTopFor,
  scrollTopOf, visibleRange, type Layout,
} from './viewerState';
import { handleViewerKey, isEditable, useBoxSize, useZoomInput, useZoomMode, zoomTarget, type ZoomDir } from './viewerInput';
import type { PdfViewerProps } from './PdfViewer';

/**
 * Ядро просмотрщика PDF — ленивый кусок, его грузит `PdfViewer` (issue #1490). Здесь: загрузка
 * документа, ключ документа, масштаб и поворот с якорем позиции, клавиши, Ctrl+P, панель снизу.
 *
 * - **Ключ документа** новый — показ с первой страницы, масштаб — запомненный режим места, поворот
 *   сброшен. Тот же ключ с новыми байтами — страница, масштаб и поворот остаются, лист перерисовывается
 *   на месте: пока новые байты разбираются, виден прежний документ ([UI-25], `swapDocument.ts`). Его
 *   видимые листы рисуются вне экрана, и подмена встаёт одним кадром. Загрузка, ставшая устаревшей
 *   (пришли следующие байты), гасится и поверх более новой не ложится.
 * - **Документ уничтожается ровно раз**: при замене другим и при уходе со страницы.
 */

export default function PdfViewerImpl({ source, documentKey, title, actions, rememberZoomAs }: PdfViewerProps) {
  const [shownKey, setShownKey] = useState(documentKey);
  const { mode, setMode, reset: resetZoom, announce, setAnnounce } = useZoomMode(rememberZoomAs);
  const [rotation, setRotation] = useState(0);
  const [state, setState] = useState<ViewerState | 'ready'>('loading');
  const [doc, setDoc] = useState<PdfDocument | null>(null);
  const [sizes, setSizes] = useState<PageSize[] | null>(null);
  const [prepared, setPrepared] = useState<Prepared | null>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [page, setPage] = useState(1);
  const scroller = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const box = useBoxSize(scroller);

  // Новый документ — сброс показа прямо при отрисовке, а не эффектом: иначе мелькнул бы прежний.
  if (documentKey !== shownKey) {
    setShownKey(documentKey);
    setDoc(null);
    setSizes(null);
    setPrepared(null);
    setState('loading');
    setRotation(0);
    setPage(1);
    resetZoom();
  }

  // Документ уничтожается, когда его сменил другой (или null) и когда просмотрщик убран.
  useEffect(() => () => { void doc?.destroy(); }, [doc]);

  // Листы уже забрали свои холсты (их эффекты — раньше этого): то, что не забрано, отдаёт память.
  useEffect(() => { if (prepared) discard(prepared); }, [prepared]);

  // Что показано сейчас — для подготовки подмены. Ссылкой: загрузка не должна начинаться заново от
  // каждой прокрутки и смены масштаба, а прочесть их ей нужно на момент готовности, а не начала.
  const current = useRef<(() => Shown) | null>(null);

  // Ключ — тоже повод открыть: новый ключ при тех же байтах сбросил показ выше, и без нового открытия
  // просмотрщик так и стоял бы «загружается».
  useEffect(() => {
    if (!source) return;
    let alive = true, resolved = false;
    let preparing: Preparing | null = null;
    const loading = openPdf(source);
    loading.promise.then(d => {
      resolved = true;
      if (!alive) { void d.destroy(); return; }
      // Документ встаёт вместе со своими размерами: новые байты того же ключа с раскладкой прежних
      // рисовали бы чужие контуры и листы, которых в новом документе нет. До тех пор виден прежний.
      d.pageSizes().then(s => {
        if (!alive) { void d.destroy(); return; }
        const show = (p: Prepared | null) => {
          setPrepared(p);
          setDoc(d);
          setSizes(s);
          setState('ready');
        };
        const was = current.current;
        if (!was) { show(null); return; }
        preparing = prepare(d, s, was());
        preparing.promise.then(p => {
          if (alive) { show(p); return; }
          discard(p);
          void d.destroy();
        });
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
      preparing?.cancel();
    };
  }, [source, documentKey]);

  const scale = sizes ? shownScale(mode, sizes, rotation, box) : 1;
  const l = useMemo(() => (sizes ? layout(sizes, scale, rotation) : null), [sizes, scale, rotation]);

  useLayoutEffect(() => {
    current.current = state === 'ready' && doc && l
      ? () => ({ layout: l, scrollTop: scroller.current?.scrollTop ?? 0, mode, rotation, box })
      : null;
  });

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

  const zoom = useCallback((dir: ZoomDir) => setMode(zoomTarget(dir, scale)), [scale, setMode]);

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
    <div ref={root} className="h-full w-full flex flex-col min-h-0 bg-surface-container-low" onKeyDown={onKeyDown} {...hover}>
      <div ref={scroller} role="region" aria-label={title} tabIndex={0} onScroll={onScroll} data-testid="viewer-region"
        className="relative flex-1 min-h-0 overflow-auto bg-surface-container-low focus-visible:outline-2 focus-visible:outline-primary focus-visible:-outline-offset-2">
        {ready
          ? <PdfPages doc={doc} layout={l} scale={scale} rotation={rotation} range={range} prepared={prepared} />
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
