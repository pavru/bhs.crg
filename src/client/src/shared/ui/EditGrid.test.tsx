// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { EditGrid, type EditGridColumn } from './EditGrid';
import { Select, SelectItem } from './Select';
import { TextField } from './TextField';

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

/** Экран, который сперва спрашивает (#1655): корзина ставит вопрос, строку убирает «Да», «Нет» её оставляет. */
function Asking({ initial }: { initial: Line[] }) {
  const [rows, setRows] = useState(initial);
  const [asked, setAsked] = useState<string | null>(null);
  return (
    <>
      <EditGrid label="Строки" numbered rows={rows} rowKey={r => r.key} columns={COLUMNS(() => {})}
        add={{ label: 'Добавить строку', onAdd: () => setRows(prev => [...prev, line()]) }}
        onRemove={r => setAsked(r.key)} />
      <button type="button" onClick={() => { setRows(prev => prev.filter(x => x.key !== asked)); setAsked(null); }}>Да</button>
      <button type="button" onClick={() => setAsked(null)}>Нет</button>
      <button type="button" onClick={() => setRows(prev => [...prev, line('Вставлено')])}>Вставить</button>
    </>
  );
}

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

  it('после удаления фокус — на поле строки, вставшей на место, а не на её корзину; с последней — на «добавить» (D2 #1655)', () => {
    render(<Draft initial={[line('А'), line('Б')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 1' }));
    // Enter, Enter больше не удаляет две строки: второй Enter приходится в поле, а не в корзину соседней строки.
    expect(document.activeElement).toBe(field('Наименование, строка 1'));
    expect(field('Наименование, строка 1').value).toBe('Б');
    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 1' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Добавить строку' }));
  });

  it('экран спросил: фокус переходит, только когда строка ушла; отказ и строки, пришедшие потом, его не трогают (#1655)', () => {
    render(<Asking initial={[line('А'), line('Б'), line('В')]} />);
    const bin = screen.getByRole('button', { name: 'Удалить строку 2' });
    fireEvent.click(bin);
    fireEvent.click(screen.getByRole('button', { name: 'Нет' }));
    // Отказ: строк столько же, а стало больше — от вставки, не от удаления. Фокус туда, где был.
    screen.getByRole('button', { name: 'Вставить' }).focus();
    fireEvent.click(screen.getByRole('button', { name: 'Вставить' }));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Вставить' }));

    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Да' }));
    expect(document.activeElement).toBe(field('Наименование, строка 2'));
    expect(field('Наименование, строка 2').value).toBe('В');
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

  it('ячейка с живым Select открывается на ↓, фокус остаётся в ней', async () => {
    render(
      <EditGrid rows={[line(), line()]} rowKey={r => r.key} columns={[{
        key: 'unit', label: 'Ед.', cell: (_, i) => (
          <Select variant="inline" aria-label={`Ед., строка ${i + 1}`} value="" onValueChange={() => {}} emptyItem="—">
            <SelectItem value="m">м</SelectItem>
          </Select>
        ),
      }]} />,
    );
    const first = screen.getByRole('combobox', { name: 'Ед., строка 1' });
    const second = screen.getByRole('combobox', { name: 'Ед., строка 2' });
    const moved = vi.spyOn(second, 'focus');
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    // Открытый Select прячет остальное от читалки (aria-hidden), поэтому дальше — по ссылкам.
    const options = screen.getByRole('listbox');
    expect(first.getAttribute('aria-expanded')).toBe('true');
    // Сетка клавишу не тронула: на строку ниже она не переводила, фокус — в открытом выборе этой клетки.
    expect(moved).not.toHaveBeenCalled();
    await waitFor(() => expect(options.contains(document.activeElement)).toBe(true));
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

  it('шапка по шкале D7 — подпись title-small; строка не ниже 48, у листа — 40 (В5)', () => {
    const { container, rerender } = render(<EditGrid rows={[line('А')]} rowKey={r => r.key}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    const head = screen.getByRole('columnheader', { name: 'Имя' });
    expect(head.className).toContain('text-title-small');
    expect(head.className).not.toContain('text-label-medium');
    expect(container.querySelector('table')!.parentElement!.className).toContain('[--dt-row:48px]');
    rerender(<EditGrid sheet rows={[line('А')]} rowKey={r => r.key}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    expect(container.querySelector('table')!.parentElement!.className).toContain('[--dt-row:40px]');
    expect(screen.getByRole('cell', { name: 'А' }).className).toContain('h-(--dt-row)');
  });

  it('клетка листа в фокусе не заливается: фокус показывает черта поля', () => {
    render(<EditGrid sheet rows={[line('А')]} rowKey={r => r.key}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    expect(screen.getByRole('cell', { name: 'А' }).className).not.toContain('bg-primary-container');
  });

  it('поле тела берёт размер клетки, поле шапки — нет', () => {
    const { container } = render(<EditGrid rows={[line('А')]} rowKey={r => r.key} columns={[{
      key: 'name', label: 'Имя', header: <TextField label="Имя в шапке" density="dense" />,
      cell: (l, i) => <TextField label={`Имя, строка ${i + 1}`} value={l.name} onChange={() => {}} />,
    }]} />);
    const body = screen.getByLabelText('Имя, строка 1');
    expect(body.className).toContain('pl-2');
    expect(body.closest('td')!.querySelector('fieldset')).toBeNull();
    expect(screen.getByLabelText('Имя в шапке').closest('th')!.querySelector('fieldset')).not.toBeNull();
    expect(container.querySelectorAll('fieldset')).toHaveLength(1);
  });

  it('колонку удаления назначает сетка — 48 под кнопку 32 с отступами', () => {
    render(<Draft initial={[line('А')]} />);
    const remove = screen.getByRole('columnheader', { name: 'Удалить' });
    expect(remove.className).toContain('w-12');
    expect(remove.className).not.toContain('w-8');
    // Кнопка — по центру строки линией клетки, как поля: `align-middle` поверх `align-top` клетки не действует.
    const button = screen.getByRole('button', { name: 'Удалить строку 1' });
    expect(button.closest('td')!.className).not.toContain('align-middle');
    expect(button.parentElement!.parentElement!.className).toContain('items-center');
  });

  it('сетка в клетке другой сетки: размер места получает только тело, шапка — нет', () => {
    const inner = <EditGrid label="Внутренняя" rows={[line('Б')]} rowKey={r => r.key} columns={[{
      key: 'name', label: 'Имя', header: <TextField label="Шапка внутренней" />,
      cell: (l, i) => <TextField label={`Внутри, строка ${i + 1}`} value={l.name} onChange={() => {}} />,
    }]} />;
    render(<EditGrid rows={[line('А')]} rowKey={r => r.key} columns={[{ key: 'name', label: 'Имя', cell: () => inner }]} />);
    expect(screen.getByLabelText('Шапка внутренней').closest('th')!.querySelector('fieldset')).not.toBeNull();
    expect(screen.getByLabelText('Внутри, строка 1').closest('td')!.querySelector('fieldset')).toBeNull();
  });

  it('итог — surface-container без черты над ним', () => {
    render(<Draft initial={[line('А')]} />);
    const total = document.querySelector('tfoot td')!;
    expect(total.className).toContain('bg-surface-container');
    expect(total.className).not.toContain('border-t');
  });

  it('выбранная строка подсвечена фоном открытой строки — secondary-container, как в DataGrid', () => {
    const rows = [line('А'), line('Б')];
    render(<EditGrid rows={rows} rowKey={r => r.key} selected={r => r.name === 'Б'}
      columns={[{ key: 'name', label: 'Имя', cell: r => r.name }]} />);
    const [, a, b] = screen.getAllByRole('row');
    expect(a.className).not.toContain('secondary-container');
    expect(b.className).toContain('[--dt-row-bg:var(--color-secondary-container)]');
    expect(b.className).not.toContain('--color-primary-container');
    expect(b.className).not.toContain('bg-primary-container');
  });
});
