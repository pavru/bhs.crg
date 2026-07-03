import { describe, it, expect } from 'vitest';
import { ruPlural, ruCount } from './pluralize';

describe('ruPlural', () => {
  it('1 → one, кроме 11', () => {
    expect(ruPlural(1, 'раздел', 'раздела', 'разделов')).toBe('раздел');
    expect(ruPlural(21, 'раздел', 'раздела', 'разделов')).toBe('раздел');
    expect(ruPlural(11, 'раздел', 'раздела', 'разделов')).toBe('разделов');
  });

  it('2-4 → few, кроме 12-14', () => {
    expect(ruPlural(2, 'раздел', 'раздела', 'разделов')).toBe('раздела');
    expect(ruPlural(3, 'раздел', 'раздела', 'разделов')).toBe('раздела');
    expect(ruPlural(4, 'раздел', 'раздела', 'разделов')).toBe('раздела');
    expect(ruPlural(24, 'раздел', 'раздела', 'разделов')).toBe('раздела');
    expect(ruPlural(12, 'раздел', 'раздела', 'разделов')).toBe('разделов');
    expect(ruPlural(14, 'раздел', 'раздела', 'разделов')).toBe('разделов');
  });

  it('0, 5-20 → many', () => {
    expect(ruPlural(0, 'раздел', 'раздела', 'разделов')).toBe('разделов');
    expect(ruPlural(5, 'раздел', 'раздела', 'разделов')).toBe('разделов');
    expect(ruPlural(20, 'раздел', 'раздела', 'разделов')).toBe('разделов');
  });
});

describe('ruCount', () => {
  it('форматирует число + существительное', () => {
    expect(ruCount(3, 'комплект', 'комплекта', 'комплектов')).toBe('3 комплекта');
    expect(ruCount(1, 'документ', 'документа', 'документов')).toBe('1 документ');
  });
});
