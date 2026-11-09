import { describe, expect, it } from 'vitest';
import { distribution, item, line } from './distributionFixtures';
import {
  distGridLines, draftBalance, factorOf, inDistQueue, inTarget, lineSummary, lineWriteOf, pendingItems, queueCounts,
  sectionNamer, wholeToSection,
} from './distributionView';

/** Правила экрана распределения (plan-10, задача 2.3, макет П2). */
describe('отбор и раскладка', () => {
  const d = distribution({
    lines: [
      line(),
      line({ rowId: 'l2', number: '2', source: 'parts', status: 'partial', parts: [{ sectionId: 's1', quantity: { row: 7, plan: 700 } }],
        rest: { row: 5, plan: 500 }, targets: ['s1'], whole: true }),
      line({ rowId: 'l3', number: '3', lsrSectionCode: '2', source: 'rule', status: 'distributed', targets: ['s2'] }),
    ],
    rules: [
      { lsrSectionCode: '1', lsrSectionName: 'Кабели', target: null, rows: 2, exceptions: 1, undistributed: 1 },
      { lsrSectionCode: '2', lsrSectionName: 'Щиты', target: { kind: 'section', sectionId: 's2' }, rows: 1, exceptions: 0, undistributed: 0 },
    ],
  });

  it('чипы отбирают по статусу и источнику сервера', () => {
    expect(d.lines.filter(l => inDistQueue(l, 'undistributed')).map(l => l.rowId)).toEqual(['l1']);
    expect(d.lines.filter(l => inDistQueue(l, 'partial')).map(l => l.rowId)).toEqual(['l2']);
    expect(d.lines.filter(l => inDistQueue(l, 'exceptions')).map(l => l.rowId)).toEqual(['l2']);
  });

  it('итог по целям ставит отбор по разделу, «стройке в целом» и «не распределено»', () => {
    expect(d.lines.filter(l => inTarget(l, { kind: 'section', sectionId: 's1' })).map(l => l.rowId)).toEqual(['l2']);
    expect(d.lines.filter(l => inTarget(l, { kind: 'whole' })).map(l => l.rowId)).toEqual(['l2']);
    expect(d.lines.filter(l => inTarget(l, { kind: 'undistributed' })).map(l => l.rowId)).toEqual(['l1']);
  });

  it('без отбора разделы свёрнуты; с отбором — только разделы с подходящими строками, раскрытые', () => {
    expect(distGridLines(d, 'all', '', null, new Set()).map(l => l.key)).toEqual(['s:1', 's:2']);
    expect(distGridLines(d, 'all', '', { kind: 'section', sectionId: 's2' }, new Set()).map(l => l.key)).toEqual(['s:2', 'l3']);
    expect(distGridLines(d, 'all', '', null, new Set(['1'])).map(l => l.key)).toEqual(['s:1', 'l1', 'l2', 's:2']);
  });

  it('число на чипе — из счётчиков сервера; исключения — сумма по разделам', () => {
    const counts = queueCounts({ ...d, carryOver: { counts: { sourceRevisionId: null, sourceNumber: 2, carried: 0, shifted: 1, ambiguous: 1, overflow: 0, lost: 0 }, pending: 1, items: [item(), item({ id: 'i2', category: 'shifted', pending: false })] } });
    expect(counts).toEqual({ undistributed: 3, partial: 0, exceptions: 1, 'carry-over': 1 });
  });

  it('чип переноса показывает только ждущие решения позиции', () => {
    expect(pendingItems({ carryOver: { counts: { sourceRevisionId: null, sourceNumber: 2, carried: 0, shifted: 1, ambiguous: 1, overflow: 0, lost: 0 }, pending: 1,
      items: [item(), item({ id: 'i2', category: 'shifted', pending: false })] } }).map(i => i.id)).toEqual(['i1']);
  });
});

describe('клетка «Распределено»', () => {
  const name = sectionNamer(distribution());

  it('по правилу — «Корпус 1 · по разделу», и у строки без объёма тоже', () => {
    const rule = { lsrSectionCode: '1', lsrSectionName: null, target: { kind: 'section' as const, sectionId: 's1' }, rows: 1, exceptions: 0, undistributed: 0 };
    expect(lineSummary(line({ source: 'rule', volume: null, splittable: false }), rule, name, new Set()).text).toBe('Корпус 1 · по разделу');
    expect(lineSummary(line({ source: 'rule' }), { ...rule, target: { kind: 'whole' } }, name, new Set()).text).toBe('стройка в целом · по разделу');
  });

  it('части: одна цель — имя целиком, две — «2 раздела», остаток — «+ стройка в целом»', () => {
    const two = line({ source: 'parts', targets: ['s1', 's2'], parts: [
      { sectionId: 's1', quantity: { row: 7, plan: 700 } }, { sectionId: 's2', quantity: { row: 5, plan: 500 } }] });
    expect(lineSummary(two, undefined, name, new Set()).text).toBe('2 раздела');
    const rest = line({ source: 'parts', targets: ['s1'], parts: [{ sectionId: 's1', quantity: { row: 7, plan: 700 } }], rest: { row: 5, plan: 500 } });
    expect(lineSummary(rest, undefined, name, new Set())).toMatchObject({ text: 'Корпус 1 + стройка в целом', tone: 'neutral' });
  });

  it('не распределено — neutral; удалённая цель — warning', () => {
    expect(lineSummary(line(), undefined, name, new Set())).toEqual({ text: 'не распределено', tone: 'neutral' });
    const gone = line({ source: 'parts', targets: ['s9'], parts: [{ sectionId: 's9', quantity: { row: 1, plan: 100 } }] });
    expect(lineSummary(gone, undefined, name, new Set(['s9'])).tone).toBe('warning');
  });
});

describe('окно строки', () => {
  const l = line();

  it('коэффициент — из объёма в обеих единицах; без плана — нет', () => {
    expect(factorOf(l)).toBe(100);
    expect(factorOf(line({ volume: { row: 12, plan: null } }))).toBeNull();
  });

  it('остаток и превышение — в единице, в которой набирали', () => {
    expect(draftBalance([{ sectionId: 's1', row: '7', plan: '700' }, { sectionId: 's2', row: '5', plan: '500' }], l, 'row'))
      .toEqual({ sum: 12, rest: 0, over: false, invalid: false });
    expect(draftBalance([{ sectionId: 's1', row: '13', plan: '1300' }], l, 'row')).toMatchObject({ over: true });
    expect(draftBalance([{ sectionId: 's1', row: '', plan: '600,5' }], l, 'plan')).toMatchObject({ sum: 600.5, rest: 599.5 });
    expect(draftBalance([{ sectionId: 's1', row: 'abc', plan: '' }], l, 'row').invalid).toBe(true);
    expect(draftBalance([{ sectionId: 's1', row: '0', plan: '' }], l, 'row').invalid).toBe(true);
  });

  it('набор: пустые пары не пишутся, ни одной — «к правилу» (пустой массив сервер не принимает)', () => {
    expect(lineWriteOf('l1', [{ sectionId: 's1', row: '7', plan: '' }, { sectionId: '', row: '5', plan: '' }], 'row'))
      .toEqual({ rowId: 'l1', unit: 'row', parts: [{ sectionId: 's1', quantity: 7 }] });
    expect(lineWriteOf('l1', [{ sectionId: 's1', row: '', plan: '' }], 'row')).toEqual({ rowId: 'l1', unit: 'row', parts: null });
    expect(lineWriteOf('l1', [{ sectionId: 's1', row: '', plan: '1 200' }], 'plan'))
      .toEqual({ rowId: 'l1', unit: 'plan', parts: [{ sectionId: 's1', quantity: 1200 }] });
  });

  it('«На раздел…» — строка целиком; без объёма — пропущена', () => {
    expect(wholeToSection([l, line({ rowId: 'l2', splittable: false, volume: null })], 's2')).toEqual({
      writes: [{ rowId: 'l1', unit: 'row', parts: [{ sectionId: 's2', quantity: 12 }] }], skipped: 1,
    });
  });
});
