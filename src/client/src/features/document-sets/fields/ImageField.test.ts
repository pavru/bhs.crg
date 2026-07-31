import { describe, it, expect } from 'vitest';
import { checkImageResult, checkImageSize } from './ImageField';

/**
 * Отказ по размеру — вместо уменьшения (issue #521). Уменьшение сейчас необратимо: оригинал лежит
 * data-URI в JSONB, положить его больше некуда, а печать и подпись попадают в подписываемый PDF.
 */
describe('checkImageSize', () => {
  const MB = 1024 * 1024;

  it('в пределах — молчит', () => expect(checkImageSize(5 * MB)).toBeNull());
  it('ровно предел — пропускает', () => expect(checkImageSize(10 * MB)).toBeNull());

  /** «Файл слишком большой» не говорит, насколько именно и что делать, — числа обязательны. */
  it('сверх предела — называет оба числа', () => {
    const msg = checkImageSize(42 * MB);
    expect(msg).toContain('42');
    expect(msg).toContain('10');
  });

  /**
   * Предел — целыми мегабайтами, как его называет сервер (issue #530): одно ограничение, показанное
   * двумя способами, читается как два разных, а после переезда в блобы эти сообщения встретятся
   * у одного поля.
   */
  it('предел пишется без дробной части', () =>
    expect(checkImageSize(42 * MB)).toContain('10 МБ'));

  /**
   * Число выбрано по пути запроса: base64 это ×4/3, и едет он внутри общего PUT со всеми полями
   * документа при потолке тела 58 МБ. Четыре таких поля обязаны укладываться, иначе Kestrel рвёт
   * запрос на транспорте и пользователь теряет правку, не зная, какое поле виновато.
   */
  it('четыре поля предельного размера укладываются в тело запроса', () => {
    const encoded = 10 * MB * (4 / 3);
    expect(4 * encoded).toBeLessThan(58 * MB);
  });
});

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
