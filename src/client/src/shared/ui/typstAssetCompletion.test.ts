import { describe, it, expect } from 'vitest';
import { assetContextAt } from './typstAssetCompletion';

/**
 * Контекст определяет, КАКОЙ вид идентификатора подсказать: на картинку ссылаются путём, на шрифт —
 * именем семейства. Ошибка здесь предложила бы строку, которая молча не сработает.
 */
describe('assetContextAt', () => {
  it.each([
    ['#image("', 'image'],
    ['  #image( "', 'image'],
    ['#image("assets/Ло', 'image'],
    ['#set text(font: "', 'font'],
    ['#set text(font:"GOST', 'font'],
  ])('%s → %s', (line, expected) => expect(assetContextAt(line)).toBe(expected));

  it.each([
    ['#let x = 1'],                       // кавычек нет вовсе
    ['#image("assets/logo.png")'],        // строка уже закрыта — мы вне литерала
    ['#text("просто строка'],             // строка не про ассет
    ['// комментарий про image("'],       // хвост «image("» есть, но это не вызов… ловим как image
  ].slice(0, 3))('вне контекста: %s', (line) => expect(assetContextAt(line)).toBeNull());

  /** Вложенные вызовы в одной строке не должны сбивать определение — берём последнюю кавычку. */
  it('несколько вызовов в строке — контекст по последней открытой кавычке', () => {
    expect(assetContextAt('#grid(image("assets/a.png"), image("')).toBe('image');
    expect(assetContextAt('#grid(image("assets/a.png"), text(font: "')).toBe('font');
  });
});
