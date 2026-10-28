// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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

  it('тон строки — заливкой; у открытой строки её перекрывает подсветка открытия', () => {
    grid([{ key: 'name', label: 'Имя' }], { tone: () => 'warning', rowOpen: p => p.id === '2' });
    const [, anna, boris] = screen.getAllByRole('row');
    expect(anna.className).toContain('bg-warning-container');
    expect(boris.className).not.toContain('bg-warning-container');
    expect(boris.className).toContain('bg-primary-container');
  });

  it('шапка рисует своё, а имя колонки остаётся подписью', () => {
    grid([{ key: 'name', label: 'Имя', header: <>Имя <span data-testid="count">2 из 4</span></> }]);
    expect(screen.getByTestId('count').textContent).toBe('2 из 4');
  });

  it('фиксированная раскладка — table-fixed на самой таблице', () => {
    grid([{ key: 'name', label: 'Имя' }], { fixed: true });
    expect(screen.getByRole('table').className).toContain('table-fixed');
  });
});

describe('DataGrid: особые строки и шапка (#1354, 3/3)', () => {
  it('двухэтажная шапка: группа над своими колонками, колонка без группы — на оба этажа', () => {
    grid([
      { key: 'id', label: 'Код' },
      { key: 'name', label: 'Имя', group: { key: 'p', label: 'Человек' } },
      { key: 'note', label: 'Заметка', group: { key: 'p', label: 'Человек' } },
    ]);
    const [upper, lower] = screen.getAllByRole('row');
    const top = upper.querySelectorAll('th');
    expect([...top].map(th => th.textContent)).toEqual(['Код', 'Человек']);
    expect(top[0].getAttribute('rowspan')).toBe('2');
    expect(top[1].getAttribute('colspan')).toBe('2');
    expect([...lower.querySelectorAll('th')].map(th => th.textContent)).toEqual(['Имя', 'Заметка']);
    // Тело — в порядке колонок, а не этажей.
    expect([...screen.getAllByRole('row')[2].querySelectorAll('td')].map(td => td.textContent))
      .toEqual(['1', 'Анна', 'длинное пояснение']);
  });

  it('закреплённая колонка в группе закрепляет и шапку группы; второй этаж липнет под первым', () => {
    // В jsdom нет ResizeObserver, а закрепление меряет шапку им. Ширин здесь нет — проверяем стили.
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    grid([
      { key: 'name', label: 'Имя', group: { key: 'p', label: 'Человек' } },
      { key: 'note', label: 'Заметка' },
    ], { pinned: 1 });
    const group = screen.getByRole('columnheader', { name: 'Человек' });
    expect(group.className).toContain('sticky');
    expect(group.style.left).toBe('var(--pin-0, 0px)');
    expect(screen.getByRole('columnheader', { name: 'Имя' }).style.top).toBe('var(--dt-head)');
    vi.unstubAllGlobals();
  });

  it('открытая строка — aria-current, а не aria-selected', () => {
    grid([{ key: 'name', label: 'Имя' }], { onRowOpen: () => {}, rowOpen: p => p.id === '1' });
    const [, anna, boris] = screen.getAllByRole('row');
    expect(anna.getAttribute('aria-current')).toBe('true');
    expect(boris.hasAttribute('aria-current')).toBe(false);
    expect(anna.hasAttribute('aria-selected')).toBe(false);
  });

  it('заголовок строки — th scope="row", заголовок группы — одна клетка во всю ширину', () => {
    grid([{ key: 'name', label: 'Имя', rowHeader: true }, { key: 'note', label: 'Заметка' }],
      { section: p => (p.id === '1' ? 'Первые' : null) });
    const boris = screen.getByRole('rowheader', { name: 'Борис' });
    expect(boris.tagName).toBe('TH');
    const heading = screen.getByText('Первые');
    expect(heading.getAttribute('scope')).toBe('rowgroup');
    expect(heading.getAttribute('colspan')).toBe('2');
    expect(screen.queryByText('Анна')).toBeNull();
  });

  it('щелчок в клетке с действиями строку не открывает, в обычной — открывает', () => {
    const open = vi.fn();
    grid([
      { key: 'name', label: 'Имя' },
      { key: 'act', label: '', interactive: true, cell: p => <button type="button">Меню {p.name}</button> },
    ], { onRowOpen: open });
    fireEvent.click(screen.getByRole('button', { name: 'Меню Анна' }));
    expect(open).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Анна'));
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('перетаскивание: тащимая строка приглушена, место под последней отмечено чертой', () => {
    const drop = vi.fn();
    grid([{ key: 'name', label: 'Имя' }], { drag: { dragging: 0, target: 2, onOver: () => {}, onDrop: drop } });
    const [, anna, boris] = screen.getAllByRole('row');
    expect(anna.className).toContain('opacity-40');
    expect(anna.style.boxShadow).toBe('');
    expect(boris.style.boxShadow).toContain('inset 0 -2px');
    fireEvent.drop(boris);
    expect(drop).toHaveBeenCalledTimes(1);
  });
});
