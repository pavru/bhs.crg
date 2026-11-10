import { useEffect, useRef, type RefObject } from 'react';
import type { PageTask, PdfDocument } from './pdfEngine';
import { canvasRatio, freeCanvas } from './swapDocument';
import {
  chunkFor, covers, imageToSheet, loupeScale, loupeView, loupeWindow, sheetAt, visiblePart,
  type Rect, type Size,
} from './magnifierGeometry';

/**
 * Окно лупы просмотрщика (issue #1871, [UI-92], [UI-93]; макет А, выбор владельца 11.11.2026).
 * Стоит в области прокрутки, пока лупа включена; масштаб и прокрутку листа не трогает.
 *
 * - Окно — прямоугольник 320 × 160 (в области уже 520 — полоса), форма large, обводка `outline`,
 *   возвышение 2; лист в нём белый в любой теме, за краем листа — холст. Скрыто от диктора.
 * - **PDF рисует движок заново** — кусок листа 3 × 3 окна в масштабе лупы (`renderArea`): растянутый
 *   холст листа в узкой панели дал бы пиксели вместо букв. Пока кусок рисуется, окно показывает
 *   растянутый холст листа, а не пустоту. Кусок отдаёт память при уходе указателя с листа, смене
 *   документа, масштаба, поворота и при выключении лупы (окно убирается).
 * - **Картинку** окно рисует само из `<img>`: пиксели исходника уже декодированы, движок не нужен.
 * - Ход указателя — без отрисовки React: окно двигается и перерисовывается напрямую, не чаще кадра.
 *   Касание окна не показывает — лист ведут пальцем ([UI-94]).
 *
 * `data-source` окна — что в нём сейчас: `chunk` (кусок движка), `sheet` (растянутый холст листа,
 * кусок ещё рисуется), `image`. По нему проверяют тесты.
 */

export interface MagnifierProps {
  /** Область прокрутки с листами (`[data-page]`). */
  scroller: RefObject<HTMLElement | null>;
  /** Масштаб листа (1 = 100 %). */
  sheetScale: number;
  rotation: number;
  /** Документ PDF — окно рисует куски движком. */
  doc?: PdfDocument;
  /** Картинка — окно рисует её саму. */
  image?: { el: RefObject<HTMLImageElement | null>; natural: Size };
  /** Показанное устарело: окно приглушено вместе с листом. */
  dimmed?: boolean;
}

interface Chunk { page: number; area: Rect; canvas: HTMLCanvasElement; task: PageTask; ready: boolean }

/** Обводка окна: холст начинается внутри неё. */
const BORDER = 1;

export function Magnifier({ scroller, sheetScale, rotation, doc, image, dimmed }: MagnifierProps) {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  // Последнее положение указателя переживает смену масштаба и документа: окно встаёт на место сразу.
  const pointer = useRef<{ x: number; y: number } | null>(null);
  const imgEl = image?.el;
  // Размер картинки — числами: объект `image` новый на каждую отрисовку места.
  const nw = image?.natural.width ?? 0, nh = image?.natural.height ?? 0;

  // Одним эффектом: новый документ, масштаб или поворот — прежний кусок ничего не значит, всё заново.
  useEffect(() => {
    const el = scroller.current, w = box.current, cv = canvas.current;
    if (!el || !w || !cv) return;
    let chunk: Chunk | null = null;
    let frame = 0;
    let alive = true;
    const natural = imgEl ? { width: nw, height: nh } : null;

    const drop = () => {
      if (!chunk) return;
      chunk.task.cancel();
      freeCanvas(chunk.canvas);
      chunk = null;
    };
    const hide = () => { w.hidden = true; drop(); };
    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(draw);
    };

    function draw() {
      const p = pointer.current;
      if (!alive || !el || !w || !cv) return;
      if (!p) { hide(); return; }
      const r = el.getBoundingClientRect();
      const at = { x: p.x - r.left, y: p.y - r.top };
      const area = { width: el.clientWidth, height: el.clientHeight };
      if (at.x < 0 || at.y < 0 || at.x > area.width || at.y > area.height) { hide(); return; }
      const pages = Array.from(el.querySelectorAll<HTMLElement>('[data-page]'));
      const rects = pages.map(pg => {
        const b = pg.getBoundingClientRect();
        return { x: b.left - r.left, y: b.top - r.top, width: b.width, height: b.height };
      });
      const i = sheetAt(rects, at);
      if (i < 0) { hide(); return; }
      const sheet = rects[i], page = pages[i];
      const win = loupeWindow(area, at);
      const k = loupeScale(sheetScale) / sheetScale;
      const { origin } = loupeView(sheet, win, at, k);

      w.hidden = false;
      w.style.width = `${win.width}px`;
      w.style.height = `${win.height}px`;
      w.style.transform = `translate(${el.scrollLeft + win.x}px, ${el.scrollTop + win.y}px)`;

      const dpr = window.devicePixelRatio || 1;
      const cw = Math.round(Math.max(0, win.width - 2 * BORDER) * dpr);
      const ch = Math.round(Math.max(0, win.height - 2 * BORDER) * dpr);
      if (cv.width !== cw) cv.width = cw;
      if (cv.height !== ch) cv.height = ch;

      const sheetSize = { width: sheet.width, height: sheet.height };
      const visible = visiblePart(sheetSize, win, origin, k);
      const ctx = cv.getContext('2d');
      // Холст окна начинается внутри обводки: начало окна — за пределами холста на её толщину.
      ctx?.setTransform(dpr, 0, 0, dpr, -BORDER * dpr, -BORDER * dpr);
      ctx?.clearRect(0, 0, win.width, win.height);
      if (ctx && visible) {
        // Бумага — цвет самого листа (`sheet.css`: белый в любой теме), а не свой литерал.
        ctx.fillStyle = getComputedStyle(page).backgroundColor;
        ctx.fillRect(visible.x - origin.x, visible.y - origin.y, visible.width, visible.height);
      }

      if (natural) {
        w.dataset.source = 'image';
        const pic = imgEl?.current;
        if (ctx && pic) {
          ctx.transform(k, 0, 0, k, -origin.x, -origin.y);
          ctx.transform(...imageToSheet(natural, sheetScale, rotation));
          ctx.drawImage(pic, 0, 0);
        }
        return;
      }
      if (!doc || !visible) { w.dataset.source = 'sheet'; return; }

      const n = Number(page.dataset.page);
      if (chunk && chunk.page === n && chunk.ready && covers(chunk.area, visible)) {
        w.dataset.source = 'chunk';
        ctx?.drawImage(chunk.canvas, chunk.area.x - origin.x, chunk.area.y - origin.y, chunk.area.width, chunk.area.height);
        return;
      }
      // Куска нет или указатель вышел за него: до готовности — растянутый холст листа.
      w.dataset.source = 'sheet';
      const shown = page.querySelector('canvas');
      if (ctx && shown && shown.width) ctx.drawImage(shown, -origin.x, -origin.y, sheet.width * k, sheet.height * k);
      if (chunk && chunk.page === n && covers(chunk.area, visible)) return; // уже рисуется
      drop();
      const want = chunkFor(sheetSize, win, origin, k);
      if (!want) return;
      const next = document.createElement('canvas');
      const task = doc.renderArea(n, next, {
        scale: loupeScale(sheetScale), rotation, pixelRatio: canvasRatio(want.width, want.height), area: want,
      });
      const made: Chunk = { page: n, area: want, canvas: next, task, ready: false };
      chunk = made;
      task.promise.then(() => {
        if (chunk !== made) { freeCanvas(next); return; }
        made.ready = true;
        schedule();
      }, () => {
        if (chunk === made) chunk = null;
        freeCanvas(next);
      });
    }

    const move = (e: PointerEvent) => {
      if (e.pointerType === 'touch') { pointer.current = null; hide(); return; }
      pointer.current = { x: e.clientX, y: e.clientY };
      schedule();
    };
    const leave = () => { pointer.current = null; cancelAnimationFrame(frame); hide(); };
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerleave', leave);
    // Колесо прокручивает лист: окно следует за листом под неподвижным указателем.
    el.addEventListener('scroll', schedule);
    if (pointer.current) schedule();
    return () => {
      alive = false;
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerleave', leave);
      el.removeEventListener('scroll', schedule);
      cancelAnimationFrame(frame);
      drop();
    };
  }, [scroller, doc, sheetScale, rotation, imgEl, nw, nh]);

  // Выключили лупу — окно ушло, его холст отдаёт память.
  useEffect(() => {
    const cv = canvas.current;
    return () => { if (cv) freeCanvas(cv); };
  }, []);

  return (
    <div ref={box} hidden aria-hidden data-testid="viewer-loupe"
      className={`absolute left-0 top-0 z-10 pointer-events-none overflow-hidden rounded-lg border border-outline shadow-elevation-2 bg-surface-container transition-opacity ${dimmed ? 'opacity-60' : ''}`}>
      <canvas ref={canvas} className="block w-full h-full" />
    </div>
  );
}
