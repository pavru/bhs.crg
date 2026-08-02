import { describe, it, expect } from 'vitest';
import { assessBulkLink, collidingIdentities, relevance, weighted, tokenize, stem } from './qualityMatch';

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

  /**
   * Симметрия к предыдущему случаю: нечем проверять бывает и СО СТОРОНЫ ДОКУМЕНТА. У
   * нераспознанного «[PDF] Информационное письмо» слов нет вовсе, и без этой ветки предупреждение
   * срабатывало бы на 100 % привязок к нему — включая верные.
   */
  it('документ без слов — «непроверяемо», а не «не похоже»', () => {
    const r = assessBulkLink(['Светильник светодиодный DARKLUM'], name, ['[PDF] Информационное письмо']);

    expect(r.unverifiable).toHaveLength(1);
    expect(r.mismatched).toHaveLength(0);
  });

  /**
   * Короткие существительные — вся номенклатура («кабель», «лампа», «муфта») — по шесть букв, и
   * срез «длиннее шести» их склонения не сводил. На живых связках это ложно тревожило верную связку
   * кабеля РЭМЗ с сертификатом «Кабели силовые».
   */
  it('«кабель» и «кабели» — одно слово', () => {
    expect(stem('кабели')).toBe(stem('кабель'));

    const remz = ['Рыбинский электромонтажный завод — Кабели силовые', 'Кабели силовые с медными жилами ГОСТ 31996-2012'];
    const r = assessBulkLink(['Кабель ВВГнг(А)-FRLS 4х1,5 ГОСТ, РЭМЗ'], name, remz);

    expect(r.fits).toHaveLength(1);
  });

  it('склонения не мешают: «выключатели» и «выключатель» — одно слово', () => {
    expect(stem('выключатели')).toBe(stem('выключатель'));
    const hay = new Set(tokenize('Выключатели автоматические').map(stem));
    expect(relevance(weighted('Выключатель автоматический'), hay)).toBeGreaterThan(0.9);
  });
});

describe('collidingIdentities (issue #585)', () => {
  const norm = (s: string) => s.trim().toLowerCase();
  const row = (key: string, ...idValues: string[]) => ({ key, idValues });

  it('одинаковый артикул при разных полных ключах — спор за связку', () => {
    const rows = [
      row('трубка тут нг 20/10 | t-1', 'Трубка ТУТ нг 20/10', 'T-1'),
      row('термоусаживаемая трубка | t-1', 'Термоусаживаемая трубка', 'T-1'),
    ];
    const colliding = collidingIdentities(rows, norm);
    expect([...colliding.keys()]).toHaveLength(2);
    expect(colliding.get('трубка тут нг 20/10 | t-1')).toEqual(['T-1']);
  });

  it('разные материалы без общих значений не спорят', () => {
    const rows = [row('трубка | t-1', 'Трубка', 'T-1'), row('кабель | k-9', 'Кабель', 'K-9')];
    expect(collidingIdentities(rows, norm).size).toBe(0);
  });

  /**
   * Одна и та же строка приходит дважды — из набора данных и из реквизитов. Ключ у неё один, спорить
   * ей не с кем, и тревожить этим нельзя: иначе значок висел бы на половине реестра и его перестали
   * бы замечать.
   */
  it('дубль одной строки спором не считается', () => {
    const rows = [row('трубка | t-1', 'Трубка', 'T-1'), row('трубка | t-1', 'Трубка', 'T-1')];
    expect(collidingIdentities(rows, norm).size).toBe(0);
  });

  it('пустые значения не сводят строки вместе', () => {
    const rows = [row('трубка | ', 'Трубка', ''), row('кабель | ', 'Кабель', '   ')];
    expect(collidingIdentities(rows, norm).size).toBe(0);
  });
});
