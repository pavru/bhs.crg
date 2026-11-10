// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { DataGrid, type DataGridColumn } from './DataGrid';
import { EditGrid } from './EditGrid';
import { Modal } from './Modal';
import { Tooltip } from './Tooltip';

/**
 * Полный текст обрезанной клетки ([UI-52] [UI-53], #1519): сетка показывает его сама, только у обрезанной,
 * не перерисовываясь, не отнимая щелчка у строки, одной подсказкой.
 */

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

interface Item { id: string; name: string }
const LONG = 'Кабель силовой ВВГнг(А)-LS 3×2,5 ок(N, PE)-0,66 ГОСТ 31996-2012';
const ITEMS: Item[] = [{ id: '1', name: LONG }, { id: '2', name: 'Короб' }];

/** Обрезка в тестовом DOM: многоточие стилем, размеры — «шире места» (или впору). */
const CLIP = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } as const;
function sizes(el: HTMLElement, scroll: number, client: number) {
  Object.defineProperty(el, 'scrollWidth', { configurable: true, value: scroll });
  Object.defineProperty(el, 'clientWidth', { configurable: true, value: client });
}
const tip = () => document.querySelector('[data-truncated-tip]');
const hover = async (el: Element, init: PointerEventInit = {}) => {
  fireEvent.pointerMove(el, { pointerType: 'mouse', buttons: 0, ...init });
  await act(async () => { vi.advanceTimersByTime(450); });
};
const nameCell = (onRender?: () => void): DataGridColumn<Item> => ({
  key: 'name', label: 'Наименование',
  cell: i => { onRender?.(); return <span data-testid={`name-${i.id}`} style={CLIP}>{i.name}</span>; },
});
const grid = (columns: DataGridColumn<Item>[], extra: Partial<Parameters<typeof DataGrid<Item>>[0]> = {}) =>
  render(<MemoryRouter><DataGrid columns={columns} rows={ITEMS} rowKey={i => i.id} {...extra} /></MemoryRouter>);

describe('DataGrid: полный текст обрезанной клетки', () => {
  it('обрезанная клетка — показ полного текста после задержки; диктору ничего не добавлено', async () => {
    grid([nameCell()]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    fireEvent.pointerMove(name, { pointerType: 'mouse', buttons: 0 });
    expect(tip()).toBeNull();
    await act(async () => { vi.advanceTimersByTime(450); });
    expect(tip()?.textContent).toBe(LONG);
    expect(tip()?.getAttribute('aria-hidden')).toBe('true');
    expect(name.hasAttribute('aria-description')).toBe(false);
    expect(name.closest('td')!.hasAttribute('aria-describedby')).toBe(false);
  });

  it('необрезанная — ничего; колонку расширили — больше ничего', async () => {
    grid([nameCell()]);
    const short = screen.getByTestId('name-2');
    sizes(short, 80, 200);
    await hover(short);
    expect(tip()).toBeNull();

    const long = screen.getByTestId('name-1');
    sizes(long, 600, 200);
    await hover(long);
    expect(tip()).not.toBeNull();
    fireEvent.pointerOut(long, { relatedTarget: document.body });
    expect(tip()).toBeNull();
    // Расширили: признак меряется заново при показе.
    sizes(long, 600, 600);
    await act(async () => { vi.advanceTimersByTime(1000); });
    await hover(long);
    expect(tip()).toBeNull();
  });

  it('щелчок по обрезанной клетке открывает строку один раз и закрывает показ', async () => {
    const open = vi.fn();
    grid([nameCell()], { onRowOpen: open });
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).not.toBeNull();
    fireEvent.pointerDown(name);
    fireEvent.click(name);
    expect(open).toHaveBeenCalledTimes(1);
    expect(tip()).toBeNull();
  });

  it('показ и скрытие не перерисовывают сетку', async () => {
    const renders = vi.fn();
    grid([nameCell(renders)]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    const before = renders.mock.calls.length;
    await hover(name);
    expect(tip()).not.toBeNull();
    fireEvent.pointerOut(name, { relatedTarget: document.body });
    expect(tip()).toBeNull();
    expect(renders.mock.calls.length).toBe(before);
  });

  it('касание и зажатая кнопка (выделяют текст) показа не зовут', async () => {
    grid([nameCell()]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name, { pointerType: 'touch' });
    expect(tip()).toBeNull();
    await hover(name.closest('td')!, { buttons: 1 });
    expect(tip()).toBeNull();
  });

  it('в окне Esc закрывает показ, а окно остаётся', async () => {
    render(<MemoryRouter><Modal open onOpenChange={() => {}} title="Ведомость">
      <DataGrid columns={[nameCell()]} rows={ITEMS} rowKey={i => i.id} />
    </Modal></MemoryRouter>);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).not.toBeNull();
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    expect(tip()).toBeNull();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('фокус с клавиатуры на обрезанной кнопке — показ сразу; на строке — нет', async () => {
    grid([{ key: 'name', label: 'Наименование',
      cell: i => <button type="button" data-testid={`btn-${i.id}`} style={CLIP}>{i.name}</button> }],
    { onRowOpen: () => {} });
    const button = screen.getByTestId('btn-1');
    sizes(button, 600, 200);
    fireEvent.keyDown(document.body, { key: 'Tab' });
    await act(async () => { button.closest('tr')!.focus(); });
    expect(tip()).toBeNull();
    await act(async () => { button.focus(); });
    expect(tip()?.textContent).toBe(LONG);
    await act(async () => { button.blur(); });
    expect(tip()).toBeNull();
  });

  it('у элемента со своей подсказкой — одна подсказка, его', async () => {
    grid([{ key: 'name', label: 'Наименование',
      cell: i => <Tooltip content="Просмотреть"><span data-testid={`name-${i.id}`} style={CLIP}>{i.name}</span></Tooltip> }]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).toBeNull();
  });

  it('вложенная сетка: показывает только своя', async () => {
    grid([{ key: 'name', label: 'Наименование', interactive: true,
      cell: i => (i.id === '1'
        ? <DataGrid framed={false} columns={[nameCell()]} rows={[{ id: 'in', name: LONG }]} rowKey={r => r.id} />
        : i.name) }]);
    const inner = screen.getByTestId('name-in');
    sizes(inner, 600, 200);
    await hover(inner);
    expect(document.querySelectorAll('[data-truncated-tip]')).toHaveLength(1);
  });

  it('после Esc переход на вложенный элемент того же текста показа не возвращает; уход из клетки — возвращает', async () => {
    grid([{ key: 'name', label: 'Наименование',
      cell: i => <span data-testid={`name-${i.id}`} style={CLIP}>Кабель <b data-testid={`b-${i.id}`}>{i.name}</b></span> }]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).not.toBeNull();
    fireEvent.keyDown(document.body, { key: 'Escape' });
    expect(tip()).toBeNull();
    await hover(screen.getByTestId('b-1'));
    expect(tip()).toBeNull();
    await hover(name);
    expect(tip()).toBeNull();
    await act(async () => { vi.advanceTimersByTime(1000); });
    await hover(screen.getByTestId('name-2'));
    await hover(name);
    expect(tip()).not.toBeNull();
  });

  it('кнопку отпустили над тем же текстом — показ приходит при следующем движении', async () => {
    grid([nameCell()]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name, { buttons: 1 });
    expect(tip()).toBeNull();
    await hover(name);
    expect(tip()).not.toBeNull();
  });

  it('слой пропускает указатель — и в окне, где тело окна в pointer-events: none', async () => {
    render(<MemoryRouter><Modal open onOpenChange={() => {}} title="Ведомость">
      <DataGrid columns={[nameCell()]} rows={ITEMS} rowKey={i => i.id} />
    </Modal></MemoryRouter>);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    const layer = tip() as HTMLElement;
    expect(layer.style.pointerEvents).toBe('none');
    // Обёртку Popper отпускает правило index.css по этой метке.
    expect(layer.hasAttribute('data-tooltip-passive')).toBe(true);
    expect(layer.parentElement!.hasAttribute('data-radix-popper-content-wrapper')).toBe(true);
  });

  it('текст под открытым показом перестал быть обрезанным — показ закрылся', async () => {
    grid([nameCell()]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).not.toBeNull();
    await act(async () => { name.style.textOverflow = 'clip'; await Promise.resolve(); });
    expect(tip()).toBeNull();
  });

  it('строку под открытым показом перерисовали — показ закрылся', async () => {
    const view = grid([nameCell()]);
    const name = screen.getByTestId('name-1');
    sizes(name, 600, 200);
    await hover(name);
    expect(tip()).not.toBeNull();
    view.rerender(<MemoryRouter><DataGrid columns={[nameCell()]} rows={[{ id: '3', name: 'Другая' }]} rowKey={i => i.id} /></MemoryRouter>);
    await act(async () => { await Promise.resolve(); });
    expect(tip()).toBeNull();
  });
});

describe('EditGrid: полный текст обрезанной текстовой клетки', () => {
  const edit = () => render(<EditGrid label="Разноска" rows={ITEMS} rowKey={i => i.id} columns={[
    { key: 'row', label: 'Строка', rowHeader: true, cell: i => <div data-testid={`title-${i.id}`} style={CLIP}>{i.name}</div> },
    { key: 'qty', label: 'Количество', cell: i => <input aria-label={`Количество, ${i.id}`} defaultValue={i.name} style={CLIP} /> },
  ]} />);

  it('название строки показывается целиком', async () => {
    edit();
    const title = screen.getByTestId('title-1');
    sizes(title, 600, 200);
    await hover(title);
    expect(tip()?.textContent).toBe(LONG);
  });

  it('переполненное поле ввода — без показа', async () => {
    edit();
    const field = screen.getByLabelText('Количество, 1');
    sizes(field, 600, 200);
    await hover(field);
    expect(tip()).toBeNull();
    fireEvent.keyDown(document.body, { key: 'Tab' });
    await act(async () => { field.focus(); });
    expect(tip()).toBeNull();
  });
});
