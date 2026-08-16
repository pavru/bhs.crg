import { describe, it, expect } from 'vitest';
import { mergeTableRows } from './arrayRows';

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
    const edited = [row('а'), row('б'), row('в')];
    expect(mergeTableRows(all, edited)).toEqual(all);
  });

  it('перестановка в таблице сохраняется, ссылки не двигаются', () => {
    const all = [row('а'), ref('Р1'), row('б'), row('в')];
    // В таблице человек поменял местами «б» и «а».
    const edited = [row('б'), row('а'), row('в')];
    expect(mergeTableRows(all, edited)).toEqual([row('б'), ref('Р1'), row('а'), row('в')]);
  });

  it('удаление строки схлопывает её слот, остальные позиции целы', () => {
    const all = [row('а'), ref('Р1'), row('б'), ref('Р2'), row('в')];
    const edited = [row('а'), row('в')];
    expect(mergeTableRows(all, edited)).toEqual([row('а'), ref('Р1'), row('в'), ref('Р2')]);
  });

  it('добавленные строки идут в конец', () => {
    const all = [ref('Р1'), row('а')];
    const edited = [row('а'), row('новая')];
    expect(mergeTableRows(all, edited)).toEqual([ref('Р1'), row('а'), row('новая')]);
  });

  it('массив из одних ссылок правкой таблицы не трогается', () => {
    const all = [ref('Р1'), ref('Р2')];
    expect(mergeTableRows(all, [])).toEqual(all);
  });

  it('ссылка первой строкой остаётся первой', () => {
    // Прямая проверка прежнего дефекта наоборот: раньше ссылки уезжали в начало, здесь важно, что
    // ссылка, которая УЖЕ первая, не утаскивает за собой остальные.
    const all = [ref('Р1'), row('а'), row('б')];
    const edited = [row('б'), row('а')];
    expect(mergeTableRows(all, edited)).toEqual([ref('Р1'), row('б'), row('а')]);
  });
});
