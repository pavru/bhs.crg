import { describe, it, expect } from 'vitest';
import { checkImageResult } from './checkImageResult';

/**
 * Проверка прочитанного файла ДО записи в значение (issue #519). Раньше в значение уходило что
 * угодно, а отбраковка случалась при отрисовке — поле оставалось пустым, и человек видел «нажал, и
 * ничего не произошло», при том что мусор уже лежал в реквизитах.
 */
describe('checkImageResult', () => {
  it('картинку пропускает', () => {
    const uri = 'data:image/png;base64,iVBORw0KGgo=';
    expect(checkImageResult(uri, 'печать.png')).toEqual({ src: uri });
  });

  it('SVG — тоже картинка', () =>
    expect(checkImageResult('data:image/svg+xml;base64,PHN2Zz4=', 'схема.svg')).toHaveProperty('src'));

  /** Тот самый обход: «Все файлы» в диалоге мимо accept="image/*". */
  it('не-картинку отклоняет и называет файл', () => {
    const r = checkImageResult('data:application/pdf;base64,JVBERi0=', 'акт.pdf');
    expect(r).not.toHaveProperty('src');
    expect('error' in r && r.error).toContain('акт.pdf');
  });

  /**
   * Пустой `file.type` (Windows отдаёт его, например, для `.tif`) даёт octet-stream. Поле такой файл
   * всё равно не покажет — значит надо сказать вслух и подсказать выход, а не молчать.
   */
  it('нераспознанный тип отклоняет с подсказкой', () => {
    const r = checkImageResult('data:application/octet-stream;base64,SUkq', 'скан.tif');
    expect('error' in r && r.error).toMatch(/PNG или JPG/);
  });

  it('нестроковый результат чтения — отказ, а не падение', () =>
    expect(checkImageResult(new ArrayBuffer(8), 'x.png')).toHaveProperty('error'));
});
