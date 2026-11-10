// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { createRef, useRef } from 'react';
import { Magnifier, type MagnifierProps } from './Magnifier';
import { fakeDocument } from './testEngine';
import { loupeWindow } from './magnifierGeometry';
import type { PageTask } from './pdfEngine';

/**
 * Окно лупы (issue #1871, [UI-92], [UI-93]): у указателя, нет вне листа, растянутый холст листа до
 * готовности куска, кусок отдаёт память, касание окна не показывает. Раскладку jsdom не считает —
 * размеры области и листов заданы руками; движок подменный.
 */

let AREA = { width: 900, height: 1400 };
/** Замеры листов — сколько раз читали раскладку. */
let sheetReads = 0;
// Лист 794 × 1123 (100 %) в 10 px от края области, второй — ниже через зазор 8.
const SHEETS = [{ x: 53, y: 10, width: 794, height: 1123 }, { x: 53, y: 1141, width: 794, height: 1123 }];

const rect = (r: { x: number; y: number; width: number; height: number }) =>
  ({ ...r, left: r.x, top: r.y, right: r.x + r.width, bottom: r.y + r.height, toJSON: () => r }) as DOMRect;

/** Область с листами нужной раскладки — как её разметили бы лента или картинка. */
function Area(p: Omit<MagnifierProps, 'scroller'>) {
  const scroller = useRef<HTMLDivElement>(null);
  return (
    <div ref={el => {
      scroller.current = el;
      if (!el) return;
      Object.defineProperty(el, 'clientWidth', { configurable: true, get: () => AREA.width });
      Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => AREA.height });
      el.getBoundingClientRect = () => rect({ x: 0, y: 0, ...AREA });
    }} data-testid="area">
      {SHEETS.map((s, i) => (
        <div key={i} data-page={i + 1} ref={el => { if (el) el.getBoundingClientRect = () => { sheetReads++; return rect(s); }; }} />
      ))}
      <Magnifier scroller={scroller} {...p} />
    </div>
  );
}

let tasks: { area: unknown; cancel: ReturnType<typeof vi.fn>; finish: () => void }[];
function engine() {
  const doc = fakeDocument(2);
  tasks = [];
  doc.renderArea = vi.fn((_n, _c, view): PageTask => {
    let finish = () => {};
    const promise = new Promise<void>(res => { finish = res; });
    const task = { area: view.area, cancel: vi.fn(), finish };
    tasks.push(task);
    return { promise, cancel: task.cancel };
  });
  return doc;
}

const move = (x: number, y: number, pointerType = 'mouse') =>
  fireEvent.pointerMove(screen.getByTestId('area'), { clientX: x, clientY: y, pointerType });
const loupe = () => screen.getByTestId('viewer-loupe');
const flush = () => new Promise(r => setTimeout(r, 0));

let resize: (() => void)[];
beforeEach(() => {
  AREA = { width: 900, height: 1400 };
  sheetReads = 0;
  resize = [];
  vi.stubGlobal('ResizeObserver', class {
    constructor(cb: () => void) { resize.push(cb); }
    observe() {}
    disconnect() {}
  });
  // jsdom холст не рисует: без подмены каждая отрисовка печатала бы «Not implemented».
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { cb(0); return 0; });
  vi.stubGlobal('cancelAnimationFrame', () => {});
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('окно лупы', () => {
  it('у указателя, в пределах области, скрыто от диктора', () => {
    render(<Area doc={engine()} sheetScale={1} rotation={0} />);
    expect(loupe().hidden).toBe(true);
    move(450, 300);
    const w = loupeWindow(AREA, { x: 450, y: 300 });
    expect(loupe().hidden).toBe(false);
    expect(loupe().style.transform).toBe(`translate(${w.x}px, ${w.y}px)`);
    expect(loupe().style.width).toBe('320px');
    expect(loupe().getAttribute('aria-hidden')).toBe('true');
  });

  it('вне листа окна нет; на зазоре между листами — есть', () => {
    render(<Area doc={engine()} sheetScale={1} rotation={0} />);
    move(20, 300);
    expect(loupe().hidden).toBe(true);
    move(450, 1137);
    expect(loupe().hidden).toBe(false);
  });

  it('до готовности куска — растянутый холст листа; готов — кусок движка', async () => {
    const doc = engine();
    render(<Area doc={doc} sheetScale={1} rotation={0} />);
    move(450, 300);
    expect(loupe().dataset.source).toBe('sheet');
    expect(doc.renderArea).toHaveBeenCalledOnce();
    expect(vi.mocked(doc.renderArea).mock.calls[0][2]).toMatchObject({ scale: 2, rotation: 0 });
    tasks[0].finish();
    await flush();
    expect(loupe().dataset.source).toBe('chunk');
  });

  it('в пределах куска движок заново не рисует; вышел — прежний отменён, заказан новый', async () => {
    const doc = engine();
    render(<Area doc={doc} sheetScale={1} rotation={0} />);
    move(450, 300);
    tasks[0].finish();
    await flush();
    move(480, 320);
    expect(doc.renderArea).toHaveBeenCalledOnce();
    expect(loupe().dataset.source).toBe('chunk');
    move(450, 520);
    expect(doc.renderArea).toHaveBeenCalledTimes(2);
    expect(tasks[0].cancel).toHaveBeenCalled();
    expect(loupe().dataset.source).toBe('sheet');
  });

  it('кусок отдаёт память: уход указателя, смена масштаба, выключение лупы', () => {
    const doc = engine();
    const { rerender, unmount } = render(<Area doc={doc} sheetScale={1} rotation={0} />);
    move(450, 300);
    fireEvent.pointerLeave(screen.getByTestId('area'));
    expect(tasks[0].cancel).toHaveBeenCalled();
    expect(loupe().hidden).toBe(true);

    move(450, 300);
    rerender(<Area doc={doc} sheetScale={1.5} rotation={0} />);
    expect(tasks[1].cancel).toHaveBeenCalled();

    unmount();
    expect(tasks.at(-1)!.cancel).toHaveBeenCalled();
  });

  it('касание окна не показывает и движок не зовёт', () => {
    const doc = engine();
    render(<Area doc={doc} sheetScale={1} rotation={0} />);
    move(450, 300, 'touch');
    expect(loupe().hidden).toBe(true);
    expect(doc.renderArea).not.toHaveBeenCalled();
  });

  it('картинка — окно рисует её само, без движка', () => {
    const img = createRef<HTMLImageElement>();
    render(<Area imageEl={img} natural={{ width: 1588, height: 2246 }} sheetScale={0.5} rotation={90} />);
    move(450, 300);
    expect(loupe().hidden).toBe(false);
    expect(loupe().dataset.source).toBe('image');
  });

  it('раскладку листов замеряет один раз; смена размера — замер заново', () => {
    render(<Area doc={engine()} sheetScale={1} rotation={0} />);
    move(450, 300);
    move(470, 320);
    fireEvent.scroll(screen.getByTestId('area'));
    expect(sheetReads).toBe(SHEETS.length);
    resize.forEach(f => f());
    expect(sheetReads).toBe(2 * SHEETS.length);
  });

  it('область сузилась без хода указателя — окно становится полосой сразу', () => {
    render(<Area doc={engine()} sheetScale={1} rotation={0} />);
    move(450, 300);
    expect(loupe().style.width).toBe('320px');
    AREA = { width: 480, height: 1400 };
    resize.forEach(f => f());
    expect(loupe().style.width).toBe(`${480 - 16}px`);
  });

  it('приглушено вместе с листом', () => {
    render(<Area doc={engine()} sheetScale={1} rotation={0} dimmed />);
    expect(loupe().className).toContain('opacity-60');
  });
});
