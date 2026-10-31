// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SegmentedButton } from './SegmentedButton';
import { TabPanel, Tabs } from './Tabs';

afterEach(cleanup);

const ITEMS = [{ value: 'with', label: 'с НДС' }, { value: 'without', label: 'без НДС' }] as const;

function Vat() {
  const [v, setV] = useState<'with' | 'without'>('with');
  return <SegmentedButton aria-label="Суммы" items={[...ITEMS]} value={v} onChange={setV} />;
}

describe('SegmentedButton', () => {
  // [UI-68]: выбор значения — радиогруппа, стрелка выбирает соседний вариант.
  it('роли radiogroup / radio, стрелка выбирает, у выбранного галочка', () => {
    render(<Vat />);
    expect(screen.getByRole('radiogroup', { name: 'Суммы' })).toBeTruthy();
    const withVat = screen.getByRole('radio', { name: 'с НДС' });
    expect(withVat.getAttribute('aria-checked')).toBe('true');
    expect(withVat.querySelector('svg')).toBeTruthy();
    withVat.focus();
    fireEvent.keyDown(withVat, { key: 'ArrowRight' });
    const without = screen.getByRole('radio', { name: 'без НДС' });
    expect(without.getAttribute('aria-checked')).toBe('true');
    expect(document.activeElement).toBe(without);
    expect(without.className).toContain('bg-secondary-container');
  });

  it('крайние сегменты — форма full, соседи делят контур', () => {
    render(<Vat />);
    expect(screen.getByRole('radio', { name: 'с НДС' }).className).toContain('rounded-l-full');
    const last = screen.getByRole('radio', { name: 'без НДС' }).className;
    expect(last).toContain('rounded-r-full');
    expect(last).toContain('-ml-px');
  });
});

describe('Tabs segmented', () => {
  function Views() {
    const [v, setV] = useState<'live' | 'closed'>('live');
    return (
      <>
        <Tabs id="cv" variant="segmented" aria-label="Взгляд" value={v} onChange={setV}
          items={[{ value: 'live', label: 'Текущий' }, { value: 'closed', label: 'На момент закрытия' }]} />
        <TabPanel id="cv" value={v}>взгляд {v}</TabPanel>
      </>
    );
  }

  it('вид сегментов, но стрелка не переключает, Enter переключает', async () => {
    render(<Views />);
    const live = screen.getByRole('tab', { name: 'Текущий' });
    expect(live.className).toContain('rounded-l-full');
    live.focus();
    await userEvent.keyboard('{ArrowRight}');
    const closed = screen.getByRole('tab', { name: 'На момент закрытия' });
    expect(document.activeElement).toBe(closed);
    expect(closed.getAttribute('aria-selected')).toBe('false');
    await userEvent.keyboard('{Enter}');
    expect(closed.getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tabpanel').textContent).toBe('взгляд closed');
  });
});
