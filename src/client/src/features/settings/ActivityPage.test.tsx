// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { ActivityRecord, ActivityRow } from '@/shared/api/activity';
import { ActivityPage } from './ActivityPage';

/**
 * Экран журнала рядами (ТЗ CORE-28.2): серия — один ряд «×N · с–по» с прежним именем цели, если оно
 * сменилось; раскрытие показывает её записи, каждую со своим «было → стало». Одиночная запись
 * раскрывать нечего — кнопки у неё нет.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const SERIES: ActivityRow = {
  id: 'h1', occurredAt: '2026-10-28T11:02:00Z', action: 'costs.invoice.lines', actionTitle: 'Строки счёта изменены',
  actorId: 'a1', actorName: 'Иван', target: 'Счёт № 41', before: '3 строки', after: '7 строк',
  count: 3, lastAt: '2026-10-28T11:19:00Z', firstTarget: 'Счёт № 14', sessionId: 's1',
};

const SINGLE: ActivityRow = {
  ...SERIES, id: 'p1', action: 'costs.invoice.parsed', actionTitle: 'Счёт разобран',
  before: null, after: null, count: 1, lastAt: SERIES.occurredAt, firstTarget: null, sessionId: null,
};

const RECORDS: ActivityRecord[] = [
  { ...SERIES, id: 'e1', before: '3 строки', after: '4 строки' },
  { ...SERIES, id: 'e2', occurredAt: '2026-10-28T11:10:00Z', before: '4 строки', after: '5 строк' },
  { ...SERIES, id: 'e3', occurredAt: '2026-10-28T11:19:00Z', before: '5 строк', after: '7 строк' },
];

function open(series: ActivityRow = SERIES) {
  vi.mocked(apiClient.get).mockImplementation((url: string) => Promise.resolve({
    data: url === '/activity' ? { total: 2, items: [series, SINGLE] }
      : url === '/activity/actions' ? []
      : url === '/activity/series/s1' ? RECORDS
      : undefined,
  }) as never);
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ActivityPage />
    </QueryClientProvider>,
  );
}

describe('журнал действий рядами', () => {
  it('серия — один ряд с числом, интервалом и прежним именем цели', async () => {
    open();

    const toggle = await screen.findByRole('button', { name: /^×3 · .+–.+: показать записи$/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByText('была: Счёт № 14')).toBeTruthy();
    // У одиночной записи раскрывать нечего.
    expect(screen.getAllByRole('button', { name: /показать записи/ })).toHaveLength(1);
  });

  it('раскрытие показывает записи серии, каждую со своим «было → стало»', async () => {
    open();

    fireEvent.click(await screen.findByRole('button', { name: /показать записи/ }));

    const list = await screen.findByRole('list', { name: 'Записи ряда' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    expect(within(list).getByText('4 строки', { selector: '.text-fg1' })).toBeTruthy();
    expect(apiClient.get).toHaveBeenCalledWith('/activity/series/s1', { params: { action: 'costs.invoice.lines' } });
    expect(screen.getByRole('button', { name: /свернуть записи/ }).getAttribute('aria-expanded')).toBe('true');
  });

  it('ряд длиннее потолка раскрытия говорит, что показана часть', async () => {
    open({ ...SERIES, count: 900 });

    fireEvent.click(await screen.findByRole('button', { name: /показать записи/ }));

    const list = await screen.findByRole('list', { name: 'Записи ряда' });
    expect(within(list).getByText('Показаны первые 3 из 900.')).toBeTruthy();
  });
});
