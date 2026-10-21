// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { Calendar } from './Calendar';
import { setCompanyTimeZone } from '@/shared/format/companyDay';

// Названия месяцев и дней недели и первый день недели — по языку пользователя (issue #1461).
let locale = 'ru-RU';
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => [locale, () => {}, 'idle'],
}));

afterEach(() => { cleanup(); locale = 'ru-RU'; vi.useRealTimers(); setCompanyTimeZone(null); });

const weekdays = () => within(screen.getByRole('grid', { name: 'Календарь' })).getAllByRole('columnheader')
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
    expect(screen.getByRole('button', { name: new RegExp(`^${title}`) })).toBeTruthy();
    const days = weekdays();
    expect(days).toHaveLength(7);
    expect(days[0]).toBe(first);
    expect(days[6]).toBe(last);
  });

  it('en-US: 1 октября 2026 (четверг) стоит в пятой колонке недели с воскресенья', () => {
    locale = 'en-US';
    render(<Calendar value="2026-10-18" onSelect={() => {}} />);
    // Строка 0 — заголовки дней недели, первая неделя — строка 1.
    const firstWeek = within(screen.getByRole('grid', { name: 'Календарь' })).getAllByRole('row')[1];
    const cells = within(firstWeek).getAllByRole('gridcell');
    expect(cells.findIndex(c => c.textContent === '1')).toBe(4);
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

// Разметка APG grid (issue #1463): клетки в строках, выбранное — `aria-selected` у клетки, у дней —
// строка заголовков. Без строк чтение с экрана не называет ни строку, ни столбец.
describe('Calendar: разметка сетки', () => {
  it('дни: заголовки недели и выбранная клетка', () => {
    render(<Calendar value="2026-10-18" onSelect={() => {}} />);
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    const [head, ...weeks] = within(grid).getAllByRole('row');
    expect(within(head).getAllByRole('columnheader')).toHaveLength(7);
    for (const week of weeks) expect(within(week).getAllByRole('gridcell')).toHaveLength(7);
    const selected = within(grid).getAllByRole('gridcell').filter(c => c.getAttribute('aria-selected') === 'true');
    expect(selected.map(c => c.textContent)).toEqual(['18']);
  });

  it.each([
    ['month' as const, 'Выбор месяца', '2026-10-01', 'Окт'],
    ['year' as const, 'Выбор года', '2026-01-01', '2026'],
  ])('%s: клетки по четыре в строке, выбранная — aria-selected', (precision, name, value, label) => {
    render(<Calendar value={value} precision={precision} onSelect={() => {}} />);
    const grid = screen.getByRole('grid', { name });
    const rows = within(grid).getAllByRole('row');
    for (const row of rows) expect(within(row).getAllByRole('gridcell').length).toBeLessThanOrEqual(4);
    const selected = within(grid).getAllByRole('gridcell').filter(c => c.getAttribute('aria-selected') === 'true');
    expect(selected.map(c => c.textContent)).toEqual([label]);
  });
});

// «Сегодня» — день компании (issue #1463): 31 октября 20:00 UTC — во Владивостоке уже 1 ноября, в
// Нью-Йорке ещё 31 октября. Браузер прогона стоит в своём поясе, и календарь обязан его не слушать.
describe('Calendar: сегодня по поясу компании', () => {
  it.each([
    ['Asia/Vladivostok', 'Ноябрь 2026', '1'],
    ['America/New_York', 'Октябрь 2026', '31'],
  ])('%s: открывается «%s», сегодня — %s', (zone, title, day) => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-31T20:00:00Z'));
    setCompanyTimeZone(zone);
    render(<Calendar value="" onSelect={() => {}} />);
    expect(screen.getByRole('button', { name: new RegExp(title) })).toBeTruthy();
    const today = within(screen.getByRole('grid', { name: 'Календарь' })).getAllByRole('button')
      .filter(b => b.getAttribute('aria-current') === 'date');
    expect(today.map(b => b.textContent)).toEqual([day]);
  });
});

// Строка действий (issue #1463, вариант В): «Сегодня» по точности поля и дню компании, «Очистить» —
// только когда поле его разрешило.
describe('Calendar: строка действий', () => {
  const at = (zone: string) => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-31T20:00:00Z'));
    setCompanyTimeZone(zone);
  };

  it.each([
    ['day' as const, 'Сегодня', 'Сегодня, 1 ноября 2026', '2026-11-01'],
    ['month' as const, 'Этот месяц', 'Этот месяц, ноябрь 2026', '2026-11-01'],
    ['year' as const, 'Этот год', 'Этот год, 2026', '2026-01-01'],
  ])('%s: «%s» ставит день компании и закрывает календарь', (precision, text, name, iso) => {
    at('Asia/Vladivostok');
    const onSelect = vi.fn(), onClose = vi.fn();
    render(<Calendar value="2026-03-15" precision={precision} onSelect={onSelect} onClose={onClose} />);
    const button = screen.getByRole('button', { name: new RegExp(`^${name}`) });
    expect(button.textContent).toBe(text);
    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledWith(iso);
    expect(onClose).toHaveBeenCalled();
  });

  it('«Очистить» — только с onClear', () => {
    render(<Calendar value="2026-10-18" onSelect={() => {}} />);
    expect(screen.queryByRole('button', { name: 'Очистить' })).toBeNull();
    cleanup();
    const onClear = vi.fn();
    render(<Calendar value="2026-10-18" onSelect={() => {}} onClear={onClear} />);
    fireEvent.click(screen.getByRole('button', { name: 'Очистить' }));
    expect(onClear).toHaveBeenCalledOnce();
  });
});

// Границы min/max (issue #1464): за ними не выбрать, фокус и стрелки шапки туда не заходят.
describe('Calendar: границы min/max', () => {
  const grid = () => screen.getByRole('grid', { name: 'Календарь' });
  const day = (n: string) => within(grid()).getAllByRole('button').find(b => b.textContent === n)!;

  it('дни вне границ неактивны, «Назад» неактивна, если прошлый месяц целиком за min', () => {
    render(<Calendar value="2026-10-14" min="2026-10-10" max="2026-11-25" onSelect={() => {}} />);
    expect((day('9') as HTMLButtonElement).disabled).toBe(true);
    expect((day('10') as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Назад' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Вперёд' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('фокус стрелкой упирается в границу, а не уходит на неактивный день', () => {
    render(<Calendar value="2026-10-11" min="2026-10-10" onSelect={() => {}} />);
    fireEvent.keyDown(grid(), { key: 'ArrowUp' });
    expect(document.activeElement?.textContent).toBe('10');
  });

  it('пустое поле при сегодня вне границ открывается на ближайшей, «Сегодня» скрыта', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 9, 21, 12));
    render(<Calendar value="" min="2027-01-01" onSelect={() => {}} onClear={() => {}} />);
    expect(screen.getByRole('button', { name: /^Январь 2027/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Сегодня/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Очистить' })).toBeTruthy();
  });

  it('без «Очистить» и «Сегодня» строки действий нет', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 9, 21, 12));
    const { container } = render(<Calendar value="" max="2026-01-31" onSelect={() => {}} />);
    expect(container.querySelector('.border-t')).toBeNull();
  });

  it('точность «месяц»: месяц границы доступен, хотя её день позже 01', () => {
    render(<Calendar value="2026-09-01" precision="month" min="2026-07-15" max="2027-03-10" onSelect={() => {}} />);
    const months = within(screen.getByRole('grid', { name: 'Выбор месяца' })).getAllByRole('button');
    expect(months.filter(b => (b as HTMLButtonElement).disabled).map(b => b.textContent)).toEqual(['Янв', 'Фев', 'Мар', 'Апр', 'Май', 'Июн']);
  });
});

// Лента периода (#1464, лента из варианта Б): границы залиты, дни между ними — на ленте.
describe('Calendar: лента периода', () => {
  it('обе границы залиты, между ними — лента, за ними — нет', () => {
    const { container } = render(<Calendar value="2026-10-24" range={['2026-10-12', '2026-10-24']} onSelect={() => {}} />);
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    const day = (n: string) => within(grid).getAllByRole('button').find(b => b.textContent === n)!;
    expect(day('12').className).toContain('bg-brand ');
    expect(day('24').className).toContain('bg-brand ');
    // Полоса ленты — у каждого дня с 12 по 24 включительно: 13 дней.
    expect(container.querySelectorAll('span.bg-brand-subtle')).toHaveLength(13);
  });
});
