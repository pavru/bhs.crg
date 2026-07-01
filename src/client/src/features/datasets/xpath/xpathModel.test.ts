import { describe, it, expect } from 'vitest';
import { parseXPath, toXPath, type XPathModel } from './xpathModel';

function roundTrip(text: string) {
  const model = parseXPath(text);
  expect(model).not.toBeNull();
  return toXPath(model!);
}

describe('parseXPath — распознаваемое подмножество', () => {
  it('простой абсолютный путь без условий', () => {
    expect(roundTrip('/Root/Item')).toBe('/Root/Item');
  });

  it('относительный путь', () => {
    expect(roundTrip('Name')).toBe('Name');
  });

  it('атрибут в последнем шаге', () => {
    expect(roundTrip('/Root/Item/@id')).toBe('/Root/Item/@id');
  });

  it('wildcard-шаг', () => {
    expect(roundTrip('/Root/*')).toBe('/Root/*');
  });

  it('условие equals с одинарными кавычками', () => {
    expect(roundTrip("/Root/Item[@id='2']")).toBe("/Root/Item[@id='2']");
  });

  it('условие equals с двойными кавычками (сериализация нормализует к одинарным)', () => {
    // Модель не хранит исходный символ кавычки — сериализатор всегда выбирает
    // одинарные, если значение их не содержит. Важно, что выражение распознаётся.
    expect(roundTrip('/Root/Item[@id="2"]')).toBe("/Root/Item[@id='2']");
  });

  it('условие != ', () => {
    expect(roundTrip("/Root/Item[@status!='deleted']")).toBe("/Root/Item[@status!='deleted']");
  });

  it('условие contains()', () => {
    expect(roundTrip("/Root/Item[contains(Name, 'Кабель')]")).toBe("/Root/Item[contains(Name, 'Кабель')]");
  });

  it('условие exists (просто путь)', () => {
    expect(roundTrip('/Root/Item[Note]')).toBe('/Root/Item[Note]');
  });

  it('позиционное условие — индекс', () => {
    expect(roundTrip('/Root/Item[1]')).toBe('/Root/Item[1]');
  });

  it('позиционное условие — last()', () => {
    expect(roundTrip('/Root/Item[last()]')).toBe('/Root/Item[last()]');
  });

  it('несколько условий подряд (and)', () => {
    expect(roundTrip("/Root/Item[@id='2'][Note]")).toBe("/Root/Item[@id='2'][Note]");
  });

  it('вложенный относительный путь внутри условия', () => {
    expect(roundTrip("/Root/Item[Info/Code='A1']")).toBe("/Root/Item[Info/Code='A1']");
  });

  it('значение условия со слэшем внутри (не должно ломать разбиение шагов)', () => {
    expect(roundTrip("/Root/Item[Path='a/b']")).toBe("/Root/Item[Path='a/b']");
  });

  it('значение условия со скобками внутри', () => {
    expect(roundTrip("/Root/Item[Note='(важно)']")).toBe("/Root/Item[Note='(важно)']");
  });

  it('кириллица в именах элементов', () => {
    expect(roundTrip("/Корень/Позиция[@Артикул='123']")).toBe("/Корень/Позиция[@Артикул='123']");
  });
});

describe('parseXPath — вне поддерживаемого поднабора → null', () => {
  it.each([
    '//Item',                          // descendant-or-self ось
    'Item/following-sibling::Other',   // произвольная ось
    'Item[1] | Item[2]',               // union
    'Item[substring(Name,1,1)="A"]',   // произвольная функция
    'Item[@a=@b or @c=@d]',            // булева логика внутри предиката
    '',
    '   ',
  ])('%s', (text) => {
    expect(parseXPath(text)).toBeNull();
  });

  it('атрибут не в последнем шаге', () => {
    expect(parseXPath('/Root/@id/Item')).toBeNull();
  });
});

describe('toXPath', () => {
  it('собирает модель без условий', () => {
    const model: XPathModel = { absolute: true, steps: [{ axis: 'child', name: 'Root', predicates: [] }] };
    expect(toXPath(model)).toBe('/Root');
  });

  it('собирает модель с условием equals', () => {
    const model: XPathModel = {
      absolute: true,
      steps: [
        { axis: 'child', name: 'Root', predicates: [] },
        { axis: 'child', name: 'Item', predicates: [{ kind: 'equals', path: '@id', op: '=', value: '2' }] },
      ],
    };
    expect(toXPath(model)).toBe("/Root/Item[@id='2']");
  });
});
