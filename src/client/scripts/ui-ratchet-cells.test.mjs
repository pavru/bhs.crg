import { describe, expect, it } from 'vitest';
import { countViolations, RULES } from './ui-ratchet-lib.mjs';

// Правило cell-field ([UI-78], sharedui-md3-14 PR 2). Отдельно от ui-ratchet.test.mjs — тот у порога
// FileSizeRatchet. Счёт — только своего правила.
const cells = text => countViolations(text, { dir: 'features', file: 'features/x/X.tsx' })['cell-field'] ?? 0;

/** Колонка прямо в `columns` сетки правки. */
const grid = cell => `<EditGrid rows={rows} rowKey={r => r.key} columns={[{ key: 'a', label: 'А', cell: (r, i) => (${cell}) }]} />`;

describe('храповик интерфейса: cell-field', () => {
  it('сырое поле в клетке — нарушение, флажок — нет', () => {
    expect(cells(grid('<input className="w-full border" value={r.a} aria-label="А" />'))).toBe(1);
    expect(cells(grid('<input value={r.a} aria-label="А" />'))).toBe(1);
    expect(cells(grid('<textarea value={r.a} />'))).toBe(1);
    expect(cells(grid('<input type="checkbox" checked={r.on} />'))).toBe(0);
  });

  it('поле со своим размером не клетки — нарушение; без размера и размера клетки — нет', () => {
    expect(cells(grid('<Select density="dense" aria-label="Раздел" value="" onValueChange={f} />'))).toBe(1);
    expect(cells(grid('<NumberInput density={dense} label="Кол-во" value="" onChange={f} />'))).toBe(1);
    expect(cells(grid('<Select density="cell" aria-label="Раздел" value="" onValueChange={f} />'))).toBe(0);
    expect(cells(grid('<TextField label="Ед." value={r.a} onChange={f} />'))).toBe(0);
    expect(cells(grid('<NumberInput label="Кол-во" value="" onChange={f} />'))).toBe(0);
  });

  it('дата размера места не берёт: без density="cell" — нарушение', () => {
    expect(cells(grid('<DateInput label="Дата" value="" onChange={f} compact />'))).toBe(1);
    expect(cells(grid('<DateInput label="Дата" value="" onChange={f} density="cell" />'))).toBe(0);
  });

  it('голое поле подсказки и выбор «внутри фразы» в клетке — нарушения', () => {
    expect(cells(grid('<SuggestField compact label="Марка" value="" onChange={f} />'))).toBe(1);
    expect(cells(grid('<Select variant="inline" aria-label="Вид" value="" onValueChange={f} />'))).toBe(1);
  });

  it('колонки под типом EditGridColumn — тоже клетки; шапка колонки — нет', () => {
    expect(cells(`const columns: EditGridColumn<Row>[] = [{
      key: 'a', label: 'А',
      header: <Select density="dense" aria-label="Раздел" value="" onValueChange={f} />,
      cell: r => <input value={r.a} />,
    }];`)).toBe(1);
    expect(cells('targets.map((t): EditGridColumn<Row> => ({ key: t.key, label: t.name, cell: r => <Select density="dense" aria-label="А" value="" onValueChange={f} /> }))')).toBe(1);
  });

  it('колонки DataGrid правило не смотрит', () => {
    expect(cells(`<DataGrid rows={rows} rowKey={r => r.id} columns={[{ key: 'a', label: 'А', cell: r => <input value={r.a} /> }]} />`)).toBe(0);
    expect(cells(`const columns: DataGridColumn<Row>[] = [{ key: 'a', label: 'А', cell: r => <input value={r.a} /> }];`)).toBe(0);
  });

  it('отказ называет, куда идти', () => {
    expect(RULES.find(r => r.id === 'cell-field').instead).toContain('density="cell"');
  });
});
