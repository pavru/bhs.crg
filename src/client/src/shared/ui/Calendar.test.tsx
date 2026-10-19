// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { Calendar } from './Calendar';

// Названия месяцев и дней недели и первый день недели — по языку пользователя (issue #1461).
let locale = 'ru-RU';
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => [locale, () => {}, 'idle'],
}));

afterEach(() => { cleanup(); locale = 'ru-RU'; });

const weekdays = () => [...screen.getByRole('grid', { name: 'Календарь' }).querySelectorAll('.grid.grid-cols-7.mb-1 > div')]
  .map(d => d.textContent);

describe('Calendar: язык пользователя', () => {
  it.each([
    ['ru-RU', 'Октябрь 2026', 'Пн', 'Вс'],
    ['en-US', 'October 2026', 'Sun', 'Sat'],
    ['en-GB', 'October 2026', 'Mon', 'Sun'],
    ['de-DE', 'Oktober 2026', 'Mo', 'So'],
  ])('%s: заголовок «%s», неделя с «%s» по «%s»', (lang, title, first, last) => {
    locale = lang;
    render(<Calendar value="2026-10-18" onSelect={() => {}} />);
    expect(screen.getByRole('button', { name: new RegExp(title) })).toBeTruthy();
    const days = weekdays();
    expect(days).toHaveLength(7);
    expect(days[0]).toBe(first);
    expect(days[6]).toBe(last);
  });

  it('en-US: 1 октября 2026 (четверг) стоит в пятой колонке недели с воскресенья', () => {
    locale = 'en-US';
    render(<Calendar value="2026-10-18" onSelect={() => {}} />);
    const firstRow = within(screen.getByRole('grid', { name: 'Календарь' })).getAllByRole('row')[0];
    const cells = firstRow.querySelectorAll('.h-9');
    expect([...cells].findIndex(c => c.textContent === '1')).toBe(4);
  });

  it('Home и End — к первому и последнему дню недели языка', () => {
    locale = 'en-US';
    const onSelect = vi.fn();
    render(<Calendar value="2026-10-14" onSelect={onSelect} />); // среда
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    fireEvent.keyDown(grid, { key: 'Home' });
    fireEvent.keyDown(grid, { key: 'Enter' });
    expect(onSelect).toHaveBeenLastCalledWith('2026-10-11'); // воскресенье
    cleanup();
    locale = 'ru-RU';
    render(<Calendar value="2026-10-14" onSelect={onSelect} />);
    const ru = screen.getByRole('grid', { name: 'Календарь' });
    fireEvent.keyDown(ru, { key: 'End' });
    fireEvent.keyDown(ru, { key: 'Enter' });
    expect(onSelect).toHaveBeenLastCalledWith('2026-10-18'); // воскресенье — конец недели с понедельника
  });

  it('месяцы точности «месяц» — по языку, без точки сокращения', () => {
    locale = 'en-US';
    render(<Calendar value="2026-10-01" precision="month" onSelect={() => {}} />);
    const grid = screen.getByRole('grid', { name: 'Выбор месяца' });
    expect(within(grid).getAllByRole('button').map(b => b.textContent)).toContain('Oct');
    cleanup();
    locale = 'ru-RU';
    render(<Calendar value="2026-10-01" precision="month" onSelect={() => {}} />);
    expect(within(screen.getByRole('grid', { name: 'Выбор месяца' })).getAllByRole('button').map(b => b.textContent)).toContain('Окт');
  });
});
