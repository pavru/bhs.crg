import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { EXTRACTION_FIELDS, PROCESSING_FIELDS, putSourceInCache } from './sourceCache';
import type { DataSetFile, DataSetSource } from './types';

/** Источник, каким его отдаёт СПИСОК: со счётчиком привязок и живой схемой. */
const listed = (over: Partial<DataSetSource> = {}): DataSetSource => ({
  id: 's1', fileId: 'f1', name: 'Счета', sheetOrPath: 'system:table:costs.invoices', columnExpressions: null,
  cachedSchema: '[{"name":"Живая"}]', cachedRowCount: 12,
  rowFilter: null, computedColumns: null, sortSpec: null,
  processingVersion: 'v1', materializationVersion: 'm1',
  tags: null, recognitionStale: false, bindingCount: 3, warning: 'оговорка',
  materializeTypeId: null, materializeMapping: null,
  ...over,
});

/** Он же в ответе МУТАЦИИ: счётчика привязок нет, схема — из кэша, оговорки нет. */
const answered = (over: Partial<DataSetSource> = {}): DataSetSource => listed({
  cachedSchema: '[]', cachedRowCount: 0, bindingCount: null, warning: null, ...over,
});

const file = (sources: DataSetSource[], id = 'f1') => ({ id, name: 'Набор', sources }) as unknown as DataSetFile;

function cacheWith(files: DataSetFile[]) {
  const qc = new QueryClient();
  qc.setQueryData(['datasets', 'files', 'System', undefined, true], files);
  return qc;
}
const sourcesIn = (qc: QueryClient) =>
  qc.getQueryData<DataSetFile[]>(['datasets', 'files', 'System', undefined, true])!.flatMap(f => f.sources);

describe('putSourceInCache', () => {
  it('вписывает изменённое и ОБЕ версии — следующий диалог откроется с новой версии', () => {
    const qc = cacheWith([file([listed()])]);
    const sort = [{ column: 'Номер', direction: 'asc' as const }];

    putSourceInCache(qc, answered({ sortSpec: sort, processingVersion: 'v2', materializationVersion: 'm2' }), PROCESSING_FIELDS);

    const [source] = sourcesIn(qc);
    expect(source.sortSpec).toEqual(sort);
    expect(source.processingVersion).toBe('v2');
    expect(source.materializationVersion).toBe('m2');
  });

  // Ответ мутации беднее списка: вписанный целиком, он стёр бы счётчик привязок и живые схему с
  // оговоркой системного источника — до фонового перечитывания чип привязок пропал бы, а диалоги
  // остались бы без колонок.
  it('не трогает то, чего правка не меняла: счётчик привязок, живую схему, оговорку', () => {
    const qc = cacheWith([file([listed()])]);

    putSourceInCache(qc, answered({ processingVersion: 'v2' }), PROCESSING_FIELDS);

    const [source] = sourcesIn(qc);
    expect(source.bindingCount).toBe(3);
    expect(source.cachedSchema).toBe('[{"name":"Живая"}]');
    expect(source.cachedRowCount).toBe(12);
    expect(source.warning).toBe('оговорка');
  });

  it('правка извлечения приносит и разобранную по нему схему', () => {
    const qc = cacheWith([file([listed({ sheetOrPath: 'Лист1' })])]);

    putSourceInCache(qc, answered({
      name: 'Иначе', sheetOrPath: 'Лист2', cachedSchema: '[{"name":"Новая"}]', cachedRowCount: 7, processingVersion: 'v2',
    }), EXTRACTION_FIELDS);

    const [source] = sourcesIn(qc);
    expect(source).toMatchObject({ name: 'Иначе', sheetOrPath: 'Лист2', cachedSchema: '[{"name":"Новая"}]', cachedRowCount: 7 });
  });

  it('соседние источники и чужие наборы остаются как были', () => {
    const neighbour = listed({ id: 's2', processingVersion: 'n1' });
    const foreign = listed({ id: 's1', fileId: 'f2', processingVersion: 'x1' });
    const qc = cacheWith([file([listed(), neighbour]), file([foreign], 'f2')]);

    putSourceInCache(qc, answered({ processingVersion: 'v2' }), PROCESSING_FIELDS);

    expect(sourcesIn(qc).map(s => s.processingVersion)).toEqual(['v2', 'n1', 'x1']);
  });

  // Под префиксом ['datasets','files'] лежат и страницы PDF-набора — данные другой формы.
  it('кэш другой формы под тем же префиксом не трогает', () => {
    const qc = cacheWith([file([listed()])]);
    const pages = [{ id: 'f1', index: 0 }];
    const grouping = { groups: [] };
    qc.setQueryData(['datasets', 'files', 'f1', 'pages'], pages);
    qc.setQueryData(['datasets', 'files', 'f1', 'grouping'], grouping);

    putSourceInCache(qc, answered({ processingVersion: 'v2' }), PROCESSING_FIELDS);

    expect(qc.getQueryData(['datasets', 'files', 'f1', 'pages'])).toEqual(pages);
    expect(qc.getQueryData(['datasets', 'files', 'f1', 'grouping'])).toBe(grouping);
  });
});
