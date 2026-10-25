import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type RefObject } from 'react';
import { parseZoomMode, zoomIn, zoomOut, type ZoomMode } from './viewerState';

/**
 * Ввод и масштаб просмотрщика, общие для ленты PDF и картинки (issue #1490, #1491): запомненный режим
 * масштаба, размер области, Ctrl+P, Ctrl± и Ctrl+колесо над листом. Без pdf.js — режим картинки
 * движок не грузит ([UI-24]).
 */

const ZOOM_KEY = (place: string) => `bhs.viewer.zoom.${place}`;

function readZoom(place?: string): ZoomMode {
  if (!place) return 'width';
  try { return parseZoomMode(localStorage.getItem(ZOOM_KEY(place))); } catch { return 'width'; }
}

function writeZoom(place: string | undefined, mode: ZoomMode): void {
  if (!place) return;
  // Режим масштаба — удобство зрителя: недоступное хранилище показу не мешает.
  try { localStorage.setItem(ZOOM_KEY(place), String(mode)); } catch { /* приватный режим */ }
}

/** Подпись режима для озвучки после смены масштаба. */
const zoomAnnouncement = (mode: ZoomMode) =>
  typeof mode === 'number' ? `Масштаб ${Math.round(mode * 100)} %` : `Масштаб: ${mode === 'width' ? 'по ширине' : 'страница целиком'}`;

/**
 * Режим масштаба места: читается из запомненного, смена запоминается и озвучивается. `reset` —
 * вернуть запомненный (новый документ); зовётся прямо при отрисовке, как и остальной сброс показа.
 */
export function useZoomMode(rememberZoomAs: string | undefined) {
  const [mode, setModeState] = useState<ZoomMode>(() => readZoom(rememberZoomAs));
  const [announce, setAnnounce] = useState('');

  const setMode = useCallback((next: ZoomMode) => {
    setModeState(next);
    writeZoom(rememberZoomAs, next);
    setAnnounce(zoomAnnouncement(next));
  }, [rememberZoomAs]);

  const reset = () => setModeState(readZoom(rememberZoomAs));

  return { mode, setMode, reset, announce, setAnnounce };
}

/** Режим после ступени масштаба от текущего `scale`: «по ширине», крупнее или мельче. */
export const zoomTarget = (dir: ZoomDir, scale: number): ZoomMode =>
  dir === 'fit' ? 'width' : dir === 'in' ? zoomIn(scale) : zoomOut(scale);

/** Размер области прокрутки — замером при появлении и при каждой смене размера. */
export function useBoxSize(scroller: RefObject<HTMLElement | null>) {
  const [box, setBox] = useState({ width: 0, height: 0 });
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
  }, [scroller]);
  return box;
}

export const isEditable = (t: EventTarget | null) =>
  t instanceof HTMLElement && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));

export type ZoomDir = 'in' | 'out' | 'fit';

/** Клавиша масштаба: по коду клавиши, а не по символу — в русской раскладке Ctrl+P это «з». */
function zoomKey(e: { code: string; key: string }): ZoomDir | null {
  if (e.code === 'Equal' || e.code === 'NumpadAdd' || e.key === '+' || e.key === '=') return 'in';
  if (e.code === 'Minus' || e.code === 'NumpadSubtract' || e.key === '-') return 'out';
  if (e.code === 'Digit0' || e.code === 'Numpad0' || e.key === '0') return 'fit';
  return null;
}

/** Сколько «щелчков» колеса с Ctrl копится на одну ступень: тачпад шлёт их десятками. */
const WHEEL_STEP = 50;

/**
 * Клавиша внутри просмотрщика: Ctrl+P печатает файл, Ctrl± масштабирует лист. `true` — клавиша
 * обработана, дальше её разбирать не надо.
 */
export function handleViewerKey(e: KeyboardEvent<HTMLElement>, act: { print: () => void; zoom: (dir: ZoomDir) => void; ready: boolean }): boolean {
  const mod = e.ctrlKey || e.metaKey;
  if (mod && !e.altKey && (e.code === 'KeyP' || e.key === 'p' || e.key === 'P')) {
    e.preventDefault();
    act.print();
    return true;
  }
  const z = mod && !e.altKey ? zoomKey(e) : null;
  if (z && act.ready) { e.preventDefault(); act.zoom(z); return true; }
  return false;
}

/**
 * Масштаб снаружи фокуса: указатель над листом, фокус где-то ещё — Ctrl± масштабирует лист, а не
 * страницу браузера (поле ввода в фокусе — нет: человек набирает там, а не смотрит). Ctrl+колесо над
 * листом — тоже масштаб листа. Возвращает обработчики наведения для корня просмотрщика.
 */
export function useZoomInput(root: RefObject<HTMLElement | null>, scroller: RefObject<HTMLElement | null>, zoom: (dir: ZoomDir) => void, ready: boolean) {
  const hovered = useRef(false);
  const latest = useRef({ zoom, ready });
  useEffect(() => { latest.current = { zoom, ready }; });
  useEffect(() => {
    const onWindowKey = (e: globalThis.KeyboardEvent) => {
      if (!hovered.current || !latest.current.ready || !(e.ctrlKey || e.metaKey) || e.altKey) return;
      if (root.current?.contains(document.activeElement) || isEditable(document.activeElement)) return;
      const z = zoomKey(e);
      if (z) { e.preventDefault(); latest.current.zoom(z); }
    };
    window.addEventListener('keydown', onWindowKey);
    return () => window.removeEventListener('keydown', onWindowKey);
  }, [root]);

  // Обработчик не пассивный: иначе preventDefault не отменит масштаб страницы браузера.
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    let acc = 0;
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey || !latest.current.ready) return;
      e.preventDefault();
      acc += e.deltaY;
      if (Math.abs(acc) < WHEEL_STEP) return;
      latest.current.zoom(acc < 0 ? 'in' : 'out');
      acc = 0;
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [scroller]);

  return {
    onPointerEnter: () => { hovered.current = true; },
    onPointerLeave: () => { hovered.current = false; },
  };
}
