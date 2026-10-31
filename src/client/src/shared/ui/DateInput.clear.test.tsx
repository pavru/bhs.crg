// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { DateInput } from './DateInput';

// Маска — языка пользователя (issue #1461); jsdom говорит по-английски, поэтому язык — явно.
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));
import { DateField } from './DateField';

/** Очистка поля даты (issue #1463) — отдельно от ввода (`DateInput.test.tsx`): храповик размера. */

beforeAll(() => {
  // Календарь — поповер Radix: jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

const seg = (ph: 'ДД' | 'ММ' | 'ГГГГ') => screen.getByPlaceholderText(ph) as HTMLInputElement;

/** Уход из поля: blur последнего сегмента и таймер, которым поле отличает переход между сегментами. */
function leave(from: HTMLInputElement = seg('ГГГГ')) {
  fireEvent.blur(from);
  act(() => { vi.advanceTimersByTime(200); });
}

// «Очистить» и «Сегодня» (issue #1463, макет «Очистить и Сегодня», вариант В): крестик — ускоритель у
// поля формы, гарантированный путь — строка действий календаря, с клавиатуры — Ctrl+A → Delete.
describe('DateInput: очистка', () => {
  function Form({ initial = '2026-11-03', required = false, compact = false }: { initial?: string; required?: boolean; compact?: boolean }) {
    const [value, setValue] = useState(initial);
    return (
      <div onKeyDown={outerKey}>
        {compact
          ? <DateInput label="Оплатить до" value={value} onChange={setValue} compact />
          : <DateField label="Оплатить до" value={value} onChange={setValue} required={required} hint="срок из договора" />}
        <output data-testid="value">{value}</output>
      </div>
    );
  }
  const outerKey = vi.fn();
  afterEach(() => outerKey.mockReset());
  const value = () => screen.getByTestId('value').textContent;
  const clearButton = () => screen.queryByRole('button', { name: 'Очистить дату' });
  const openCalendar = () => fireEvent.click(screen.getByRole('button', { name: 'Открыть календарь' }));
  const selectAll = (el: HTMLInputElement = seg('ММ')) => { el.focus(); fireEvent.keyDown(el, { key: 'a', code: 'KeyA', ctrlKey: true }); };

  it('крестик стирает дату, фокус — в первый сегмент, Esc возвращает прежнюю', () => {
    render(<Form />);
    fireEvent.click(clearButton()!);
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(clearButton()).toBeNull(); // стирать больше нечего
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(value()).toBe('2026-11-03');
  });

  it('крестик — кнопка внутри поля, место XS ([UI-68]): 32, как календарь рядом, при касании 40', () => {
    render(<Form />);
    expect(clearButton()!.className.split(/\s+/)).toEqual(expect.arrayContaining([
      '[--md-comp-icon-button-size:32px]', 'pointer-coarse:[--md-comp-icon-button-size:40px]']));
  });

  it('у обязательного и пустого поля крестика нет; у обязательного нет и «Очистить» в календаре', () => {
    render(<Form initial="" />);
    expect(clearButton()).toBeNull();
    cleanup();
    render(<Form required />);
    expect(clearButton()).toBeNull();
    openCalendar();
    expect(screen.queryByRole('button', { name: 'Очистить' })).toBeNull();
    expect(screen.getByRole('button', { name: /^Сегодня, / })).toBeTruthy();
  });

  it('недописанное поверх пустого стирается крестиком — вместе с ошибкой', () => {
    vi.useFakeTimers();
    render(<Form initial="" />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '18' } });
    leave(seg('ММ'));
    expect(screen.getByText('Дата не дописана. Срок из договора')).toBeTruthy();
    fireEvent.click(clearButton()!);
    act(() => { vi.advanceTimersByTime(200); });
    expect(seg('ДД').value).toBe('');
    expect(screen.queryByText(/Дата не дописана/)).toBeNull();
    expect(screen.getByText('срок из договора')).toBeTruthy();
  });

  it('«не дата» стирается крестиком', () => {
    vi.useFakeTimers();
    render(<Form initial="вчера" />);
    fireEvent.click(clearButton()!);
    act(() => { vi.runAllTimers(); });
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
  });

  it('в ячейке крестика нет — «Очистить» в календаре, фокус в первый сегмент', () => {
    render(<Form compact />);
    expect(clearButton()).toBeNull();
    openCalendar();
    fireEvent.click(screen.getByRole('button', { name: 'Очистить' }));
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('«Сегодня» в календаре ставит день компании', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 9, 21, 12));
    render(<Form compact />);
    openCalendar();
    fireEvent.click(screen.getByRole('button', { name: /^Сегодня, / }));
    expect(value()).toBe('2026-10-21');
  });

  it('Ctrl+A → Delete стирает и у обязательного поля; фокус — в первый сегмент', () => {
    render(<Form required />);
    selectAll();
    expect(screen.getByText('Выделена вся дата')).toBeTruthy();
    fireEvent.keyDown(seg('ММ'), { key: 'Delete' });
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
  });

  it('Ctrl+A → цифра начинает дату заново с первого сегмента; прежняя держится до новой, Esc её возвращает', () => {
    render(<Form />);
    selectAll(seg('ГГГГ'));
    fireEvent.keyDown(seg('ГГГГ'), { key: '2' });
    expect(seg('ДД').value).toBe('2');
    expect(seg('ММ').value).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    // Посреди набора форма пустоты не получает: значение — прежнее, пока новая дата не сложится.
    expect(value()).toBe('2026-11-03');
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(seg('ДД').value).toBe('03');
    expect(value()).toBe('2026-11-03');
  });

  it('Ctrl+A → цифра 4–9 дополняет день нулём и уводит дальше, как обычный набор', () => {
    render(<Form />);
    selectAll();
    fireEvent.keyDown(seg('ММ'), { key: '5' });
    expect(seg('ДД').value).toBe('05');
    expect(document.activeElement).toBe(seg('ММ'));
    expect(seg('ММ').value).toBe('');
  });

  it('Ctrl+A в пустом поле ничего не выделяет', () => {
    render(<Form initial="" />);
    selectAll();
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
    expect(seg('ММ').className.split(' ')).not.toContain('bg-primary-container'); // `focus:` — не в счёт
  });

  it('Ctrl+A → Ctrl+X: дата в буфер, поле стёрто целиком', () => {
    render(<Form />);
    selectAll();
    const setData = vi.fn();
    fireEvent.keyDown(seg('ММ'), { key: 'x', code: 'KeyX', ctrlKey: true });
    expect(screen.getByText('Выделена вся дата')).toBeTruthy(); // Ctrl+X выделения не снимает
    fireEvent.cut(seg('ММ'), { clipboardData: { setData } });
    expect(setData).toHaveBeenCalledWith('text/plain', '03.11.2026');
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
  });

  it('Ctrl+A → Ctrl+C у недописанной даты не подменяет копирование браузера', () => {
    vi.useFakeTimers();
    render(<Form initial="" />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '18' } });
    selectAll();
    const setData = vi.fn();
    fireEvent.copy(seg('ММ'), { clipboardData: { setData } });
    expect(setData).not.toHaveBeenCalled();
  });

  it('Ctrl+A → Esc снимает выделение, дату не трогает, окну Esc не уходит', () => {
    render(<Form />);
    selectAll();
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
    expect(value()).toBe('2026-11-03');
    expect(outerKey).not.toHaveBeenCalledWith(expect.objectContaining({ key: 'Escape' }));
    // Второй Esc — уже окну: правки не было.
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(outerKey).toHaveBeenCalledTimes(2); // Ctrl+A и этот Esc; первый Esc погашен
  });

  it('Ctrl+A → Ctrl+C копирует дату в маске языка', () => {
    render(<Form />);
    selectAll();
    const setData = vi.fn();
    fireEvent.keyDown(seg('ММ'), { key: 'Control', ctrlKey: true });
    fireEvent.copy(seg('ММ'), { clipboardData: { setData } });
    expect(setData).toHaveBeenCalledWith('text/plain', '03.11.2026');
  });
});
