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

  it('тон строки — заливкой; у открытой строки её перекрывает фон открытой (secondary-container)', () => {
    grid([{ key: 'name', label: 'Имя' }], { tone: () => 'warning', rowOpen: p => p.id === '2' });
    const [, anna, boris] = screen.getAllByRole('row');
    expect(anna.className).toContain('[--dt-row-bg:var(--color-warning-container)]');
    expect(anna.className).toContain('[--dt-row-fg:var(--color-on-warning-container)]');
    expect(boris.className).not.toContain('warning-container');
    expect(boris.className).toContain('[--dt-row-bg:var(--color-secondary-container)]');
    expect(boris.className).toContain('[--dt-row-fg:var(--color-on-secondary-container)]');
    // Прежняя подсветка открытой строки — primary-container — ушла (вариант А).
    expect(boris.className).not.toContain('primary-container');
  });

  it('обычная строка без фона — под ней видна поверхность, на которой стоит таблица', () => {
    grid([{ key: 'name', label: 'Имя' }]);
    const [, anna] = screen.getAllByRole('row');
    expect(anna.className).toContain('[--dt-row-bg:initial]');
    expect(anna.className).toContain('bg-[color:var(--dt-row-bg,transparent)]');
    expect(anna.className).not.toContain('--dt-row-bg:var(--dt-bg)');
  });

  it('клетка цвет текста не задаёт — он у строки, иначе открытая не стала бы on-secondary-container', () => {
    grid([{ key: 'name', label: 'Имя' }], { rowOpen: () => true });
    const [, anna] = screen.getAllByRole('row');
    expect(anna.className).toContain('text-(--dt-row-fg)');
    expect(anna.querySelector('td')!.className).not.toContain('text-on-surface');
  });

  it('плотность — переменными на обёртке с прокруткой: страница 56 / 48, compact 48 / 40', () => {
    grid([{ key: 'name', label: 'Имя' }]);
    const page = screen.getByRole('table').parentElement!;
    expect(page.className).toContain('[--dt-head:56px]');
    expect(page.className).toContain('[--dt-row:48px]');
    cleanup();
    grid([{ key: 'name', label: 'Имя' }], { density: 'compact' });
    const dialog = screen.getByRole('table').parentElement!;
    expect(dialog.className).toContain('[--dt-head:48px]');
    expect(dialog.className).toContain('[--dt-row:40px]');
    expect(dialog.className).not.toContain('56px');
    // Высота — от переменной: у клетки минимум строки, у шапки — её высота; чисел в клетках нет.
    expect(screen.getByRole('columnheader', { name: 'Имя' }).className).toContain('h-(--dt-head)');
    expect(screen.getAllByRole('row')[1].querySelector('td')!.className).toContain('h-(--dt-row)');
  });

  it('отступы прокрутки — по высоте шапки и итога: двухэтажная шапка и итог в две строки не закрывают строку', () => {
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.tagName === 'THEAD' ? 112 : this.tagName === 'TFOOT' ? 60 : 0;
    });
    grid([{ key: 'name', label: 'Имя', group: { key: 'person', label: 'Человек' } }, { key: 'note', label: 'Заметка' }],
      { footer: () => 'Итого' });
    const scroller = screen.getByRole('table').parentElement!;
    expect(scroller.style.scrollPaddingTop).toBe('112px');
    expect(scroller.style.scrollPaddingBottom).toBe('60px');
    vi.restoreAllMocks();
  });

  it('шапка без своей заливки: фон контейнера, над прокрученным телом — surface-container; подпись title-small', () => {
    grid([{ key: 'name', label: 'Имя' }]);
    const head = screen.getByRole('columnheader', { name: 'Имя' });
    expect(head.className).toContain('bg-(--dt-bg)');
    expect(head.className).toContain('group-data-[scrolled]/dt:bg-surface-container');
    expect(head.className).toContain('text-title-small');
    expect(head.className).not.toMatch(/(^|\s)bg-surface-container(\s|$)/);
    const scroller = screen.getByRole('table').parentElement!;
    scroller.scrollTop = 30;
    fireEvent.scroll(scroller);
    expect(scroller.hasAttribute('data-scrolled')).toBe(true);
    scroller.scrollTop = 0;
    fireEvent.scroll(scroller);
    expect(scroller.hasAttribute('data-scrolled')).toBe(false);
  });

  it('итог — surface-container без черты над ним, подпись label-large', () => {
    grid([{ key: 'name', label: 'Имя' }], { footer: () => 'Итого' });
    const total = screen.getByText('Итого');
    expect(total.className).toContain('bg-surface-container');
    expect(total.className).toContain('text-label-large');
    expect(total.className).not.toContain('border-t');
  });

  it('прижатая к верху строка держит однострочный текст на линии строки, а не у верхней черты', () => {
    grid([{ key: 'name', label: 'Имя' }, { key: 'note', label: 'Заметка', wrap: true }]);
    const name = screen.getAllByRole('row')[1].querySelector('td')!;
    expect(name.firstElementChild!.className).toContain('min-h-[calc(var(--dt-row)-2*var(--spacing)-1px)]');
    expect(name.firstElementChild!.className).toContain('items-center');
    cleanup();
    // Без переноса клетки по центру сами — лишней обёртки нет.
    grid([{ key: 'name', label: 'Имя' }]);
    expect(screen.getAllByRole('row')[1].querySelector('td')!.firstElementChild).toBeNull();
  });

  it('шапка рисует своё, а имя колонки остаётся подписью', () => {
    grid([{ key: 'name', label: 'Имя', header: <>Имя <span data-testid="count">2 из 4</span></> }]);
    expect(screen.getByTestId('count').textContent).toBe('2 из 4');
  });

  it('фиксированная раскладка — table-fixed на самой таблице', () => {
    grid([{ key: 'name', label: 'Имя' }], { fixed: true });
    expect(screen.getByRole('table').className).toContain('table-fixed');
  });

  it('фиксированная раскладка: таблица не уже суммы колонок — наименование не сжимается в ноль на телефоне', () => {
    grid([{ key: 'code', label: 'Шифр', width: 'w-36' }, { key: 'name', label: 'Имя' }], { fixed: true });
    expect(screen.getByRole('table').style.minWidth).toBe('calc(var(--spacing) * 84)');
  });

  it('обычная раскладка наименьшей ширины не ставит: колонки растут по содержимому', () => {
    grid([{ key: 'code', label: 'Шифр', width: 'w-36' }, { key: 'name', label: 'Имя' }]);
    expect(screen.getByRole('table').style.minWidth).toBe('');
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

  it('закреплённая клетка — фон строки и её слой состояния, а не своя подложка', () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    grid([{ key: 'name', label: 'Имя' }, { key: 'note', label: 'Заметка' }], { pinned: 1, rowOpen: () => true });
    const [pinned, free] = screen.getAllByRole('row')[1].querySelectorAll('td');
    expect(pinned.className).toContain('bg-[color:var(--dt-row-bg,var(--dt-bg))]');
    expect(pinned.className).toContain('[background-image:inherit]');
    expect(pinned.className).not.toMatch(/bg-(surface|primary-container|secondary-container)\b/);
    expect(free.className).not.toContain('--dt-row-bg');
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
    // Подпись над группой, а не строка данных: высоты строки не держит.
    expect(heading.className).not.toContain('h-(--dt-row)');
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

  it('перетаскивание: тащимая строка — слоем перетаскивания, место под последней отмечено чертой', () => {
    const drop = vi.fn();
    grid([{ key: 'name', label: 'Имя' }], { drag: { dragging: 0, target: 2, onOver: () => {}, onDrop: drop } });
    const [, anna, boris] = screen.getAllByRole('row');
    // Слой 16 % ставит state-layer по data-dragging ([UI-64]), а не прозрачность строки.
    expect(anna.dataset.dragging).toBe('true');
    expect(anna.className).toContain('state-layer');
    expect(anna.className).not.toContain('opacity-');
    expect(boris.dataset.dragging).toBeUndefined();
    expect(anna.className).not.toContain('drop-mark-');
    expect(boris.className).toContain('drop-mark-after');
    fireEvent.drop(boris);
    expect(drop).toHaveBeenCalledTimes(1);
  });
});
