import { describe, it, expect } from 'vitest';
import { buildPreviewModel, getPath } from './materializePreviewModel';

describe('buildPreviewModel (issue #393)', () => {
  it('разворачивает вложенные объекты в колонки-листья с точечными путями', () => {
    const rows = [
      { Обозначение: 'ВРЦ', Кабель: { Марка: 'ВВГ', Сечение: '5x16' } },
      { Обозначение: 'ГРЩ', Кабель: { Марка: 'ПуГВ', Сечение: '4x25' } },
    ];
    const m = buildPreviewModel(rows);
    expect(m.leaves.map(l => l.key)).toEqual(['Обозначение', 'Кабель.Марка', 'Кабель.Сечение']);
    expect(m.unwrappedLabel).toBe(''); // верхних ключей несколько — размотки нет
    // значение достаётся по пути
    expect(getPath(m.rows[0], ['Кабель', 'Марка'])).toBe('ВВГ');
  });

  it('single-child unwrap: общий верхний ключ размотан, имя — в подписи (кейс union)', () => {
    const rows = [
      { КабельнаяЛинияТрасса: { Обозначение: 'ВРЦ', КабельПоПроекту: { Марка: 'ВВГ' } } },
      { КабельнаяЛинияТрасса: { Обозначение: 'ГРЩ', КабельПоПроекту: { Марка: 'ПуГВ' } } },
    ];
    const m = buildPreviewModel(rows, k => (k === 'КабельнаяЛинияТрасса' ? 'Кабельная линия методом трасс' : undefined));
    // префикс варианта размотан — колонки без него
    expect(m.leaves.map(l => l.key)).toEqual(['Обозначение', 'КабельПоПроекту.Марка']);
    expect(m.unwrappedLabel).toBe('Кабельная линия методом трасс'); // берётся title, не ключ
    expect(getPath(m.rows[1], ['КабельПоПроекту', 'Марка'])).toBe('ПуГВ');
  });

  it('НЕ разматывает, если у строк разные верхние ключи', () => {
    const rows = [{ A: { x: 1 } }, { B: { y: 2 } }];
    const m = buildPreviewModel(rows);
    expect(m.unwrappedLabel).toBe('');
    expect(m.leaves.map(l => l.key)).toEqual(['A.x', 'B.y']);
  });

  it('двухэтажная шапка: листья одного родителя идут подряд, группы схлопнуты', () => {
    const rows = [{ Обозначение: 'ВРЦ', Кабель: { Марка: 'ВВГ', Сечение: '5x16' }, Примечание: 'ok' }];
    const m = buildPreviewModel(rows);
    // группы: [Обозначение(без родителя)], [Кабель: Марка,Сечение], [Примечание(без родителя)]
    expect(m.groups.map(g => ({ p: g.parentKey, n: g.leaves.length }))).toEqual([
      { p: '', n: 1 },
      { p: 'Кабель', n: 2 },
      { p: '', n: 1 },
    ]);
  });

  it('порядок листьев — натуральный (порядок полей типа), depth-1 не хоистятся к группам', () => {
    const rows = [{ Обозначение: 'ВРЦ', Кабель: { Марка: 'ВВГ' }, Примечание: 'ok' }];
    const m = buildPreviewModel(rows);
    // Примечание остаётся ПОСЛЕ группы Кабель, а не подтягивается к другим листьям-без-родителя
    expect(m.leaves.map(l => l.key)).toEqual(['Обозначение', 'Кабель.Марка', 'Примечание']);
  });

  it('массивы и файлы — листья (не разворачиваются в объект)', () => {
    const rows = [{ Работы: [1, 2, 3], Файл: { $type: 'file', blobPath: 'b/x', fileName: 'a.pdf', size: 10 } }];
    const m = buildPreviewModel(rows);
    expect(m.leaves.map(l => l.key)).toEqual(['Работы', 'Файл']);
    expect(getPath(m.rows[0], ['Работы'])).toEqual([1, 2, 3]);
  });

  it('пустые строки → нет колонок', () => {
    expect(buildPreviewModel([{}, {}]).leaves).toEqual([]);
    expect(buildPreviewModel([]).leaves).toEqual([]);
  });
});
