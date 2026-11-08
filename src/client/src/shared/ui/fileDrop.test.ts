import { describe, expect, it, vi } from 'vitest';
import { FOLDER_REFUSAL, FOLDERS_REFUSAL, SEVERAL_REFUSAL, zoneRefusal } from './fileDrop';

// Отказ площадки одного файла ([UI-89]): папка → несколько → проверка экрана, «имя — причина».
const file = (name: string, size = 10) => new File([new Uint8Array(size)], name);

describe('zoneRefusal', () => {
  it('годный файл без проверки и с проверкой — отказа нет', () => {
    expect(zoneRefusal({ files: [file('смета.gsfx')], folders: [] })).toBeNull();
    expect(zoneRefusal({ files: [file('смета.gsfx')], folders: [] }, () => null)).toBeNull();
  });

  it('пусто — ни отказа, ни проверки', () => {
    const check = vi.fn(() => 'не годится');
    expect(zoneRefusal({ files: [], folders: [] }, check)).toBeNull();
    expect(check).not.toHaveBeenCalled();
  });

  it('папка — словами пакета и по имени', () => {
    expect(zoneRefusal({ files: [], folders: ['Сканы'] })).toBe(`Сканы — ${FOLDER_REFUSAL}`);
    expect(FOLDER_REFUSAL).toBe('это папка — перетащите файлы из неё');
    // Несколько папок — названы все, ни одна не теряется молча.
    expect(zoneRefusal({ files: [], folders: ['Сканы', 'Акты'] })).toBe(`Сканы, Акты — ${FOLDERS_REFUSAL}`);
    expect(FOLDERS_REFUSAL).toBe('это папки — перетащите файлы из них');
  });

  it('несколько файлов — просит по одному', () => {
    expect(zoneRefusal({ files: [file('a.pdf'), file('b.pdf'), file('c.pdf')], folders: [] }))
      .toBe(`3 файла — ${SEVERAL_REFUSAL}`);
  });

  it('отказ проверки экрана — имя и её причина', () => {
    expect(zoneRefusal({ files: [file('счёт.pdf')], folders: [] }, () => 'не .gsfx или .xml'))
      .toBe('счёт.pdf — не .gsfx или .xml');
  });

  it('порядок: папка раньше нескольких, несколько раньше проверки', () => {
    const check = vi.fn(() => 'не годится');
    expect(zoneRefusal({ files: [file('a.pdf'), file('b.pdf')], folders: ['Сканы'] }, check))
      .toBe(`Сканы — ${FOLDER_REFUSAL}`);
    expect(zoneRefusal({ files: [file('a.pdf'), file('b.pdf')], folders: [] }, check))
      .toBe(`2 файла — ${SEVERAL_REFUSAL}`);
    // Проверка — только для одного файла и не для папки.
    expect(check).not.toHaveBeenCalled();
    expect(zoneRefusal({ files: [file('a.pdf')], folders: ['Сканы'] }, check)).toBe(`Сканы — ${FOLDER_REFUSAL}`);
    expect(check).not.toHaveBeenCalled();
  });

  it('проверка получает сам файл', () => {
    const one = file('смета.xml');
    const check = vi.fn(() => null);
    zoneRefusal({ files: [one], folders: [] }, check);
    expect(check).toHaveBeenCalledWith(one);
  });
});
