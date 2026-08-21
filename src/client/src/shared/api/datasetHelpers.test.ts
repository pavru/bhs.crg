import { describe, it, expect } from 'vitest';
import {
  parseSourceColumns, parseSourceColumnNames, countFilterConditions, cleanFilterNode,
  mergeBindingPreviewsIntoValues, computeBoundFieldKeys, computeRecognizedFieldKeys,
  computeStaleFieldKeys, computeStaleReasonByField, staleReasonText,
  isFileMappingValue, parseFileMapping, buildFileMapping, nextSourceName,
} from './datasetHelpers';
import type { FilterGroup, DataSetBindingPreviewResult } from './types';

describe('parseSourceColumns', () => {
  it('returns [] for null/undefined/blank', () => {
    expect(parseSourceColumns(null)).toEqual([]);
    expect(parseSourceColumns(undefined)).toEqual([]);
    expect(parseSourceColumns('')).toEqual([]);
  });

  it('returns [] for malformed JSON', () => {
    expect(parseSourceColumns('{ broken')).toEqual([]);
  });

  it('returns [] when JSON is not an array', () => {
    expect(parseSourceColumns('{"name":"x"}')).toEqual([]);
  });

  it('parses column descriptors', () => {
    const json = JSON.stringify([{ name: 'A', sampleValues: ['1'] }, { name: 'B' }]);
    expect(parseSourceColumns(json)).toEqual([{ name: 'A', sampleValues: ['1'] }, { name: 'B' }]);
  });
});

describe('parseSourceColumnNames', () => {
  it('extracts names only', () => {
    const json = JSON.stringify([{ name: 'A' }, { name: 'B' }]);
    expect(parseSourceColumnNames(json)).toEqual(['A', 'B']);
  });
  it('returns [] on bad input', () => {
    expect(parseSourceColumnNames('nope')).toEqual([]);
  });
});

describe('countFilterConditions', () => {
  it('returns 0 for null/undefined', () => {
    expect(countFilterConditions(null)).toBe(0);
    expect(countFilterConditions(undefined)).toBe(0);
  });

  it('counts a single condition', () => {
    expect(countFilterConditions({ type: 'condition', column: 'A', op: 'eq', value: '1' })).toBe(1);
  });

  it('does not count a condition with empty column', () => {
    expect(countFilterConditions({ type: 'condition', column: '', op: 'eq' })).toBe(0);
  });

  it('counts conditions across nested groups', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [
        { type: 'condition', column: 'A', op: 'eq', value: '1' },
        {
          type: 'group', logic: 'or',
          children: [
            { type: 'condition', column: 'B', op: 'eq', value: '2' },
            { type: 'condition', column: 'C', op: 'eq', value: '3' },
            { type: 'condition', column: '', op: 'eq' }, // not counted
          ],
        },
      ],
    };
    expect(countFilterConditions(tree)).toBe(3);
  });

  it('returns 0 for an empty group', () => {
    expect(countFilterConditions({ type: 'group', logic: 'and', children: [] })).toBe(0);
  });
});

describe('cleanFilterNode', () => {
  it('keeps a valid condition', () => {
    const c = { type: 'condition', column: 'A', op: 'eq', value: '1' } as const;
    expect(cleanFilterNode(c)).toEqual(c);
  });

  it('drops a condition with a blank column', () => {
    expect(cleanFilterNode({ type: 'condition', column: '  ', op: 'eq' })).toBeNull();
  });

  it('returns null for a group with only empty conditions', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [{ type: 'condition', column: '', op: 'eq' }],
    };
    expect(cleanFilterNode(tree)).toBeNull();
  });

  it('prunes empty children but keeps valid ones', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [
        { type: 'condition', column: 'A', op: 'eq', value: '1' },
        { type: 'condition', column: '', op: 'eq' },
        { type: 'group', logic: 'or', children: [] },
      ],
    };
    const cleaned = cleanFilterNode(tree) as FilterGroup;
    expect(cleaned.children).toHaveLength(1);
    expect((cleaned.children[0] as { column: string }).column).toBe('A');
  });

  it('collapses a group whose only sub-group is empty', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [{ type: 'group', logic: 'or', children: [{ type: 'condition', column: '', op: 'eq' }] }],
    };
    expect(cleanFilterNode(tree)).toBeNull();
  });
});

describe('mergeBindingPreviewsIntoValues', () => {
  function scalar(data: Record<string, string | null>): DataSetBindingPreviewResult {
    return { bindingId: '1', sourceName: 's', fileName: 'f', mode: 'scalar', targetFieldKey: null, totalRows: 1, data, error: null };
  }
  function tabular(targetFieldKey: string, data: Record<string, string | null>[]): DataSetBindingPreviewResult {
    return { bindingId: '2', sourceName: 's', fileName: 'f', mode: 'tabular', targetFieldKey, totalRows: data.length, data, error: null };
  }

  it('overwrites matching scalar key', () => {
    const result = mergeBindingPreviewsIntoValues({ inn: 'старое', name: 'не трогать' }, [scalar({ inn: 'новое' })]);
    expect(result.inn).toBe('новое');
    expect(result.name).toBe('не трогать');
  });

  it('does not overwrite existing value with empty scalar', () => {
    const result = mergeBindingPreviewsIntoValues({ inn: 'ручное' }, [scalar({ inn: '' })]);
    expect(result.inn).toBe('ручное');
  });

  it('writes tabular array into targetFieldKey, even empty', () => {
    const result = mergeBindingPreviewsIntoValues({ Чертежи: [{ old: true }] }, [tabular('Чертежи', [])]);
    expect(result['Чертежи']).toEqual([]);
  });

  it('skips error bindings', () => {
    const errored: DataSetBindingPreviewResult = { bindingId: '3', sourceName: 's', fileName: 'f', mode: 'error', targetFieldKey: null, totalRows: 0, data: {}, error: 'нет источника' };
    const result = mergeBindingPreviewsIntoValues({ inn: 'прежнее' }, [errored]);
    expect(result.inn).toBe('прежнее');
  });
});

describe('computeBoundFieldKeys', () => {
  it('collects scalar mapping keys and array targetFieldKeys separately', () => {
    const { scalarKeys, arrayKeys } = computeBoundFieldKeys([
      { targetFieldKey: null, mapping: { inn: 'ИНН', name: 'Название' } },
      { targetFieldKey: 'Чертежи', mapping: { НомерЛиста: 'НомерЛиста' } },
    ]);
    expect([...scalarKeys]).toEqual(['inn', 'name']);
    expect([...arrayKeys]).toEqual(['Чертежи']);
  });

  it('returns empty sets for no bindings', () => {
    const { scalarKeys, arrayKeys } = computeBoundFieldKeys([]);
    expect(scalarKeys.size).toBe(0);
    expect(arrayKeys.size).toBe(0);
  });
});

describe('file mapping (@@file:)', () => {
  it('round-trips column and sizeColumn through build/parse', () => {
    const encoded = buildFileMapping({ column: 'ФайлПуть', sizeColumn: 'РазмерБайт' });
    expect(isFileMappingValue(encoded)).toBe(true);
    expect(parseFileMapping(encoded)).toEqual({ column: 'ФайлПуть', sizeColumn: 'РазмерБайт' });
  });

  it('round-trips without a sizeColumn', () => {
    const encoded = buildFileMapping({ column: 'ФайлПуть', sizeColumn: '' });
    expect(parseFileMapping(encoded)).toEqual({ column: 'ФайлПуть', sizeColumn: '' });
  });

  it('does not treat a plain column name as a file mapping', () => {
    expect(isFileMappingValue('ФайлПуть')).toBe(false);
    expect(parseFileMapping('ФайлПуть')).toBeNull();
  });

  it('does not confuse file mapping with ref mapping prefix', () => {
    expect(isFileMappingValue('@@ref:{"column":"X","match":"","typeId":"1"}')).toBe(false);
  });

  it('returns null for malformed JSON', () => {
    expect(parseFileMapping('@@file:not-json')).toBeNull();
  });

  it('returns null when column is missing', () => {
    expect(parseFileMapping('@@file:{"sizeColumn":"РазмерБайт"}')).toBeNull();
  });
});

// Имя источника — единственное, чем источники различимы в селекторе привязки (issue #717).
describe('nextSourceName', () => {
  it('оставляет имя как есть, если оно свободно', () => {
    expect(nextSourceName(['Другое'], 'Документы комплекта')).toBe('Документы комплекта');
  });

  it('нумерует занятое имя со второго', () => {
    expect(nextSourceName(['Документы комплекта'], 'Документы комплекта')).toBe('Документы комплекта — 2');
  });

  it('пропускает уже занятые номера, а не идёт по порядку', () => {
    expect(nextSourceName(['Реестр', 'Реестр — 2', 'Реестр — 4'], 'Реестр')).toBe('Реестр — 3');
  });

  // Копия «Реестр — 2» — это «Реестр — 3», а не «Реестр — 2 — 2»: суффикс наш, наращивать его вглубь незачем.
  it('не наращивает собственный суффикс нумерации', () => {
    expect(nextSourceName(['Реестр', 'Реестр — 2'], 'Реестр — 2')).toBe('Реестр — 3');
  });

  // Регистр не различает: «документы» рядом с «Документы» в выпадающем списке так же неразличимы.
  it('считает имена занятыми без учёта регистра', () => {
    expect(nextSourceName(['ДОКУМЕНТЫ'], 'документы')).toBe('документы — 2');
  });
});

describe('computeRecognizedFieldKeys', () => {
  const recognized = { origin: 'Recognized' as const };
  const parsed = { origin: 'Parsed' as const };

  it('берёт и скалярные ключи маппинга, и табличное поле', () => {
    const keys = computeRecognizedFieldKeys([
      { targetFieldKey: null, mapping: { Шифр: 'A', Титул: 'B' }, source: recognized },
      { targetFieldKey: 'Схемы', mapping: {}, source: recognized },
    ]);
    expect(keys).toEqual(new Set(['Шифр', 'Титул', 'Схемы']));
  });

  it('нераспознанные источники не помечаются', () => {
    // Parsed и System в интерфейсе ничего не меняют: у них нет действия для читателя, а метка без
    // действия становится фоном, который перестают замечать.
    const keys = computeRecognizedFieldKeys([
      { targetFieldKey: 'Материалы', mapping: {}, source: parsed },
      { targetFieldKey: 'Реестр', mapping: {}, source: { origin: 'System' as const } },
      { targetFieldKey: 'Схемы', mapping: {}, source: undefined },
    ]);
    expect(keys.size).toBe(0);
  });

  it('пустой маппинг — берём маппинг материализации источника', () => {
    // Тем же правилом форма решает, какие поля сделать read-only. Разойдись они — часть полей стала
    // бы нередактируемой без объяснения, откуда взялось значение.
    const keys = computeRecognizedFieldKeys([
      { targetFieldKey: null, mapping: {}, source: { ...recognized, materializeMapping: { Шифр: 'A' } } },
    ]);
    expect(keys).toEqual(new Set(['Шифр']));
  });

  it('смесь: помечается только распознанная часть', () => {
    const keys = computeRecognizedFieldKeys([
      { targetFieldKey: 'Материалы', mapping: {}, source: parsed },
      { targetFieldKey: 'Схемы', mapping: {}, source: recognized },
    ]);
    expect(keys).toEqual(new Set(['Схемы']));
  });
});

describe('computeStaleFieldKeys', () => {
  const stale = { origin: 'Recognized' as const, recognitionStale: true };
  const fresh = { origin: 'Recognized' as const, recognitionStale: false };

  it('устаревание считается по ВСЕМ источникам, а не только распознанным', () => {
    // Парсерный источник, не разобравшийся против нового файла, тоже устарел — и человеку у поля
    // это так же важно. Свяжи признак с распознаванием, и такое поле промолчало бы.
    const keys = computeStaleFieldKeys([
      { targetFieldKey: 'Материалы', mapping: {}, source: { origin: 'Parsed', recognitionStale: true } },
      { targetFieldKey: 'Схемы', mapping: {}, source: fresh },
    ]);
    expect(keys).toEqual(new Set(['Материалы']));
  });

  it('разбирает привязку теми же правилами, что и происхождение', () => {
    const bindings: Parameters<typeof computeStaleFieldKeys>[0] = [
      { targetFieldKey: null, mapping: { Шифр: 'A' }, source: stale },
      { targetFieldKey: null, mapping: {}, source: { ...stale, materializeMapping: { Титул: 'B' } } },
      { targetFieldKey: 'Схемы', mapping: {}, source: stale },
    ];
    expect(computeStaleFieldKeys(bindings)).toEqual(new Set(['Шифр', 'Титул', 'Схемы']));
    // Ровно те же ключи считает признак происхождения — множества разные только по условию.
    expect(computeRecognizedFieldKeys(bindings)).toEqual(new Set(['Шифр', 'Титул', 'Схемы']));
  });

  it('свежие источники не помечаются', () => {
    expect(computeStaleFieldKeys([
      { targetFieldKey: 'Схемы', mapping: {}, source: fresh },
      { targetFieldKey: 'Прочее', mapping: {}, source: undefined },
    ]).size).toBe(0);
  });
});

describe('staleReasonText', () => {
  it('каждая причина называется своим словом', () => {
    const texts = (['FileReplaced', 'NotParsedAgainstNewFile', 'TableBoundariesChanged', 'ProfileChanged'] as const)
      .map(r => staleReasonText(r));
    expect(new Set(texts).size).toBe(4);
    expect(texts.every(t => t.length > 0)).toBe(true);
  });

  it('незнакомая причина не оставляет читателя без ответа', () => {
    // Признак пришёл с сервера, значит что-то случилось; промолчать хуже, чем сказать общее.
    expect(staleReasonText(null)).toBe('Данные источника устарели');
    expect(staleReasonText(undefined)).toBe('Данные источника устарели');
  });

  it('текст — констатация, без глагола', () => {
    // Действие дописывает место показа: путь к «Перераспознать» есть не отовсюду, а подсказка,
    // требующая невыполнимого, обесценивается так же, как та, что горит всегда.
    for (const r of ['FileReplaced', 'NotParsedAgainstNewFile', 'TableBoundariesChanged', 'ProfileChanged'] as const) {
      expect(staleReasonText(r)).not.toMatch(/[Пп]ерераспозна|[Пп]роверьте|[Оо]ткройте/);
    }
  });
});

describe('computeStaleReasonByField', () => {
  it('поле получает причину СВОЕГО источника, а не совпавшего по ключу', () => {
    // Табличная привязка названа целевым полем, и её маппинг описывает колонки строк, а не
    // реквизиты. Отдельный поиск причины совпадал и по тому, и по другому — и поле «Шифр»
    // получало причину от таблицы, которая его не заполняет.
    const reasons = computeStaleReasonByField([
      {
        targetFieldKey: 'Схемы', mapping: { Шифр: 'A' },
        source: { recognitionStale: true, staleReason: 'TableBoundariesChanged' },
      },
      {
        targetFieldKey: null, mapping: { Шифр: 'B' },
        source: { recognitionStale: true, staleReason: 'FileReplaced' },
      },
    ]);
    expect(reasons.get('Схемы')).toBe('TableBoundariesChanged');
    expect(reasons.get('Шифр')).toBe('FileReplaced');
  });

  it('свежие источники в карту не попадают', () => {
    const reasons = computeStaleReasonByField([
      { targetFieldKey: 'Схемы', mapping: {}, source: { recognitionStale: false, staleReason: 'FileReplaced' } },
    ]);
    expect(reasons.size).toBe(0);
  });
});
