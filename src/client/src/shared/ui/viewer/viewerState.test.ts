import { describe, expect, it } from 'vitest';
import {
  anchorOf, CANVAS_PADDING, currentPage, layout, MAX_SCALE, maxCanvasPixels, MIN_SCALE, normalizePage, PAGE_GAP,
  parseZoomMode, pixelRatio, resolveScale, rotateClockwise, scrollTopFor, scrollTopOf, visibleRange, zoomIn,
  zoomLabel, zoomOut,
} from './viewerState';

/** Чистая часть просмотрщика PDF (issue #1490, [UI-11], [UI-16]). */

const A4 = { width: 794, height: 1123 };
const A3 = { width: 1123, height: 1587 };

describe('масштаб', () => {
  it('ступени и границы', () => {
    expect(zoomIn(1)).toBe(1.1);
    expect(zoomOut(1)).toBe(0.9);
    expect(zoomIn(0.83)).toBe(0.9); // «по ширине» между ступенями — к ближайшей крупнее
    expect(zoomOut(0.83)).toBe(0.75);
    expect(zoomIn(MAX_SCALE)).toBe(MAX_SCALE);
    expect(zoomOut(MIN_SCALE)).toBe(MIN_SCALE);
  });

  it('«по ширине» — лист в ширину области без отступов', () => {
    const box = { width: 620 + 2 * CANVAS_PADDING, height: 400 };
    expect(resolveScale('width', [A4], 0, box)).toBeCloseTo(620 / 794);
  });

  it('«страница целиком» — по меньшей стороне', () => {
    const box = { width: 2000 + 2 * CANVAS_PADDING, height: 1123 / 2 + 2 * CANVAS_PADDING };
    expect(resolveScale('page', [A4], 0, box)).toBeCloseTo(0.5);
  });

  it('поворот меняет стороны листа', () => {
    const box = { width: 1123 + 2 * CANVAS_PADDING, height: 5000 };
    expect(resolveScale('width', [A4], 90, box)).toBeCloseTo(1);
    expect(resolveScale('width', [A4], 180, box)).toBeCloseTo(1123 / 794);
  });

  it('листы разного размера — один масштаб по самому широкому и самому высокому', () => {
    const box = { width: 1123 + 2 * CANVAS_PADDING, height: 1587 / 2 + 2 * CANVAS_PADDING };
    const landscape = { width: A4.height, height: A4.width };
    // Порядок листов (и тем более текущий лист) масштаб не меняет: прокрутка ленту не перекраивает.
    expect(resolveScale('width', [A4, landscape], 0, box)).toBeCloseTo(1);
    expect(resolveScale('width', [landscape, A4], 0, box)).toBeCloseTo(1);
    expect(resolveScale('page', [A4, A3], 0, box)).toBeCloseTo(0.5);
    expect(resolveScale('width', [], 0, box)).toBe(1);
  });

  it('число — как есть, в границах', () => {
    expect(resolveScale(1.5, [A4], 0, { width: 10, height: 10 })).toBe(1.5);
    expect(resolveScale(99, [A4], 0, { width: 10, height: 10 })).toBe(MAX_SCALE);
    expect(resolveScale('width', [A4], 0, { width: 1, height: 1 })).toBe(MIN_SCALE);
  });

  it('подписи', () => {
    expect(zoomLabel('width')).toBe('По ширине');
    expect(zoomLabel('page')).toBe('Страница целиком');
    expect(zoomLabel(1.5)).toBe('150 %');
  });

  it('режим из хранилища: чужое — «по ширине»', () => {
    expect(parseZoomMode('page')).toBe('page');
    expect(parseZoomMode('0.75')).toBe(0.75);
    expect(parseZoomMode('мусор')).toBe('width');
    expect(parseZoomMode('100')).toBe('width');
    expect(parseZoomMode(null)).toBe('width');
  });

  it('поворот по часовой и по кругу', () => {
    expect([0, 90, 180, 270].map(rotateClockwise)).toEqual([90, 180, 270, 0]);
  });
});

describe('лента', () => {
  it('разные листы — свои контуры', () => {
    const l = layout([A4, A3], 1, 0);
    expect(l.tops).toEqual([CANVAS_PADDING, CANVAS_PADDING + 1123 + PAGE_GAP]);
    expect(l.heights).toEqual([1123, 1587]);
    expect(l.width).toBe(1123 + 2 * CANVAS_PADDING);
    expect(l.total).toBe(CANVAS_PADDING + 1123 + PAGE_GAP + 1587 + CANVAS_PADDING);
  });

  it('видимые ±2 на длинном документе', () => {
    const l = layout(Array.from({ length: 200 }, () => A4), 0.5, 0);
    const page = 561.5 + PAGE_GAP;
    const top = l.tops[99]; // сотый лист вверху
    expect(visibleRange(l, top, page * 1.5)).toEqual([98, 103]);
    expect(visibleRange(l, 0, page)).toEqual([1, 3]); // виден один первый лист
    expect(visibleRange(l, l.total, page)).toEqual([198, 200]);
  });

  it('пустая лента', () => {
    expect(visibleRange(layout([], 1, 0), 0, 100)).toEqual([0, -1]);
  });

  it('номер страницы — лист в верхней трети', () => {
    const l = layout([A4, A4, A4], 1, 0);
    expect(currentPage(l, 0, 600)).toBe(1);
    expect(currentPage(l, l.tops[1] - 100, 600)).toBe(2);
    expect(scrollTopOf(l, 3)).toBe(l.tops[2] - CANVAS_PADDING);
    expect(scrollTopOf(l, 99)).toBe(l.tops[2] - CANVAS_PADDING);
  });

  it('якорь переживает смену масштаба', () => {
    const at1 = layout([A4, A4, A4], 1, 0);
    const anchor = anchorOf(at1, at1.tops[1] + 1123 / 4);
    expect(anchor).toEqual({ page: 2, fraction: 0.25 });
    const at2 = layout([A4, A4, A4], 2, 0);
    expect(scrollTopFor(at2, anchor)).toBeCloseTo(at2.tops[1] + 2246 / 4);
  });
});

describe('холст', () => {
  it('потолок пикселей: iOS ниже', () => {
    expect(maxCanvasPixels(true)).toBe(2 ** 24);
    expect(maxCanvasPixels(false)).toBe(2 ** 25);
  });

  it('DPR, пока влезает; выше — режется до потолка', () => {
    expect(pixelRatio(800, 1100, 2, 2 ** 24)).toBe(2);
    const big = pixelRatio(4000, 5600, 3, 2 ** 24);
    expect(4000 * 5600 * big * big).toBeLessThanOrEqual(2 ** 24 + 1);
  });
});

describe('номер страницы из поля', () => {
  it('в пределах — число, иначе null', () => {
    expect(normalizePage(' 3 ', 12)).toBe(3);
    expect(normalizePage('0', 12)).toBeNull();
    expect(normalizePage('13', 12)).toBeNull();
    expect(normalizePage('2.5', 12)).toBeNull();
    expect(normalizePage('', 12)).toBeNull();
  });
});
