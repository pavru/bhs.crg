// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { BrokenCount } from './BrokenCount';

/** Свод битых ссылок ([UI-74]): на вкладке — badge, у подписи поля — пометка error с числом. */

afterEach(cleanup);

describe('BrokenCount', () => {
  it('ноль — ничего', () => {
    const { container } = render(<BrokenCount count={0} />);
    expect(container.innerHTML).toBe('');
  });

  it('на вкладке — badge error без обёртки, диктор слышит «битых ссылок»', () => {
    const { container } = render(<button type="button">Реквизиты <BrokenCount nav count={3} className="align-middle" /></button>);
    const badge = container.querySelector('button > span')!;
    expect(badge.className).toContain('bg-error');
    expect(badge.className).toContain('align-middle');
    expect(screen.getByRole('button', { name: 'Реквизиты Битых ссылок: 3' })).toBeTruthy();
  });

  it('у подписи поля — пометка error со знаком и числом, без заливки', () => {
    const { container } = render(<BrokenCount count={2} />);
    const mark = container.querySelector('span')!;
    expect(mark.className).toContain('text-error');
    expect(mark.className).not.toMatch(/(?<![\w:-])bg-/);
    expect(mark.textContent).toBe('2');
    expect(container.querySelector('svg')).not.toBeNull();
  });
});
