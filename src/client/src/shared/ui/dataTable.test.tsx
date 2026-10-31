// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import * as Popover from '@radix-ui/react-popover';
import { MemoryRouter } from 'react-router';
import { IconButton } from './Button';
import { DataGrid } from './DataGrid';
import { EditGrid } from './EditGrid';
import { Close, MoreVert } from './icons';
import { TextField } from './TextField';
import { SourceBindingTrigger } from '@/features/document-sets/editor/SourceBindingTrigger';

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

// Второе место XS — кнопка внутри поля (design.md «Кнопка внутри поля»): линза в углу поля реквизита при 40
// накрывала кнопку календаря поля даты. Крестик `DateInput` — в DateInput.clear.test.tsx.
describe('кнопка-иконка внутри поля', () => {
  it('линза привязки реквизита — XS, при касании 40', () => {
    render(<SourceBindingTrigger locked={false} isBound={false} onOpen={() => {}} />);
    const lens = screen.getByRole('button', { name: 'Привязать к источнику' });
    expect(lens.className.split(/\s+/)).toEqual(expect.arrayContaining([XS, COARSE]));
  });

  // Хвост поля ставит класс места сам (ревью PR 1708): «Показать пароль» при 40 заходила на конец текста.
  it('хвост TextField — место XS, отступ текста под кнопку при касании шире', () => {
    render(<TextField label="Пароль" trailing={<IconButton label="Показать пароль" icon={Close} />} />);
    // Между кнопкой и слотом — обёртка `contents`, пропускающая щелчки (sharedui-md3-11): класс места — на слоте.
    const holder = screen.getByRole('button', { name: 'Показать пароль' }).closest('div')!;
    expect(holder.className.split(/\s+/)).toEqual(expect.arrayContaining([XS, COARSE]));
    // Отступ — под хвост и место знака ошибки (`--field-mark`, без ошибки ноль): 44 и 52.
    expect(screen.getByLabelText('Пароль').className.split(/\s+/)).toEqual(expect.arrayContaining([
      'pr-[calc(2.75rem+var(--field-mark,0px))]', 'pointer-coarse:pr-[calc(3.25rem+var(--field-mark,0px))]']));
  });
});
