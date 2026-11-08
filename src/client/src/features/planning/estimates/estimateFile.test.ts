import { describe, expect, it } from 'vitest';
import type { FileKindsInfo } from '@/shared/api/fileKinds';
import { estimateFileHint, estimateFileRefusal } from './estimateFile';

// Проверка файла сметы на площадке окна загрузки ([UI-89]): отказ — только тому, что точно не годится.
const file = (name: string, size = 10) => new File([new Uint8Array(size)], name);

describe('estimateFileRefusal', () => {
  it('.gsfx и .xml в любом регистре годятся', () => {
    expect(estimateFileRefusal(file('ЛСР_02-01-03.gsfx'))).toBeNull();
    expect(estimateFileRefusal(file('КОРПУС.GSFX'))).toBeNull();
    expect(estimateFileRefusal(file('смета.Xml'))).toBeNull();
  });

  it('чужое расширение — отказ', () => {
    expect(estimateFileRefusal(file('счёт.pdf'))).toBe('не .gsfx или .xml');
    expect(estimateFileRefusal(file('смета.gsfx.zip'))).toBe('не .gsfx или .xml');
  });

  it('без расширения — на сервер: вид он узнаёт по содержимому', () => {
    expect(estimateFileRefusal(file('смета'))).toBeNull();
    expect(estimateFileRefusal(file('смета.'))).toBeNull();
  });

  it('точка внутри имени — не расширение: на сервер', () => {
    expect(estimateFileRefusal(file('ЛСР 02-01-03 ред.2'))).toBeNull();
    expect(estimateFileRefusal(file('смета v1.5'))).toBeNull();
    expect(estimateFileRefusal(file('Корпус.А'))).toBeNull();
    expect(estimateFileRefusal(file('.gsfx'))).toBeNull();
  });

  it('пустой файл — отказ', () => {
    expect(estimateFileRefusal(file('смета.gsfx', 0))).toBe('файл пуст');
  });

  const kinds = { unknown: 'application/octet-stream', maxBytes: 50 * 1024 * 1024, kinds: [] } as FileKindsInfo;

  it('больше предела реестра — отказ; без реестра размер не судится', () => {
    const sized = (size: number) => Object.defineProperty(file('смета.gsfx'), 'size', { value: size });
    expect(estimateFileRefusal(sized(kinds.maxBytes + 1), kinds)).toBe('больше 50 МБ');
    expect(estimateFileRefusal(sized(kinds.maxBytes + 1))).toBeNull();
    expect(estimateFileRefusal(sized(kinds.maxBytes), kinds)).toBeNull();
  });

  it('подпись называет предел, когда реестр прочитан', () => {
    expect(estimateFileHint(kinds)).toBe('.gsfx или .xml · до 50 МБ');
    expect(estimateFileHint()).toBe('.gsfx или .xml');
  });
});
