// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { PeriodsPage } from './PeriodsPage';
import { companyClosingNote, hiddenSitesNote, shownSitesCount } from './periodSites';

/**
 * «Учётный период» под фильтром «Избранные стройки» (issue #1220): урезана только таблица.
 *
 * <p>Сторож — закрытие периода компании: под включённым фильтром на сервер уходят те же запросы, что и
 * без него, — тот же перечень, тот же отпечаток, то же закрытие. Фильтр, просочившийся в операцию,
 * закрыл бы период компании «по трём стройкам»; здесь это видно как расхождение запросов.</p>
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/api/access', () => ({ useCan: () => ({ permission: () => true, module: () => true }) }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const SITES = [
  { id: 'a', name: 'ЖК «Север»' }, { id: 'b', name: 'Школа №7' },
  { id: 'c', name: 'ТЦ «Меридиан»' }, { id: 'd', name: 'Склад-2' },
];
const contour = (constructionId: string | null) => ({
  constructionId, closedThrough: '2026-08-31', ownClosedThrough: constructionId ? null : '2026-08-31',
  expectedFrom: '2026-09-01', reopenable: null,
});
const PERIODS = { today: '2026-10-15', company: contour(null), constructions: SITES.map(s => contour(s.id)) };
// Закрытие неизбранной стройки в истории: её имя обязано прийти из полного справочника.
const HISTORY = [{
  id: 'h1', kind: 'Close', contour: 'Construction', constructionId: 'c', from: '2026-08-01', through: '2026-08-31',
  at: '2026-09-01T10:00:00Z', byName: 'Бухгалтер', reason: null, report: null,
}];

function answer() {
  vi.mocked(apiClient.get).mockImplementation((url: string) => Promise.resolve({
    data: url === '/periods' ? PERIODS : url === '/periods/history' ? HISTORY : url === '/constructions' ? SITES : null,
  }));
  vi.mocked(apiClient.post).mockImplementation((url: string) =>
    Promise.resolve({ data: url.endsWith('/preview') ? { stamp: 's1', sections: [] } : {} }));
}

/** Фильтр по настоящему правилу: избранные «Север» и «Школа», или избранного нет вовсе. */
function filterOf(active: boolean): SiteFilterValue {
  const state = filterState(active ? ['a', 'b'] : [], true, new Set(SITES.map(s => s.id)));
  return {
    status: 'ready', favorites: state.favorites, on: true, active: state.active, sites: SITES,
    visible: sites => isVisible(sites, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
  };
}

const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

async function shown(active: boolean) {
  answer();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ToastProvider>
        <SiteFilterContext.Provider value={filterOf(active)}><PeriodsPage /></SiteFilterContext.Provider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  for (let i = 0; i < 4; i++) await tick();
}

/** Имена контуров в таблице границ — первой таблице страницы. */
const contours = () => within(screen.getAllByRole('table')[0]).getAllByRole('row').slice(1)
  .map(row => row.querySelector('td')!.textContent);

/** Открыть закрытие компании, прочитать пояснение и закрыть; вернуть всё, что ушло на сервер. */
async function closeCompany() {
  const company = within(screen.getAllByRole('table')[0]).getAllByRole('row')[1];
  fireEvent.click(within(company).getByRole('button', { name: 'Закрыть период' }));
  for (let i = 0; i < 4; i++) await tick();
  const dialog = screen.getByRole('dialog');
  const note = dialog.textContent!;
  fireEvent.click(within(dialog).getByRole('button', { name: 'Закрыть период' }));
  for (let i = 0; i < 4; i++) await tick();
  return { note, sent: vi.mocked(apiClient.post).mock.calls };
}

describe('Учётный период под фильтром избранного (#1220)', () => {
  it('таблица — компания и избранные; число скрытых сверху и под таблицей', async () => {
    await shown(true);
    expect(contours()).toEqual(['Компания', 'ЖК «Север»', 'Школа №7']);
    expect(screen.getByTestId('period-sites-count').textContent).toBe('2 из 4 строек');
    expect(screen.getByTestId('site-filter-note').textContent).toContain('Ещё 2 стройки скрыто фильтром избранного');
  });

  it('без фильтра — все стройки, без свидетельства', async () => {
    await shown(false);
    expect(contours()).toEqual(['Компания', ...SITES.map(s => s.name)]);
    expect(screen.queryByTestId('period-sites-count')).toBeNull();
    expect(screen.queryByTestId('site-filter-note')).toBeNull();
  });

  it('неизбранная стройка в истории — по имени, а не «удалена»', async () => {
    await shown(true);
    const history = screen.getAllByRole('table')[1];
    expect(history.textContent).toContain('ТЦ «Меридиан»');
    expect(history.textContent).not.toContain('Стройка удалена');
  });

  it('закрытие компании под фильтром уходит тем же, что без него, и называет скрытые', async () => {
    await shown(true);
    const filtered = await closeCompany();
    cleanup();
    vi.clearAllMocks();
    await shown(false);
    const plain = await closeCompany();

    expect(filtered.note).toContain('все стройки — 4, в том числе 2, скрытых фильтром избранного');
    expect(plain.note).toContain('все стройки — 4.');
    // Положительный якорь: без закрытия сравнение двух пустых списков прошло бы всегда.
    expect(plain.sent.map(([url]) => url)).toEqual(['/periods/close/preview', '/periods/close']);
    expect(filtered.sent).toEqual(plain.sent);
  });
});

describe('тексты периода под фильтром', () => {
  it('склоняются по числу', () => {
    expect(shownSitesCount(3, 14)).toBe('3 из 14 строек');
    expect(shownSitesCount(1, 21)).toBe('1 из 21 стройки');
    expect(hiddenSitesNote(1)).toBe('Ещё 1 стройка скрыта фильтром избранного');
    expect(hiddenSitesNote(11)).toBe('Ещё 11 строек скрыто фильтром избранного');
    expect(companyClosingNote({ total: 14, hidden: 11 }))
      .toBe('Закрытие компании закрывает и все стройки — 14, в том числе 11, скрытых фильтром избранного.');
    expect(companyClosingNote({ total: 2, hidden: 1 })).toContain('в том числе 1, скрытая фильтром избранного');
    expect(companyClosingNote({ total: 14, hidden: 0 })).toBe('Закрытие компании закрывает и все стройки — 14.');
  });
});
