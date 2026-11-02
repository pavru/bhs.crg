// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DateInput } from './DateInput';
import { Select, SelectItem } from './Select';
import { TextField } from './TextField';
import { FieldDensityContext } from './fieldBox';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Поле в клетке сетки правки — размер `cell` ([UI-78], sharedui-md3-14): без рамки, заливки и черты в покое,
 * черта 2 px `primary` у фокуса и открытого списка, `error` у ошибки, знак ошибки 20 с подсказкой.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const classes = (el: Element) => el.className.split(/\s+/);
/** Обёртка оболочки — на ней черта клетки (`cellLine`). */
const shell = (control: Element) => control.closest('.relative')!;

describe('поле клетки: TextField', () => {
  it('40, body-medium, без рамки и выреза; имя — у самого поля, текстом в клетке его нет', () => {
    const { container } = render(<TextField label="Наименование, строка 1" density="cell" />);
    const input = screen.getByLabelText('Наименование, строка 1');
    expect(classes(input)).toEqual(expect.arrayContaining(['h-10', 'text-body-medium', 'pl-2']));
    expect(container.querySelector('fieldset')).toBeNull();
    // Подпись текстом вошла бы в текст строки — в копирование и сверку строк.
    expect(input.getAttribute('aria-label')).toBe('Наименование, строка 1');
    expect(container.textContent).toBe('');
  });

  it('в покое черты нет, у фокуса — primary', () => {
    render(<TextField label="Ед." density="cell" />);
    const line = classes(shell(screen.getByLabelText('Ед.')));
    expect(line).toEqual(expect.arrayContaining(['after:h-0.5', 'after:bg-transparent', 'focus-within:after:bg-primary']));
    expect(line).not.toContain('after:bg-outline');
  });

  it('ошибка — черта error и знак 20 вместо текста под полем', () => {
    const { container } = render(<TextField label="Цена" density="cell" error="Не число" />);
    const line = classes(shell(screen.getByLabelText('Цена')));
    expect(line).toContain('after:bg-error');
    expect(line).not.toContain('focus-within:after:bg-primary');
    expect(container.querySelector('[data-field-error-mark] svg')?.getAttribute('width')).toBe('20');
    expect(classes(screen.getByText('Не число'))).toContain('sr-only');
  });

  it('у клетки своя подсказка в пустом поле, у поля формы — нет', () => {
    render(<><TextField label="Сумма" density="cell" placeholder="48,50" /><TextField label="Номер" placeholder="12" /></>);
    expect(screen.getByLabelText('Сумма').getAttribute('placeholder')).toBe('48,50');
    expect(screen.getByLabelText('Номер').getAttribute('placeholder')).toBe(' ');
  });

  it('размер места: в теле сетки поле без своего размера — клетка, свой размер сильнее', () => {
    const { container } = render(
      <FieldDensityContext.Provider value="cell">
        <TextField label="Марка" />
        <TextField label="Шапка" density="dense" />
      </FieldDensityContext.Provider>,
    );
    expect(classes(screen.getByLabelText('Марка'))).toContain('h-10');
    expect(container.querySelectorAll('fieldset')).toHaveLength(1);
  });
});

describe('поле клетки: Select', () => {
  it('без рамки, а открытый список держит черту primary', () => {
    const { container } = render(
      <Select density="cell" aria-label="Раздел, часть 1" value="" onValueChange={() => {}} emptyItem="— вся стройка —">
        <SelectItem value="a">Раздел А</SelectItem>
      </Select>,
    );
    const trigger = screen.getByRole('combobox', { name: 'Раздел, часть 1' });
    expect(container.querySelector('fieldset')).toBeNull();
    expect(classes(shell(trigger))).toContain('focus-within:after:bg-primary');
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(classes(shell(trigger))).toContain('after:bg-primary');
  });
});

describe('поле клетки: DateInput', () => {
  it('без рамки, значение не прошло проверку — черта error', () => {
    const { container } = render(<DateInput label="Дата, строка 1" value="" onChange={() => {}} density="cell" invalid />);
    const group = container.querySelector('[role="group"]')!;
    expect(group.className).not.toMatch(/(^|\s)border(\s|$)/);
    expect(classes(group)).toEqual(expect.arrayContaining(['h-10', 'after:bg-error']));
  });

  it('пустая дата: в покое черты нет, у фокуса — primary; значок календаря — по наведению', () => {
    const { container } = render(<DateInput label="Дата, строка 1" value="" onChange={() => {}} density="cell" />);
    const group = container.querySelector('[role="group"]')!;
    expect(classes(group)).toEqual(expect.arrayContaining(['after:bg-transparent', 'focus-within:after:bg-primary']));
    expect(screen.getByRole('button', { name: 'Открыть календарь' }).className).toContain('opacity-0');
    expect(screen.queryByRole('button', { name: 'Очистить дату' })).toBeNull();
  });
});
