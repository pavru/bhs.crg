import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { compare, countViolations } from './ui-ratchet-lib.mjs';

// Пустой уровень семи правил ([UI-81], sharedui-md3-17 PR 5б; list-row — sharedui-md3-21 PR 2). Отдельно от ui-ratchet.test.mjs — тот у
// порога FileSizeRatchet. На нуле уровень пуст, и первое же место в любом файле — красное: проверяем, что
// каждое правило до него дотягивается, а не только то, что уровень сейчас чист.
const FIXTURES = {
  'notice-plaque': '<div className="bg-warning-container border rounded-md px-3 py-2">Счёт изменили</div>',
  'hand-card': '<section className="rounded-md border border-outline-variant bg-surface p-4">Итоги</section>',
  'raw-field': '<input value={v} className="w-full border border-outline rounded-xs px-2" />',
  'legacy-radius': '<div className="rounded-2xl" />',
  'legacy-shadow': '<div className="shadow-sm" />',
  'legacy-type': '<p className="text-xs" />',
  'list-row': '<div role="option" className="px-3 py-1.5">Стройка</div>',
};


const file = 'features/x/X.tsx';
const actualOf = text => Object.fromEntries(
  Object.entries(countViolations(text, { dir: 'features', file })).map(([rule, n]) => [rule, { [file]: n }]));

describe('храповик интерфейса: пустой уровень (sharedui-md3-17)', () => {
  it.each(Object.entries(FIXTURES))('%s: первое место при пустом уровне — красное', (rule, text) => {
    expect(compare({}, actualOf(text)).worse).toContainEqual({ rule, file, was: 0, now: 1 });
  });

  // notice-plaque и hand-card доведены до нуля экраном счёта (costs-invoice-screen-11: hand-card — PR 1, notice-plaque —
  // PR 2, 07.11.2026).
  it('уровень: семь правил пусты', () => {
    const baseline = JSON.parse(readFileSync(new URL('../ui-baseline.json', import.meta.url), 'utf8'));
    for (const rule of ['raw-field', 'legacy-type', 'legacy-radius', 'legacy-shadow', 'list-row', 'hand-card', 'notice-plaque'])
      expect(baseline[rule], rule).toEqual({});
  });
});
