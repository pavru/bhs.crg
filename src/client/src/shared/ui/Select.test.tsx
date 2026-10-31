// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Select, SelectItem } from './Select';

// Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

function Harness({ initial = '' }: { initial?: string }) {
  const [value, setValue] = useState(initial);
  return (
    <>
      <Select density="dense" aria-label="Итог" value={value} onValueChange={setValue} emptyItem="без итога">
        <SelectItem value="sum">Сумма</SelectItem>
      </Select>
      <output>{JSON.stringify(value)}</output>
    </>
  );
}

const pick = (name: string) => {
  fireEvent.keyDown(screen.getByRole('combobox', { name: 'Итог' }), { key: 'Enter' });
  fireEvent.click(screen.getByRole('option', { name }));
};

describe('Select: пункт «значения нет» (issue #1353)', () => {
  it('пустое значение показывается пунктом, а не пустым триггером', () => {
    render(<Harness />);
    expect(screen.getByRole('combobox', { name: 'Итог' }).textContent).toContain('без итога');
  });

  it('выбор пункта «значения нет» отдаёт пустую строку, а не своё служебное значение', () => {
    render(<Harness initial="sum" />);
    pick('без итога');
    expect(document.querySelector('output')!.textContent).toBe('""');
    expect(screen.getByRole('combobox', { name: 'Итог' }).textContent).toContain('без итога');
  });
});

describe('Select с подписью: exposed dropdown ([UI-69])', () => {
  function Field() {
    const [value, setValue] = useState('a');
    return (
      <Select label="Раздел" value={value} onValueChange={setValue}>
        <SelectItem value="a">Первый</SelectItem>
        <SelectItem value="b">Второй</SelectItem>
      </Select>
    );
  }
  const arrow = () => document.querySelector('[data-field-arrow]')!;

  it('стрелка — в хвосте оболочки и поворачивается у открытого списка', () => {
    render(<Field />);
    const trigger = screen.getByRole('combobox', { name: 'Раздел' });
    expect(trigger.contains(arrow())).toBe(false);
    expect(arrow().getAttribute('data-state')).toBe('closed');
    expect(arrow().className).toContain('data-[state=open]:rotate-180');
    fireEvent.keyDown(trigger, { key: 'Enter' });
    expect(arrow().getAttribute('data-state')).toBe('open');
  });

  it('клавиатура выбора прежняя: Enter открывает, пункт выбирается', () => {
    render(<Field />);
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Раздел' }), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Второй' }));
    expect(screen.getByRole('combobox', { name: 'Раздел' }).textContent).toContain('Второй');
    expect(arrow().getAttribute('data-state')).toBe('closed');
  });
});

describe('Select без подписи: вид по месту ([UI-69])', () => {
  function Filter() {
    const [value, setValue] = useState('');
    return (
      <Select variant="chip" aria-label="Отбор по действию" value={value} onValueChange={setValue} emptyItem="Все действия">
        <SelectItem value="login">Вход</SelectItem>
      </Select>
    );
  }
  const chip = () => screen.getByRole('combobox', { name: 'Отбор по действию' });

  it('чип отбора — сам триггер с ролью combobox, «все» — в покое', () => {
    render(<Filter />);
    expect(chip().tagName).toBe('BUTTON');
    expect(chip().textContent).toContain('Все действия');
    expect(chip().hasAttribute('data-selected')).toBe(false);
    expect(chip().className).toContain('h-8');
  });

  it('чип выбран, когда значение не пусто, и «все» возвращает его в покой', () => {
    render(<Filter />);
    fireEvent.keyDown(chip(), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Вход' }));
    expect(chip().hasAttribute('data-selected')).toBe(true);
    expect(chip().className).toContain('bg-secondary-container');
    fireEvent.keyDown(chip(), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Все действия' }));
    expect(chip().hasAttribute('data-selected')).toBe(false);
  });

  it('стрелка чипа поворачивается у открытого меню', () => {
    render(<Filter />);
    // Открытое меню прячет от диктора всё вокруг себя — триггер берём до открытия.
    const trigger = chip();
    expect(trigger.querySelector('svg:last-of-type')!.getAttribute('class')).toContain('group-data-[state=open]/chip:rotate-180');
    expect(trigger.className).toContain('group/chip');
    fireEvent.keyDown(trigger, { key: 'Enter' });
    expect(trigger.getAttribute('data-state')).toBe('open');
  });

  it('плотное поле без подписи — 40 на оболочке, имя из aria-label', () => {
    const { container } = render(
      <Select density="dense" aria-label="Колонка" value="" onValueChange={() => {}} emptyItem="— колонка —">
        <SelectItem value="a">A</SelectItem>
      </Select>,
    );
    const trigger = screen.getByRole('combobox', { name: 'Колонка' });
    expect(trigger.className.split(/\s+/)).toEqual(expect.arrayContaining(['h-10', 'text-body-medium']));
    expect(container.querySelector('fieldset')).not.toBeNull();
    expect(container.querySelector('legend')).toBeNull();
  });

  it('вид обязателен: без подписи, чипа, inline и плотности тип не собирается; чип — без emptyItem', () => {
    // @ts-expect-error — без подписи вид выбирает экран
    void <Select aria-label="Итог" value="" onValueChange={() => {}}><SelectItem value="a">A</SelectItem></Select>;
    // @ts-expect-error — чип отбора без «все» не бывает
    void <Select variant="chip" aria-label="Итог" value="" onValueChange={() => {}}><SelectItem value="a">A</SelectItem></Select>;
  });
});
