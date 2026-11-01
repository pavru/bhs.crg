// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { LinearProgress } from './LinearProgress';

/** Ход операции на месте ([UI-74]): progressbar с именем, доля — только когда известна. */

afterEach(cleanup);

describe('LinearProgress', () => {
  it('с долей — progressbar с именем и долей в процентах', () => {
    render(<LinearProgress value={0.4} label="Ход пакета сканов" />);
    const bar = screen.getByRole('progressbar', { name: 'Ход пакета сканов' });
    expect(bar.getAttribute('aria-valuenow')).toBe('40');
    expect(bar.className).toContain('bg-secondary-container');
    expect((bar.firstElementChild as HTMLElement).style.width).toBe('40%');
  });

  it('без доли — бежит, и aria-valuenow нет', () => {
    render(<LinearProgress label="Загрузка" />);
    const bar = screen.getByRole('progressbar', { name: 'Загрузка' });
    expect(bar.hasAttribute('aria-valuenow')).toBe(false);
    expect(bar.firstElementChild!.className).toContain('md-linear');
  });

  it('доля вне 0…1 прижимается к краю', () => {
    render(<LinearProgress value={1.7} label="Готово" />);
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('100');
  });
});
