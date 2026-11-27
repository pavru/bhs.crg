import { expect, it } from 'vitest';
import type { DraftLine } from '../entry/reportContents';
import { reconcileCorrection, type CorrectionContents } from './correctionContents';

function line(id: string, changes: Partial<DraftLine> = {}): DraftLine {
  return { id, workTypeId: 'w', variantId: null, unitId: 'm', volume: '8', sectionId: null, location: null,
    comment: null, details: {}, isCrew: false, crewRole: null, materials: [], participants: [], photos: [], ...changes };
}

const of = (...lines: DraftLine[]): CorrectionContents => ({ hours: '8', note: '', lines });

it('сверяет отдельные ключи сведений и оставляет доставленное фото текущей строки', () => {
  const base = of(line('a', { details: { act: 'А-16', test: '' }, photos: [{ id: 'p', state: 'pending' }] }));
  const draft = of(line('a', { details: { act: 'А-17', test: 'П-17' }, photos: base.lines[0].photos }));
  const current = of(line('a', { details: { act: 'А-18', test: '', note: 'Из другой правки' }, photos: [{ id: 'p', state: 'stored' }] }));
  const key = 'a/details/act';
  const { conflicts, contents } = reconcileCorrection(base, draft, current, { [key]: 'draft' });
  expect(conflicts).toEqual([{ key, label: 'Строка 1 · act', before: 'А-16', current: 'А-18', draft: 'А-17' }]);
  expect(contents.lines[0].details).toEqual({ act: 'А-17', test: 'П-17', note: 'Из другой правки' });
  expect(contents.lines[0].photos).toEqual(current.lines[0].photos);
});

it('явно сверяет удалённую другим менеджером строку с черновиком', () => {
  const base = of(line('a', { photos: [{ id: 'p', state: 'stored' }] }));
  const draft = of({ ...base.lines[0], volume: '9' });
  const current = of();
  const compared = reconcileCorrection(base, draft, current, {});
  expect(compared.conflicts.map(conflict => conflict.key)).toEqual(['a']);
  expect(compared.contents.lines).toEqual([]);
  expect(reconcileCorrection(base, draft, current, { a: 'draft' }).contents.lines).toEqual([line('a', { volume: '9' })]);
  expect(draft.lines[0].photos).toHaveLength(1);
});

it('при сверке сохраняет положение новых строк и требует решения при встречной перестановке', () => {
  const base = of(line('a'), line('b'), line('c'));
  const draft = of(line('new'), line('b'), line('a'), line('c'));
  const current = of(line('a'), line('c'), line('b'), line('remote'));
  const result = reconcileCorrection(base, draft, current, { order: 'draft' });
  expect(result.conflicts.map(conflict => conflict.key)).toEqual(['order']);
  expect(result.contents.lines.map(row => row.id)).toEqual(['new', 'b', 'a', 'c', 'remote']);
});
