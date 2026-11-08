import { describe, expect, it } from 'vitest';
import { estimateFileRefusal } from './estimateFile';

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

  it('пустой файл — отказ', () => {
    expect(estimateFileRefusal(file('смета.gsfx', 0))).toBe('файл пуст');
  });
});
