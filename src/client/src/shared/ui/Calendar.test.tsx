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

/** Подписи строк списка месяцев или лет. */
const options = (name: string) => within(screen.getByRole('listbox', { name })).getAllByRole('option').map(o => o.textContent);
const option = (name: string, text: string) =>
  within(screen.getByRole('listbox', { name })).getAllByRole('option').find(o => o.textContent === text)!;

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

  it('месяцы точности «месяц» — полными названиями по языку, в шапке — сокращением без точки', () => {
    locale = 'en-US';
    render(<Calendar value="2026-10-01" precision="month" onSelect={() => {}} />);
    expect(options('Выбор месяца')).toContain('October');
    cleanup();
    locale = 'ru-RU';
    render(<Calendar value="2026-10-01" precision="month" onSelect={() => {}} />);
    expect(options('Выбор месяца')).toContain('Октябрь');
    cleanup();
    render(<Calendar value="2026-09-18" onSelect={() => {}} />);
    expect(screen.getByRole('button', { name: /выбрать месяц$/ }).textContent).toBe('Сен');
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

  // Месяц и год — списком docked date picker ([UI-80], выбор владельца 03.11.2026), а не сеткой.
  it.each([
    ['month' as const, 'Выбор месяца', '2026-10-01', 'Октябрь', 12],
    ['year' as const, 'Выбор года', '2026-01-01', '2026', 201],
  ])('%s: listbox, отмечено значение — aria-selected и галочка', (precision, name, value, label, count) => {
    render(<Calendar value={value} precision={precision} onSelect={() => {}} />);
    const all = within(screen.getByRole('listbox', { name })).getAllByRole('option');
    expect(all).toHaveLength(count);
    const selected = all.filter(o => o.getAttribute('aria-selected') === 'true');
    expect(selected.map(o => o.textContent)).toEqual([label]);
    expect(selected[0].querySelector('svg')).toBeTruthy();
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
    expect((screen.getByRole('button', { name: 'Предыдущий месяц' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Следующий месяц' }) as HTMLButtonElement).disabled).toBe(false);
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
    render(<Calendar value="" max="2026-01-31" onSelect={() => {}} />);
    expect(screen.queryByTestId('calendar-actions')).toBeNull();
  });

  it('точность «месяц»: месяц границы доступен, хотя её день позже 01', () => {
    render(<Calendar value="2026-09-01" precision="month" min="2026-07-15" max="2027-03-10" onSelect={() => {}} />);
    const months = within(screen.getByRole('listbox', { name: 'Выбор месяца' })).getAllByRole('option');
    expect(months.filter(o => o.getAttribute('aria-disabled') === 'true').map(o => o.textContent))
      .toEqual(['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь']);
  });

  it('«Вперёд» неактивна, если следующий месяц целиком за max', () => {
    render(<Calendar value="2026-11-14" min="2026-10-10" max="2026-11-25" onSelect={() => {}} />);
    expect((screen.getByRole('button', { name: 'Следующий месяц' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Предыдущий месяц' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('месяцы: ↓ и End за max упираются в месяц границы', () => {
    render(<Calendar value="2027-02-01" precision="month" max="2027-03-10" onSelect={() => {}} />);
    const list = screen.getByRole('listbox', { name: 'Выбор месяца' });
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(document.activeElement?.textContent).toBe('Март');
    fireEvent.keyDown(list, { key: 'End' });
    expect(document.activeElement?.textContent).toBe('Март');
    fireEvent.keyDown(list, { key: 'PageDown' });
    expect(document.activeElement?.textContent).toBe('Март');
  });

  it('годы: за границами неактивны, End и Home упираются в годы max и min', () => {
    render(<Calendar value="2025-01-01" precision="year" min="2024-05-01" max="2026-03-10" onSelect={() => {}} />);
    const list = screen.getByRole('listbox', { name: 'Выбор года' });
    const off = (y: string) => option('Выбор года', y).getAttribute('aria-disabled') === 'true';
    expect([off('2023'), off('2024'), off('2026'), off('2027')]).toEqual([true, false, false, true]);
    fireEvent.keyDown(list, { key: 'End' });
    expect(document.activeElement?.textContent).toBe('2026');
    fireEvent.keyDown(list, { key: 'Home' });
    expect(document.activeElement?.textContent).toBe('2024');
  });
});

// Лента периода (#1464, лента из варианта Б): границы залиты, дни между ними — на ленте.
describe('Calendar: лента периода', () => {
  it('обе границы залиты, между ними — лента, за ними — нет', () => {
    const { container } = render(<Calendar value="2026-10-24" range={['2026-10-12', '2026-10-24']} onSelect={() => {}} />);
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    const day = (n: string) => within(grid).getAllByRole('button').find(b => b.textContent === n)!;
    expect(day('12').className).toContain('bg-primary ');
    expect(day('24').className).toContain('bg-primary ');
    // Полоса ленты — у каждого дня с 12 по 24 включительно: 13 дней.
    expect(container.querySelectorAll('span.bg-secondary-container')).toHaveLength(13);
    // Дни между концами — цветом текста на ленте.
    expect(day('18').className).toContain('text-on-secondary-container');
  });

  it('точность «месяц»: концы периода отмечены галочкой, середина — на ленте', () => {
    render(<Calendar value="2026-03-01" precision="month" range={['2026-03-01', '2026-06-01']} onSelect={() => {}} />);
    const all = within(screen.getByRole('listbox', { name: 'Выбор месяца' })).getAllByRole('option');
    expect(all.filter(o => o.getAttribute('aria-selected') === 'true').map(o => o.textContent)).toEqual(['Март', 'Июнь']);
    expect(option('Выбор месяца', 'Апрель').className).toContain('bg-secondary-container');
    expect(option('Выбор месяца', 'Июль').className).not.toContain('bg-secondary-container');
  });
});

// Docked date picker ([UI-80], макет А md3-16): 360, клетка 40 в строке 48, шапка — два селектора.
describe('Calendar: вид docked', () => {
  it('ширина 360 и не шире места; клетка дня 40; выбранный — круг primary, сегодня — обводка primary', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 10, 20, 12));
    const { container } = render(<Calendar value="2026-11-15" onSelect={() => {}} />);
    const root = (container.firstElementChild as HTMLElement).className.split(/\s+/);
    expect(root).toContain('w-90');
    expect(root).toContain('max-w-full');
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    const day = (n: string) => within(grid).getAllByRole('button').find(b => b.textContent === n)!;
    expect(day('15').className).toContain('size-10');
    expect(day('15').className).toContain('bg-primary text-on-primary');
    expect(day('20').className).toContain('inset-ring-primary');
    expect(day('20').className).not.toContain('bg-primary');
    expect(within(grid).getAllByRole('gridcell')[0].className).toContain('h-12');
  });

  it('строка действий — без черты над ней', () => {
    render(<Calendar value="2026-10-18" onSelect={() => {}} onClear={() => {}} />);
    expect(screen.getByTestId('calendar-actions').className.split(/\s+/)).not.toContain('border-t');
  });

  it('стрелки листают месяц и год', () => {
    render(<Calendar value="2026-11-12" onSelect={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Следующий месяц' }));
    expect(screen.getByRole('button', { name: /^Декабрь 2026/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Предыдущий год' }));
    expect(screen.getByRole('button', { name: /^Декабрь 2025/ })).toBeTruthy();
  });

  it('выбор месяца из шапки возвращает к дням и даты не ставит', () => {
    const onSelect = vi.fn(), onClose = vi.fn();
    render(<Calendar value="2026-11-12" onSelect={onSelect} onClose={onClose} />);
    fireEvent.click(screen.getByRole('button', { name: /выбрать месяц$/ }));
    // Отмечен показанный месяц; пока список открыт, стрелок нет.
    expect(option('Выбор месяца', 'Ноябрь').getAttribute('aria-selected')).toBe('true');
    expect(screen.queryByRole('button', { name: 'Следующий месяц' })).toBeNull();
    fireEvent.click(option('Выбор месяца', 'Октябрь'));
    expect(screen.getByRole('grid', { name: 'Календарь' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Октябрь 2026/ })).toBeTruthy();
    expect(onSelect).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('кнопка года: список лет, выбранный отмечен и в фокусе, стрелок и селектора месяца нет', () => {
    render(<Calendar value="2026-11-12" onSelect={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: /выбрать год$/ }));
    const y2026 = option('Выбор года', '2026');
    expect(y2026.getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(y2026);
    expect(screen.queryByRole('button', { name: 'Предыдущий год' })).toBeNull();
    expect(screen.queryByRole('button', { name: /выбрать месяц$/ })).toBeNull();
    expect(screen.getByRole('button', { name: /выбрать год$/ }).getAttribute('aria-expanded')).toBe('true');
    fireEvent.click(option('Выбор года', '2024'));
    expect(screen.getByRole('button', { name: /^Ноябрь 2024/ })).toBeTruthy();
  });

  it('Esc в списке из шапки — к дням; признак для слоя снят', () => {
    const { container } = render(<Calendar value="2026-11-12" onSelect={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: /выбрать месяц$/ }));
    expect(container.querySelector('[data-calendar-back]')).toBeTruthy();
    fireEvent.keyDown(screen.getByRole('listbox', { name: 'Выбор месяца' }), { key: 'Escape' });
    expect(screen.getByRole('grid', { name: 'Календарь' })).toBeTruthy();
    expect(container.querySelector('[data-calendar-back]')).toBeNull();
  });

  it('клавиши списка: ↓ на одну строку, End — к последней, Enter выбирает', () => {
    const onSelect = vi.fn(), onClose = vi.fn();
    render(<Calendar value="2026-05-01" precision="month" onSelect={onSelect} onClose={onClose} />);
    const list = screen.getByRole('listbox', { name: 'Выбор месяца' });
    expect(document.activeElement?.textContent).toBe('Май');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(document.activeElement?.textContent).toBe('Июнь');
    fireEvent.keyDown(list, { key: 'End' });
    expect(document.activeElement?.textContent).toBe('Декабрь');
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledWith('2026-12-01');
    expect(onClose).toHaveBeenCalled();
  });

  it('точность «месяц»: список под селектором года, выбор ставит месяц показанного года', () => {
    const onSelect = vi.fn(), onClose = vi.fn();
    render(<Calendar value="2026-10-01" precision="month" onSelect={onSelect} onClose={onClose} />);
    expect(screen.queryByRole('button', { name: /выбрать месяц$/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Следующий год' }));
    fireEvent.click(option('Выбор месяца', 'Март'));
    expect(onSelect).toHaveBeenCalledWith('2027-03-01');
    expect(onClose).toHaveBeenCalled();
  });

  it('точность «месяц»: год из шапки возвращает к месяцам, Esc в годах — тоже', () => {
    const onSelect = vi.fn();
    render(<Calendar value="2026-10-01" precision="month" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole('button', { name: /выбрать год$/ }));
    fireEvent.click(option('Выбор года', '2028'));
    expect(screen.getByRole('listbox', { name: 'Выбор месяца' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /^2028/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /выбрать год$/ }));
    fireEvent.keyDown(screen.getByRole('listbox', { name: 'Выбор года' }), { key: 'Escape' });
    expect(screen.getByRole('listbox', { name: 'Выбор месяца' })).toBeTruthy();
    expect(onSelect).not.toHaveBeenCalled();
  });
});
