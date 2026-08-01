import { describe, it, expect } from 'vitest';
import { assessBulkLink, relevance, weighted, tokenize, stem } from './qualityMatch';

/** Сертификат на автоматы EKF модели AV-125 — тот самый, на котором висело 69 связок (#552). */
const AV125 = [
  'EKF — автоматические выключатели',
  'Выключатели автоматические, торговой марки «EKF», модель: AV-125. Продукция изготовлена в соответствии с Директивами.',
];

const name = (s: string) => s;

describe('assessBulkLink (issue #552)', () => {
  /**
   * Главный случай разбора: к сертификату на автоматы AV-125 массовой привязкой прицепили
   * светильники, розетки и рамки. Ни одного сигнала при этом не было.
   */
  it('чужая продукция попадает в «не похоже»', () => {
    const r = assessBulkLink([
      'Выключатель автоматический AV-125 3P 80A EKF AVERES',
      'Светильник светодиодный DARKLUM встраиваемый',
      'Рамка-суппорт под 6 модулей Brava',
    ], name, AV125);

    expect(r.fits.map(name)).toEqual(['Выключатель автоматический AV-125 3P 80A EKF AVERES']);
    expect(r.mismatched).toHaveLength(2);
    expect(r.unverifiable).toHaveLength(0);
  });

  /**
   * И обратное, ради чего группы вообще разделены: голый артикул сравнивать НЕ С ЧЕМ. Ноль здесь
   * означает «непроверяемо», а не «неверно» — на живых данных таких 58 связок из 113, и красить их
   * тревогой значит приучить человека не смотреть на предупреждение.
   */
  it('артикул без слов — «непроверяемо», а не «не похоже»', () => {
    const r = assessBulkLink(['mb15-07-01m-54', 'as-32', '31502r'], name, AV125);

    expect(r.unverifiable).toHaveLength(3);
    expect(r.mismatched).toHaveLength(0);
    expect(r.fits).toHaveLength(0);
  });

  it('пустой выбор — пустая оценка', () => {
    const r = assessBulkLink([], name, AV125);
    expect(r).toEqual({ fits: [], unverifiable: [], mismatched: [] });
  });

  it('документ без текста не объявляет всё подряд подходящим', () => {
    const r = assessBulkLink(['Светильник светодиодный DARKLUM'], name, ['']);
    expect(r.mismatched).toHaveLength(1);
  });

  it('склонения не мешают: «выключатели» и «выключатель» — одно слово', () => {
    expect(stem('выключатели')).toBe(stem('выключатель'));
    const hay = new Set(tokenize('Выключатели автоматические').map(stem));
    expect(relevance(weighted('Выключатель автоматический'), hay)).toBeGreaterThan(0.9);
  });
});
