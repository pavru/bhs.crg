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
