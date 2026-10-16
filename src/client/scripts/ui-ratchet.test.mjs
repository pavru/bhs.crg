import { describe, expect, it } from 'vitest';
import { compare, countViolations } from './ui-ratchet-lib.mjs';

// Храповик интерфейса (issue #1349): что считается нарушением и как сравнивается с уровнем.
describe('храповик интерфейса: счёт', () => {
  it('таблица и нативный список', () => {
    expect(countViolations('<table className="w-full"><tr/></table> <select value={v}>'))
      .toEqual({ table: 1, select: 1 });
  });

  it('компоненты-обёртки нарушением не считаются', () => {
    expect(countViolations('<DataGrid rows={r} /> <Select value={v} /> <selection> <PageHeader title="x" />')).toEqual({});
  });

  it('свой заголовок страницы', () => {
    expect(countViolations('<h1 className="text-xl">Стройки</h1> <h2>Раздел</h2> <h1>')).toEqual({ h1: 2 });
  });

  it('произвольный размер текста — px и rem, но не цвет', () => {
    expect(countViolations('text-[11px] md:text-[12.5px] text-[0.7rem] text-xs text-[#333]'))
      .toEqual({ 'text-size': 3, palette: 1 });
  });

  it('сырая палитра с модификаторами и прозрачностью', () => {
    expect(countViolations('bg-indigo-50 hover:text-purple-700 border-red-200/50 placeholder:text-purple-400/70'))
      .toEqual({ palette: 4 });
  });

  it('своя ширина — только в файле с окном', () => {
    const body = '<div className="space-y-4 min-w-[520px]"> <p className="max-w-[18rem] min-w-0 max-w-lg w-[5px] max-w-[45%]">';
    expect(countViolations(`<Modal open wide>${body}</Modal>`)).toEqual({ 'modal-width': 2 });
    expect(countViolations(body)).toEqual({});
    expect(countViolations('<ModalExit /> min-w-[520px]')).toEqual({});
  });

  it('токены и чёрно-белые слои палитрой не считаются', () => {
    expect(countViolations('bg-brand-subtle text-tertiary border-danger/30 bg-black/40 hover:bg-white/10 text-fg2'))
      .toEqual({});
  });
});

describe('храповик интерфейса: сравнение по файлам', () => {
  const baseline = { table: { 'features/a.tsx': 2 } };

  it('перенос нарушения в новый файл — рост, хотя общее число то же', () => {
    const { worse, better } = compare(baseline, { table: { 'features/a.tsx': 1, 'features/b.tsx': 1 } });
    expect(worse).toEqual([{ rule: 'table', file: 'features/b.tsx', was: 0, now: 1 }]);
    expect(better).toEqual([{ rule: 'table', file: 'features/a.tsx', was: 2, now: 1 }]);
  });

  it('удалённый файл опускает уровень', () => {
    expect(compare(baseline, {}).better).toEqual([{ rule: 'table', file: 'features/a.tsx', was: 2, now: 0 }]);
  });

  it('совпадение с уровнем — ни роста, ни улучшения', () => {
    expect(compare(baseline, { table: { 'features/a.tsx': 2 } })).toEqual({ worse: [], better: [] });
  });
});
