// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { useState } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { EditGrid, type EditGridColumn } from './EditGrid';
import { Select, SelectItem } from './Select';

// Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
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

/** Поле со списком, который открывается вводом и закрывается Esc, — стрелок само не гасит: проверяется
 *  именно условие сетки «уступать только при aria-expanded="true"». */
function SuggestCell({ label }: { label: string }) {
  const [value, setValue] = useState('');
  const [open, setOpen] = useState(false);
  return (
    <input role="combobox" aria-label={label} aria-expanded={open} value={value}
      onChange={e => { setValue(e.target.value); setOpen(e.target.value !== ''); }}
      onKeyDown={e => { if (e.key === 'Escape') setOpen(false); }} />
  );
}

function SuggestDraft() {
  return (
    <EditGrid rows={[line(), line(), line()]} rowKey={r => r.key}
      columns={[{ key: 'mark', label: 'Марка', cell: (_, i) => <SuggestCell label={`Марка, строка ${i + 1}`} /> }]} />
  );
}

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

  it('стрелки, которые триггер списка забрал себе, сетка не трогает', () => {
    // Так ведёт себя триггер Radix Select: открывается на ↓ и сам зовёт preventDefault.
    render(
      <EditGrid rows={[line(), line()]} rowKey={r => r.key} columns={[{
        key: 'pick', label: 'Выбор', cell: (_, i) => (
          <button type="button" role="combobox" aria-expanded="false" aria-label={`Выбор ${i + 1}`}
            onKeyDown={e => { if (e.key === 'ArrowDown') e.preventDefault(); }} />
        ),
      }]} />,
    );
    const first = screen.getByRole('combobox', { name: 'Выбор 1' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(first);
  });

  it('ячейка с живым Select открывается на ↓, фокус остаётся в ней', () => {
    render(
      <EditGrid rows={[line(), line()]} rowKey={r => r.key} columns={[{
        key: 'unit', label: 'Ед.', cell: (_, i) => (
          <Select aria-label={`Ед., строка ${i + 1}`} value="" onValueChange={() => {}} emptyItem="—">
            <SelectItem value="m">м</SelectItem>
          </Select>
        ),
      }]} />,
    );
    const first = screen.getByRole('combobox', { name: 'Ед., строка 1' });
    const second = screen.getByRole('combobox', { name: 'Ед., строка 2' });
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    // Открытый Select прячет остальное от читалки (aria-hidden), поэтому дальше — по ссылкам.
    expect(screen.getByRole('listbox')).toBeTruthy();
    expect(first.getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).not.toBe(second);
  });

  it('текстовая ячейка со списком: закрытый список стрелок не забирает, открытый — забирает', () => {
    render(<SuggestDraft />);
    const first = field('Марка, строка 1');
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(field('Марка, строка 2'));

    const second = field('Марка, строка 2');
    fireEvent.change(second, { target: { value: 'ВВГ' } });
    expect(second.getAttribute('aria-expanded')).toBe('true');
    fireEvent.keyDown(second, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(second);

    fireEvent.keyDown(second, { key: 'Escape' });
    expect(second.getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(second, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(field('Марка, строка 3'));
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

  it('закреплённые колонки липнут к своим краям, название строки — заголовок строки', () => {
    render(<EditGrid rows={[line('А')]} rowKey={r => r.key} columns={[
      { key: 'name', label: 'Строка', rowHeader: true, pin: 'start', cell: r => r.name },
      { key: 'mid', label: 'Объект', cell: () => '1' },
      { key: 'rest', label: 'Остаток', pin: 'end', cell: () => '0' },
    ]} />);
    const head = screen.getByRole('rowheader', { name: 'А' });
    expect(head.className).toContain('sticky');
    expect(head.style.left).toBe('0px');
    const rest = screen.getAllByRole('cell').at(-1)!;
    expect(rest.style.right).toBe('0px');
    expect(screen.getByRole('columnheader', { name: 'Остаток' }).style.right).toBe('0px');
  });

  it('ширина в пикселях делает раскладку фиксированной', () => {
    render(<EditGrid rows={[line('А')]} rowKey={r => r.key}
      columns={[{ key: 'name', label: 'Имя', size: 120, cell: r => r.name }]} />);
    expect(screen.getByRole('table').className).toContain('table-fixed');
    expect(screen.getByRole('columnheader', { name: 'Имя' }).style.width).toBe('120px');
  });

  it('выбранная строка подсвечена', () => {
    const rows = [line('А'), line('Б')];
    render(<EditGrid rows={rows} rowKey={r => r.key} selected={r => r.name === 'Б'}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    const [, a, b] = screen.getAllByRole('row');
    expect(a.className).not.toContain('bg-brand-subtle');
    expect(b.className).toContain('bg-brand-subtle');
  });
});
