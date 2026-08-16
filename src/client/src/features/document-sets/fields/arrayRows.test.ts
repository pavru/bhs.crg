import { describe, it, expect } from 'vitest';
import { mergeTableRows, moveOrder, dropOrder, applyOrder, remapSelection } from './arrayRows';

const ref = (name: string) => ({ $ref: 'catalog' as const, entryId: name, displayName: name });
const row = (n: string) => ({ Наименование: n });

/**
 * Возврат правок таблицы на места (issue #755).
 *
 * Порядок строк — данные: в реестрах и журналах он и есть порядок в готовом PDF (#663). Прежняя
 * склейка `[...refs, ...rows]` уносила ссылочные строки в начало при КАЖДОМ сохранении, даже когда
 * человек ничего не менял. Тесты держат именно позиции, а не содержимое.
 */
describe('mergeTableRows', () => {
  it('без изменений массив остаётся тем же — включая места ссылок', () => {
    const all = [row('а'), ref('Р1'), row('б'), ref('Р2'), row('в')];
    expect(mergeTableRows(all, [row('а'), row('б'), row('в')], [0, 1, 2])).toEqual(all);
  });

  it('перестановка в таблице сохраняется, ссылки не двигаются', () => {
    const all = [row('а'), ref('Р1'), row('б'), row('в')];
    // В таблице человек поменял местами «б» и «а».
    expect(mergeTableRows(all, [row('б'), row('а'), row('в')], [1, 0, 2]))
      .toEqual([row('б'), ref('Р1'), row('а'), row('в')]);
  });

  it('удаление схлопывает слот УДАЛЁННОЙ строки, а не хвостовой', () => {
    const all = [row('а'), ref('Р1'), row('б'), ref('Р2'), row('в')];
    // Удалили «б» — второй слот.
    expect(mergeTableRows(all, [row('а'), row('в')], [0, 2]))
      .toEqual([row('а'), ref('Р1'), ref('Р2'), row('в')]);
  });

  it('удаление первой строки не протаскивает данные сквозь ссылки', () => {
    // Без происхождения строк схлопывался бы ХВОСТОВОЙ слот, и «и2» уехал бы перед «Р1», а «и3»
    // перед «Р2» — тихая перестановка документа, ровно то, что эта функция и чинит.
    const all = [row('и1'), ref('Р1'), row('и2'), ref('Р2'), row('и3'), row('и4')];
    expect(mergeTableRows(all, [row('и2'), row('и3'), row('и4')], [1, 2, 3]))
      .toEqual([ref('Р1'), row('и2'), ref('Р2'), row('и3'), row('и4')]);
  });

  it('добавленные строки идут в конец', () => {
    const all = [ref('Р1'), row('а')];
    expect(mergeTableRows(all, [row('а'), row('новая')], [0, null]))
      .toEqual([ref('Р1'), row('а'), row('новая')]);
  });

  it('массив из одних ссылок правкой таблицы не трогается', () => {
    const all = [ref('Р1'), ref('Р2')];
    expect(mergeTableRows(all, [], [])).toEqual(all);
  });

  it('ссылка первой строкой остаётся первой', () => {
    const all = [ref('Р1'), row('а'), row('б')];
    expect(mergeTableRows(all, [row('б'), row('а')], [1, 0]))
      .toEqual([ref('Р1'), row('б'), row('а')]);
  });

  it('удаление и перестановка разом', () => {
    const all = [row('а'), ref('Р1'), row('б'), row('в')];
    // Удалили «б», оставшиеся поменяли местами.
    expect(mergeTableRows(all, [row('в'), row('а')], [2, 0]))
      .toEqual([row('в'), ref('Р1'), row('а')]);
  });
});

/**
 * Перестановка и пакетное удаление в аккордеонном списке (issue #754).
 *
 * Тесты держат главное свойство: строки и выбор переставляет ОДИН порядок, поэтому они не могут
 * разъехаться. Расхождение массива с сопутствующим состоянием — это #755, второй раз не надо.
 */
describe('порядок строк массива', () => {
  it('moveOrder двигает элемент вниз', () => {
    expect(moveOrder(4, 0, 2)).toEqual([1, 2, 0, 3]);
  });

  it('moveOrder двигает элемент вверх', () => {
    expect(moveOrder(4, 3, 1)).toEqual([0, 3, 1, 2]);
  });

  it('moveOrder за краем и на месте — порядок не меняется', () => {
    expect(moveOrder(3, 0, -1)).toEqual([0, 1, 2]);
    expect(moveOrder(3, 2, 3)).toEqual([0, 1, 2]);
    expect(moveOrder(3, 1, 1)).toEqual([0, 1, 2]);
  });

  it('dropOrder выбрасывает указанные и только их', () => {
    expect(dropOrder(5, new Set([1, 3]))).toEqual([0, 2, 4]);
    expect(dropOrder(3, new Set())).toEqual([0, 1, 2]);
    expect(dropOrder(2, new Set([0, 1]))).toEqual([]);
  });

  it('applyOrder переставляет строки, включая ссылочные', () => {
    const all = [row('а'), ref('Р1'), row('б')];
    expect(applyOrder(all, moveOrder(3, 2, 0))).toEqual([row('б'), row('а'), ref('Р1')]);
  });

  it('выбор едет вместе со строками', () => {
    // Выбраны «а» (0) и «б» (2). Тащим «б» в начало — выбранными обязаны остаться те же строки.
    const order = moveOrder(3, 2, 0);
    expect(applyOrder([row('а'), ref('Р1'), row('б')], order))
      .toEqual([row('б'), row('а'), ref('Р1')]);
    expect(remapSelection(new Set([0, 2]), order)).toEqual(new Set([1, 0]));
  });

  it('после удаления выбранных выбор пуст, соседи не выбираются', () => {
    const order = dropOrder(4, new Set([1, 2]));
    expect(applyOrder([row('а'), row('б'), row('в'), row('г')], order))
      .toEqual([row('а'), row('г')]);
    expect(remapSelection(new Set([1, 2]), order)).toEqual(new Set());
  });

  it('удаление одной строки не сдвигает выбор на чужую', () => {
    // Выбрана «г» (3). Удаляем «б» (1) корзиной — выбранной обязана остаться «г», теперь под № 2.
    const order = dropOrder(4, new Set([1]));
    expect(remapSelection(new Set([3]), order)).toEqual(new Set([2]));
  });
});
