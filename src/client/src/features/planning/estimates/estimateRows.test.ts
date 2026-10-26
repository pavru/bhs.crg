import { describe, expect, it } from 'vitest';
import type { EstimateRevision, EstimateRow, RevisionSection } from '@/shared/api/planningEstimates';
import { gridLines, inQueue, queueCount, rowNumber, visibleRevisions } from './estimateRows';

function row(over: Partial<EstimateRow> & { id: string }): EstimateRow {
  return {
    order: 1, lsrSection: { code: '1', name: 'Кабельные конструкции' }, numberInSection: '1', number: null,
    subordinate: false, kind: 'work', rateCode: 'ГЭСНм08-02-399-01', name: 'Кабель до 35 кВ', sourceVolume: 12.4,
    sourceUnit: '100 м', workTypeId: null, unitId: null, factor: null, volume: null, planItemId: null, marks: [],
    materials: [], labor: [], ...over,
  };
}

function section(code: string | null, over: Partial<RevisionSection> = {}): RevisionSection {
  return { code, name: code ? `Раздел ${code}` : null, rows: 1, unmatched: 0, materialsUnmatched: 0, units: [], ...over };
}

const unmatched = { code: 'unmatched', text: 'Не сопоставлена' };
const remembered = { code: 'remembered', text: 'Подставлено из запомненного' };

function material(id: string, nomenclatureId: string | null, marks: EstimateRow['materials'][number]['marks']) {
  return { id, order: 1, nomenclatureId, sourceName: 'Провод', sourceCode: null, sourceUnit: 'м', quantity: 1, marks };
}

describe('отбор «что ждёт»', () => {
  it('по пометкам сервера, а материалы без номенклатуры — у строк любого вида', () => {
    const work = row({ id: 'w', marks: [unmatched] });
    const material = row({
      id: 'm', kind: 'material', materials: [{
        id: 'r', order: 1, nomenclatureId: null, sourceName: 'Провод', sourceCode: null, sourceUnit: 'м', quantity: 1,
        marks: [unmatched],
      }],
    });
    expect(inQueue(work, 'unmatched')).toBe(true);
    expect(inQueue(material, 'unmatched')).toBe(false);
    expect(inQueue(material, 'no-nomenclature')).toBe(true);
    expect(inQueue(work, 'no-nomenclature')).toBe(false);
  });

  it('«без номенклатуры» — по пометке материала с сервера, а не по пустой номенклатуре', () => {
    const silent = row({ id: 's', materials: [material('a', null, [])] });
    const marked = row({ id: 'm', materials: [material('a', 'n1', [unmatched])] });
    expect(inQueue(silent, 'no-nomenclature')).toBe(false);
    expect(inQueue(marked, 'no-nomenclature')).toBe(true);
  });

  it('«Запомнено» — и расценка строки, и сопоставление её материала', () => {
    const byRate = row({ id: 'r', marks: [remembered] });
    const byMaterial = row({ id: 'm', materials: [material('a', 'n1', []), material('b', 'n2', [remembered])] });
    const plain = row({ id: 'p', materials: [material('a', 'n1', [])] });
    expect(inQueue(byRate, 'remembered')).toBe(true);
    expect(inQueue(byMaterial, 'remembered')).toBe(true);
    expect(inQueue(plain, 'remembered')).toBe(false);
  });

  it('число чипа — строки очереди, а не материалы: три материала одной строки дают «1»', () => {
    const three = row({ id: 'w', materials: ['a', 'b', 'c'].map(id => material(id, null, [unmatched])) });
    const other = row({ id: 'o', materials: [material('d', 'n1', [])] });
    expect(queueCount([three, other], 'no-nomenclature')).toBe(1);
    expect(gridLines([section('1', { rows: 2 })], [three, other], 'no-nomenclature', '', new Set()))
      .toMatchObject([{ kind: 'section', shown: 1 }, { kind: 'row', key: 'w' }]);
  });
});

describe('раскладка по разделам ЛСР', () => {
  const rows = [
    row({ id: 'a', lsrSection: { code: '1', name: null }, marks: [unmatched] }),
    row({ id: 'b', lsrSection: { code: '1', name: null } }),
    row({ id: 'c', lsrSection: { code: '2', name: null } }),
    row({ id: 'd', lsrSection: null }),
  ];
  const sections = [section('1', { rows: 2, unmatched: 1 }), section('2'), section(null)];

  it('без отбора все разделы свёрнуты, раскрытый показывает свои строки', () => {
    expect(gridLines(sections, rows, 'all', '', new Set()).map(l => l.key)).toEqual(['s:1', 's:2', `s:${'\u0000вне разделов'}`]);
    expect(gridLines(sections, rows, 'all', '', new Set(['1'])).map(l => l.key)).toEqual(['s:1', 'a', 'b', 's:2', `s:${'\u0000вне разделов'}`]);
  });

  it('с отбором — только разделы, где ждут, и они раскрыты', () => {
    const lines = gridLines(sections, rows, 'unmatched', '', new Set());
    expect(lines.map(l => l.key)).toEqual(['s:1', 'a']);
    expect(lines[0]).toMatchObject({ kind: 'section', open: true, shown: 1 });
  });

  it('поиск — по шифру и наименованию, строки вне разделов тоже находятся', () => {
    const named = [...rows.slice(0, 3), row({ id: 'd', lsrSection: null, name: 'Розетка штепсельная' })];
    expect(gridLines(sections, named, 'all', 'розетка', new Set()).map(l => l.key)).toEqual([`s:${'\u0000вне разделов'}`, 'd']);
  });
});

describe('номер строки', () => {
  it('сквозной № п/п из файла, у старых редакций — номер в разделе', () => {
    expect(rowNumber(row({ id: 'a', number: '13.1', numberInSection: '2' }))).toBe('13.1');
    expect(rowNumber(row({ id: 'a', number: null, numberInSection: '2' }))).toBe('2');
  });
});

function revision(number: number, over: Partial<EstimateRevision> = {}): EstimateRevision {
  return {
    id: `r${number}`, number, current: false, importedAt: '2026-10-20T00:00:00Z', importedBy: null, sourceFileName: null,
    schemaVersion: null, rows: 1, unmatched: 0, materialsUnmatched: 0, unitChanged: 0, importId: null, partial: null,
    frozen: null, version: '1', ...over,
  };
}

describe('вкладки редакций', () => {
  const revisions = [1, 2, 3, 4, 5, 6, 7].map(n => revision(n, { current: n === 2 }));

  it('видны действующая и последние, остальные — в «ещё N»', () => {
    const { tabs, more } = visibleRevisions({ revisions }, 'r7');
    expect(tabs.map(r => r.number)).toEqual([7, 6, 5, 2]);
    expect(more.map(r => r.number)).toEqual([4, 3, 1]);
  });

  it('открытая из «ещё N» встаёт вкладкой', () => {
    const { tabs, more } = visibleRevisions({ revisions }, 'r3');
    expect(tabs.map(r => r.number)).toEqual([7, 6, 3, 2]);
    expect(more.map(r => r.number)).toEqual([5, 4, 1]);
  });
});
