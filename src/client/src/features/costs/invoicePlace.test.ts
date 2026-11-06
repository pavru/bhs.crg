import { describe, expect, it } from 'vitest';
import { FILE_WIDTH_CSS, FORM_BESIDE_FILE, fileWanted, placeBand, placeOf, positionOf } from './invoicePlace';

/** Место по контейнеру (costs-invoice-screen-10-layout, D-10.3, ТЗ COST-6.3). Окно минус рейл 80. */

const RAIL = 80;

describe('placeOf', () => {
  it('1366: без файла — список слева; с файлом — файл рядом, списка нет', () => {
    expect(placeOf(1366 - RAIL, false)).toEqual({ list: true, file: 'closed', fileWidth: 489 });
    expect(placeOf(1366 - RAIL, true)).toEqual({ list: false, file: 'beside', fileWidth: 489 });
  });

  it('1280: списка нет и без файла; файл рядом, форме не меньше 720', () => {
    expect(placeOf(1280 - RAIL, false)).toEqual({ list: false, file: 'closed', fileWidth: 456 });
    const place = placeOf(1280 - RAIL, true);
    expect(place.file).toBe('beside');
    expect(1280 - RAIL - place.fileWidth).toBeGreaterThanOrEqual(FORM_BESIDE_FILE);
  });

  it('1024 (и 125 % на 1280): файл поверх, списка нет', () => {
    expect(placeOf(1024 - RAIL, true)).toEqual({ list: false, file: 'over', fileWidth: 420 });
    expect(placeOf(1024 - RAIL, false)).toEqual({ list: false, file: 'closed', fileWidth: 420 });
  });

  it('граница 720 — включительно', () => {
    // 1161 × 0,38 = 441,18 → 441: форме ровно 720. 1160 → 441: 719.
    expect(placeOf(1161, true).file).toBe('beside');
    expect(placeOf(1160, true).file).toBe('over');
  });

  it('панель файла — 38 % в пределах 420…520', () => {
    expect(placeOf(2000, true).fileWidth).toBe(520);
    expect(placeOf(600, true).fileWidth).toBe(420);
  });

  it('список — пока форме остаётся не меньше 940', () => {
    expect(placeOf(1260, false).list).toBe(true);
    expect(placeOf(1259, false).list).toBe(false);
  });
});

describe('placeBand — замер перерисовывает страницу только на смене места (ревью PR #1787)', () => {
  it('внутри полосы место одно, на пороге — другое', () => {
    expect(placeBand(1286)).toBe(placeBand(1368));
    expect(placeBand(1100)).toBe(placeBand(1000));
    // Файл рядом — с 1161; без файла список встаёт с 1260.
    expect(placeBand(1161)).not.toBe(placeBand(1160));
    expect(placeBand(1260)).not.toBe(placeBand(1259));
  });

  it('ширина панели файла — та же доля и те же края, что у placeOf', () => {
    expect(FILE_WIDTH_CSS).toBe('clamp(420px, 38%, 520px)');
  });
});

describe('fileWanted', () => {
  const base = { hasFile: true, needsPaper: false, remembered: false, chosen: null };

  it('без файла — не открыт', () => {
    expect(fileWanted({ ...base, hasFile: false, needsPaper: true, chosen: true })).toBe(false);
  });

  it('черновик с метками или сбоем — открыт по умолчанию', () => {
    expect(fileWanted({ ...base, needsPaper: true })).toBe(true);
  });

  it('остальным — как человек оставил', () => {
    expect(fileWanted(base)).toBe(false);
    expect(fileWanted({ ...base, remembered: true })).toBe(true);
  });

  it('выбор по этому счёту сильнее', () => {
    expect(fileWanted({ ...base, needsPaper: true, chosen: false })).toBe(false);
    expect(fileWanted({ ...base, chosen: true })).toBe(true);
  });
});

describe('positionOf', () => {
  const ids = ['a', 'b', 'c'];

  it('N из M и соседи', () => {
    expect(positionOf(ids, 'b')).toEqual({ index: 2, total: 3, prev: 'a', next: 'c' });
  });

  it('на краю отбора соседа нет', () => {
    expect(positionOf(ids, 'c').next).toBeNull();
    expect(positionOf(ids, 'a').prev).toBeNull();
  });

  it('счёта в отборе нет — «— из M»', () => {
    expect(positionOf(ids, 'z')).toEqual({ index: null, total: 3, prev: null, next: null });
  });
});
