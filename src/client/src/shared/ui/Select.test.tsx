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
      <Select aria-label="Итог" value={value} onValueChange={setValue} emptyItem="без итога">
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
