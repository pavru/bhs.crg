// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { useState } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { EditGrid, type EditGridColumn } from './EditGrid';

afterEach(cleanup);

interface Line { key: string; name: string; unit: string; quantity: string }

let counter = 0;
const line = (name = ''): Line => ({ key: `k${++counter}`, name, unit: '', quantity: '' });

const COLUMNS = (edit: (key: string, patch: Partial<Line>) => void): EditGridColumn<Line>[] => [
  {
    key: 'name', label: 'Наименование',
    cell: (l, i) => <input aria-label={`Наименование, строка ${i + 1}`} value={l.name}
      onChange={e => edit(l.key, { name: e.target.value })} />,
    total: () => 'Итого',
  },
  // Колонка без поля: стрелка её перешагивает.
  { key: 'unit', label: 'Ед.', cell: l => l.unit || '—' },
  {
    key: 'quantity', label: 'Кол-во', align: 'right',
    cell: (l, i) => <input aria-label={`Количество, строка ${i + 1}`} value={l.quantity}
      onChange={e => edit(l.key, { quantity: e.target.value })} />,
  },
];

function Draft({ initial, readOnly = false }: { initial: Line[]; readOnly?: boolean }) {
  const [rows, setRows] = useState(initial);
  const edit = (key: string, patch: Partial<Line>) => setRows(prev => prev.map(r => (r.key === key ? { ...r, ...patch } : r)));
  return (
    <EditGrid label="Строки" numbered rows={rows} rowKey={r => r.key} columns={COLUMNS(edit)}
      add={readOnly ? undefined : { label: 'Добавить строку', onAdd: () => setRows(prev => [...prev, line()]) }}
      onRemove={readOnly ? undefined : r => setRows(prev => prev.filter(x => x.key !== r.key))} />
  );
}

const field = (name: string) => screen.getByLabelText(name) as HTMLInputElement;

describe('EditGrid (#1354)', () => {
  it('добавленная строка получает фокус в первом поле', () => {
    render(<Draft initial={[line('Кабель')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));
    expect(document.activeElement).toBe(field('Наименование, строка 2'));
  });

  it('после удаления фокус встаёт на удаление соседней строки, а с последней — на «добавить»', () => {
    render(<Draft initial={[line('А'), line('Б')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 1' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Удалить строку 1' }));
    expect(field('Наименование, строка 1').value).toBe('Б');
    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 1' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Добавить строку' }));
  });

  it('без строк говорит «Строк нет», и «добавить» остаётся на месте', () => {
    render(<Draft initial={[]} />);
    expect(screen.getByText('Строк нет')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Добавить строку' })).toBeTruthy();
  });

  it('без права правки нет ни «добавить», ни колонки удаления', () => {
    render(<Draft initial={[line('А')]} readOnly />);
    expect(screen.queryByRole('button', { name: 'Добавить строку' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Удалить/ })).toBeNull();
    expect(screen.getAllByRole('columnheader').map(th => th.textContent)).toEqual(['№', 'Наименование', 'Ед.', 'Кол-во']);
  });

  it('↑↓ ведут по той же колонке соседних строк', () => {
    render(<Draft initial={[line('А'), line('Б')]} />);
    field('Количество, строка 1').focus();
    fireEvent.keyDown(field('Количество, строка 1'), { key: 'ArrowDown' });
    expect(document.activeElement).toBe(field('Количество, строка 2'));
    fireEvent.keyDown(field('Количество, строка 2'), { key: 'ArrowUp' });
    expect(document.activeElement).toBe(field('Количество, строка 1'));
  });

  it('→ с конца текста ведёт к следующему полю, перешагивая клетку без поля', () => {
    render(<Draft initial={[line('Кабель')]} />);
    const name = field('Наименование, строка 1');
    name.focus();
    name.setSelectionRange(2, 2);
    fireEvent.keyDown(name, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(name);

    name.setSelectionRange(name.value.length, name.value.length);
    fireEvent.keyDown(name, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(field('Количество, строка 1'));
  });

  it('стрелки выпадающего списка сетка не трогает: ими листают пункты', () => {
    render(
      <EditGrid rows={[line(), line()]} rowKey={r => r.key} columns={[{
        key: 'pick', label: 'Выбор', cell: (_, i) => <button type="button" role="combobox" aria-label={`Выбор ${i + 1}`} />,
      }]} />,
    );
    const first = screen.getByRole('combobox', { name: 'Выбор 1' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(first);
  });

  it('итоговая строка есть, когда итог есть хоть у одной колонки', () => {
    render(<Draft initial={[line('А')]} />);
    const foot = document.querySelector('tfoot');
    expect(foot?.textContent).toBe('Итого');
  });

  it('атрибуты строки — якорь, к которому ведёт ссылка из перечня', () => {
    render(<EditGrid rows={[line('А')]} rowKey={r => r.key} rowData={(_, i) => ({ 'data-readiness': `line-${i + 1}` })}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    expect(document.querySelector('[data-readiness="line-1"]')?.textContent).toBe('А');
  });
});
