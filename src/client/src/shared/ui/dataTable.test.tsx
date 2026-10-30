// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import * as Popover from '@radix-ui/react-popover';
import { MemoryRouter } from 'react-router';
import { IconButton } from './Button';
import { DataGrid } from './DataGrid';
import { EditGrid } from './EditGrid';
import { Close, MoreVert } from './icons';

// Размер кнопки-иконки задаёт место ([UI-68], В7 = Б): клетка ставит переменную, а слой, открытый из
// клетки, лежит в body и её не наследует. jsdom не считает CSS — проверяется, что переменная стоит у
// клетки, а слой — вне клетки.
afterEach(cleanup);

const XS = '[--md-comp-icon-button-size:32px]';
const COARSE = 'pointer-coarse:[--md-comp-icon-button-size:40px]';
type Row = { id: string };
const ROWS: Row[] = [{ id: 'a' }];

describe('кнопка-иконка в клетке таблицы', () => {
  it('клетка DataGrid ставит размер XS, при касании — 40', () => {
    render(<MemoryRouter><DataGrid rows={ROWS} rowKey={r => r.id}
      columns={[{ key: 'x', label: 'Действия', cell: () => <IconButton label="Ещё" icon={MoreVert} /> }]} /></MemoryRouter>);
    const cell = screen.getByRole('button', { name: 'Ещё' }).closest('td')!;
    expect(cell.className.split(/\s+/)).toEqual(expect.arrayContaining([XS, COARSE]));
  });

  it('клетка EditGrid — тоже, и крестик удаления строки — IconButton в ней', () => {
    render(<EditGrid label="Строки" rows={ROWS} rowKey={r => r.id} onRemove={() => {}}
      columns={[{ key: 'x', label: 'Поле', cell: () => 'значение' }]} />);
    const remove = screen.getByRole('button', { name: 'Удалить строку 1' });
    expect(remove.className).toContain('size-[var(--md-comp-icon-button-size,40px)]');
    expect(remove.closest('td')!.className.split(/\s+/)).toEqual(expect.arrayContaining([XS, COARSE]));
  });

  it('слой, открытый из клетки, — вне клетки: переменная на его кнопки не переходит', () => {
    render(<MemoryRouter><DataGrid rows={ROWS} rowKey={r => r.id}
      columns={[{ key: 'x', label: 'Действия', cell: () => (
        <Popover.Root open>
          <Popover.Trigger asChild><IconButton label="Открыть" icon={MoreVert} /></Popover.Trigger>
          <Popover.Portal><Popover.Content aria-label="Слой"><IconButton label="Закрыть слой" icon={Close} /></Popover.Content></Popover.Portal>
        </Popover.Root>
      ) }]} /></MemoryRouter>);
    expect(screen.getByRole('button', { name: 'Закрыть слой' }).closest('td')).toBeNull();
  });
});
