// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { DataGrid, type DataGridColumn } from './DataGrid';
import { DATA_GRID_STATES } from './dataGridStates';

afterEach(cleanup);

interface Person { id: string; name: string; note: string; strong: boolean }

const PEOPLE: Person[] = [
  { id: '1', name: 'Анна', note: 'длинное пояснение', strong: true },
  { id: '2', name: 'Борис', note: '', strong: false },
];

const grid = (columns: DataGridColumn<Person>[], extra: Partial<Parameters<typeof DataGrid<Person>>[0]> = {}) =>
  render(<MemoryRouter><DataGrid columns={columns} rows={PEOPLE} rowKey={p => p.id} {...extra} /></MemoryRouter>);

describe('DataGrid: клетка по строке (#1354)', () => {
  it('рисует клетку из строки целиком, а не из значения поля', () => {
    grid([{ key: 'name', label: 'Имя', cell: p => <b>{p.name}!</b> }]);
    expect(screen.getByText('Анна!').tagName).toBe('B');
  });

  it('у закрытой колонки клетку не зовёт: колонка называет причину', () => {
    const cell = vi.fn(() => 'утечка');
    grid([{ key: 'salary', label: 'Оклад', unavailable: 'no-right', cell }]);
    expect(cell).not.toHaveBeenCalled();
    expect(screen.getAllByText(DATA_GRID_STATES['no-right'].title)).toHaveLength(2);
  });

  it('перенос у одной колонки прижимает к верху клетки всей строки', () => {
    grid([{ key: 'name', label: 'Имя' }, { key: 'note', label: 'Заметка', wrap: true }]);
    const [name, note] = screen.getAllByRole('row')[1].querySelectorAll('td');
    expect(note.className).toContain('break-words');
    expect(name.className).toContain('whitespace-nowrap');
    expect(name.className).toContain('align-top');
  });

  it('выделяет строку начертанием, когда экран так сказал', () => {
    grid([{ key: 'name', label: 'Имя' }], { emphasized: p => p.strong });
    const [, anna, boris] = screen.getAllByRole('row');
    expect(anna.className).toContain('font-medium');
    expect(boris.className).not.toContain('font-medium');
  });

  it('ширина колонки — классом на шапке', () => {
    grid([{ key: 'name', label: 'Имя', width: 'w-36' }]);
    expect(screen.getByRole('columnheader', { name: 'Имя' }).className).toContain('w-36');
  });
});
