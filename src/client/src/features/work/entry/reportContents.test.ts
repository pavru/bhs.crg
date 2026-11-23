import { expect, it } from 'vitest';
import { emptyLine, rebaseContents, type DraftLine } from './reportContents';

const material = (id: string, note: string | null = null) => ({ nomenclatureId: id, note });
const line: DraftLine = { ...emptyLine(), isCrew: true, materials: [material('base')], participants: ['base'] };
const contents = (row: DraftLine) => ({ hours: '', lines: [row] });

it('combines independent additions and removals by material and participant ID', () => {
  const local = { ...line, materials: [material('local')], participants: ['local'] };
  const fresh = { ...line, materials: [...line.materials, material('remote')], participants: [...line.participants, 'remote'] };

  const result = rebaseContents(contents(line), contents(local), contents(fresh));

  expect(result.contents.lines[0].materials).toEqual([material('remote'), material('local')]);
  expect(result.contents.lines[0].participants).toEqual(['remote', 'local']);
  expect(result.conflicts).toEqual([]);
});

it('keeps the current material on a concurrent note edit and reports the local note', () => {
  const local = { ...line, materials: [material('base', 'локальное примечание')] };
  const fresh = { ...line, materials: [material('base', 'серверное примечание')] };

  const result = rebaseContents(contents(line), contents(local), contents(fresh));

  expect(result.contents.lines[0].materials).toEqual(fresh.materials);
  expect(result.conflicts).toEqual([expect.stringContaining('локальное примечание')]);
});

it('keeps the crew flag when another author added a participant while it was being cleared', () => {
  const local = { ...line, isCrew: false, participants: [] };
  const fresh = { ...line, participants: ['base', 'remote'] };

  const result = rebaseContents(contents(line), contents(local), contents(fresh));

  expect(result.contents.lines[0]).toMatchObject({ isCrew: true, participants: ['remote'] });
  expect(result.conflicts).toEqual([expect.stringContaining('признак бригады')]);
});
