import { describe, expect, it } from 'vitest';
import {
  apply, chunkFor, covers, EDGE, imageToSheet, loupeAvailable, loupeScale, loupeView, loupeWindow, NEAR,
  sheetAt, STRIP_HEIGHT, visiblePart, WINDOW,
} from './magnifierGeometry';

describe('масштаб лупы — max(200 %, лист × 2), не больше 400 % [UI-92]', () => {
  it.each([
    [0.5, 2], [1, 2], [1.5, 3], [2.5, 4],
  ])('лист %f → лупа %f', (sheet, loupe) => {
    expect(loupeScale(sheet)).toBeCloseTo(loupe);
  });

  it('от 300 % лупа недоступна, ниже — доступна', () => {
    expect(loupeAvailable(2.99)).toBe(true);
    expect(loupeAvailable(3)).toBe(false);
    expect(loupeAvailable(4)).toBe(false);
  });
});

describe('окно у указателя в пределах области [UI-92]', () => {
  const wide = { width: 900, height: 536 };
  const narrow = { width: 404, height: 536 };

  it('в широкой области — 320 × 160, серединой под указателем', () => {
    expect(loupeWindow(wide, { x: 450, y: 300 })).toEqual({ x: 290, y: 220, ...WINDOW });
  });

  it('у правого и нижнего края — прижато, целиком внутри', () => {
    const w = loupeWindow(wide, { x: 895, y: 530 });
    expect(w.x + w.width).toBe(wide.width - EDGE);
    expect(w.y + w.height).toBe(wide.height - EDGE);
  });

  it('у левого и верхнего края — тоже', () => {
    expect(loupeWindow(wide, { x: 2, y: 3 })).toMatchObject({ x: EDGE, y: EDGE });
  });

  it('в области уже 520 — полоса по ширине минус 8 + 8, высотой 140; ведёт её только вертикаль', () => {
    const a = loupeWindow(narrow, { x: 40, y: 200 });
    const b = loupeWindow(narrow, { x: 380, y: 200 });
    expect(a).toEqual({ x: EDGE, y: 130, width: narrow.width - 2 * EDGE, height: STRIP_HEIGHT });
    expect(b).toEqual(a);
  });

  it.each([
    ['прямоугольник', { width: 900, height: 150 }],
    ['полоса', { width: 400, height: 120 }],
  ])('область ниже окна (%s) — окно сжато по высоте и не выходит за край: прокрутку не растит', (_k, area) => {
    for (const y of [0, area.height / 2, area.height]) {
      const w = loupeWindow(area, { x: area.width / 2, y });
      expect(w).toMatchObject({ y: EDGE, height: area.height - 2 * EDGE });
    }
  });
});

describe('точка листа под указателем — в окне под указателем', () => {
  const sheet = { x: 10, y: 10, width: 384, height: 543 };

  it('в середине и у края — одно правило: содержимое не прыгает', () => {
    for (const pointer of [{ x: 200, y: 200 }, { x: 390, y: 520 }]) {
      const win = loupeWindow({ width: 404, height: 536 }, pointer);
      const { origin } = loupeView(sheet, win, pointer, 4);
      // Под указателем в окне — точка увеличенного листа, соответствующая точке под указателем.
      expect(origin.x + (pointer.x - win.x)).toBeCloseTo((pointer.x - sheet.x) * 4);
      expect(origin.y + (pointer.y - win.y)).toBeCloseTo((pointer.y - sheet.y) * 4);
    }
  });
});

describe('лист под указателем', () => {
  // Два листа с зазором 8, как в ленте.
  const sheets = [
    { x: 10, y: 10, width: 384, height: 543 },
    { x: 10, y: 561, width: 384, height: 543 },
  ];

  it('над листом — его номер', () => {
    expect(sheetAt(sheets, { x: 100, y: 100 })).toBe(0);
    expect(sheetAt(sheets, { x: 100, y: 700 })).toBe(1);
  });

  it('на зазоре между листами окно не гаснет — ближний лист', () => {
    expect(sheetAt(sheets, { x: 100, y: 556 })).toBe(0);
    expect(sheetAt(sheets, { x: 100, y: 559 })).toBe(1);
  });

  it(`дальше ${NEAR} px от листа — окна нет`, () => {
    expect(sheetAt(sheets, { x: 394 + NEAR + 1, y: 100 })).toBe(-1);
    expect(sheetAt(sheets, { x: 394 + NEAR, y: 100 })).toBe(0);
    expect(sheetAt([], { x: 0, y: 0 })).toBe(-1);
  });
});

describe('кусок с запасом 3 × 3 окна [UI-93]', () => {
  const sheet = { width: 400, height: 560 };
  const win = { width: 320, height: 160 };

  it('вокруг видимого, в пределах увеличенного листа', () => {
    const origin = { x: 600, y: 600 };
    expect(chunkFor(sheet, win, origin, 2)).toEqual({ x: 280, y: 440, width: 520, height: 480 });
  });

  it('у угла листа — срезан листом', () => {
    expect(chunkFor(sheet, win, { x: -100, y: -50 }, 2)).toEqual({ x: 0, y: 0, width: 540, height: 270 });
  });

  it('внутри куска — движок не нужен; вышел — нужен новый', () => {
    const chunk = chunkFor(sheet, win, { x: 300, y: 300 }, 2)!;
    const still = visiblePart(sheet, win, { x: 450, y: 400 }, 2)!;
    const out = visiblePart(sheet, win, { x: 300, y: 300 + 321 }, 2)!;
    expect(covers(chunk, still)).toBe(true);
    expect(covers(chunk, out)).toBe(false);
  });

  it('видимое за краем листа — только его часть на листе', () => {
    expect(visiblePart(sheet, win, { x: 700, y: -40 }, 2)).toEqual({ x: 700, y: 0, width: 100, height: 120 });
    expect(visiblePart(sheet, win, { x: 900, y: 0 }, 2)).toBeNull();
  });

  it('при масштабе листа 50 / 150 / 300 % кусок укладывается в потолок пикселей при DPR 2', () => {
    for (const s of [0.5, 1.5, 3]) {
      const k = loupeScale(s) / s;
      const big = { width: 794 * s, height: 1123 * s };
      const c = chunkFor(big, win, { x: 100 * k, y: 100 * k }, k)!;
      expect(c.width * c.height * 4).toBeLessThan(2 ** 25);
    }
  });
});

describe('картинка в лист с поворотом показа', () => {
  const natural = { width: 200, height: 100 };

  it.each([
    // [поворот, угол картинки (0,0) → точка листа, угол (200,0) → точка листа]
    [0, { x: 0, y: 0 }, { x: 100, y: 0 }],
    [90, { x: 50, y: 0 }, { x: 50, y: 100 }],
    [180, { x: 100, y: 50 }, { x: 0, y: 50 }],
    [270, { x: 0, y: 100 }, { x: 0, y: 0 }],
  ])('%i°', (rotation, topLeft, topRight) => {
    const m = imageToSheet(natural, 0.5, rotation as number);
    expect(apply(m, { x: 0, y: 0 })).toEqual(topLeft);
    expect(apply(m, { x: 200, y: 0 })).toEqual(topRight);
  });
});
