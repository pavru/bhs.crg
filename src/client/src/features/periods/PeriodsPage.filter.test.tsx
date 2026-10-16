// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { PeriodsPage } from './PeriodsPage';
import { companyClosingNote, companyReopenNote, shownSitesCount } from './periodSites';

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
// Закрытие компании, которое можно отменить; «Школа» закрыта своим закрытием — её отмена не откроет.
const REOPENABLE = {
  ...PERIODS,
  company: { ...contour(null), reopenable: { from: '2026-08-01', through: '2026-08-31', keptClosed: ['b'] } },
};
// Закрытие неизбранной стройки в истории: её имя обязано прийти из полного справочника.
const HISTORY = [{
  id: 'h1', kind: 'Close', contour: 'Construction', constructionId: 'c', from: '2026-08-01', through: '2026-08-31',
  at: '2026-09-01T10:00:00Z', byName: 'Бухгалтер', reason: null, report: null,
}];

function answer(periods: unknown) {
  vi.mocked(apiClient.get).mockImplementation((url: string) => Promise.resolve({
    data: url === '/periods' ? periods : url === '/periods/history' ? HISTORY : url === '/constructions' ? SITES : null,
  }));
  vi.mocked(apiClient.post).mockImplementation((url: string) =>
    Promise.resolve({ data: url.endsWith('/preview') ? { stamp: 's1', sections: [] } : {} }));
}

interface Filter {
  /** Избранное; по умолчанию — «Север» и «Школа», пусто — фильтра нет. */
  favorites?: string[];
  /** Стройки в кэше справочника фильтра; по умолчанию — все. */
  live?: string[];
  status?: SiteFilterValue['status'];
}

/** Фильтр по настоящему правилу. */
function filterOf({ favorites = ['a', 'b'], live = SITES.map(s => s.id), status = 'ready' }: Filter): SiteFilterValue {
  const state = filterState(favorites, true, new Set(live));
  return {
    status, favorites: state.favorites, on: true, active: status === 'ready' && state.active,
    sites: SITES.filter(s => live.includes(s.id)),
    visible: sites => isVisible(sites, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
  };
}

const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

const NO_FILTER: Filter = { favorites: [] };

async function shown(filter: Filter = {}, periods: unknown = PERIODS) {
  answer(periods);
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ToastProvider>
        <SiteFilterContext.Provider value={filterOf(filter)}><PeriodsPage /></SiteFilterContext.Provider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  for (let i = 0; i < 4; i++) await tick();
}

/** Имена контуров в таблице границ — первой таблице страницы. */
const contours = () => within(screen.getAllByRole('table')[0]).getAllByRole('row').slice(1)
  .map(row => row.querySelector('td')!.textContent);
/** Стройки ещё не известны — строка под сеткой, а не строка контура. */
const loadingSites = () => screen.queryByText('Загрузка строек...');

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
    await shown();
    expect(contours()).toEqual(['Компания', 'ЖК «Север»', 'Школа №7']);
    expect(screen.getByTestId('period-sites-count').textContent).toBe('2 из 4 строек');
    expect(screen.getByTestId('site-filter-note').textContent).toContain('Избранные стройки · скрыто: 2');
  });

  it('стройка, которой ещё нет в кэше справочника, — не в избранном, а не «без стройки»', async () => {
    // Завели с другого компьютера: границы периода её уже знают, справочник вкладки — ещё нет.
    await shown({ live: ['a', 'b', 'c'] });
    expect(contours()).toEqual(['Компания', 'ЖК «Север»', 'Школа №7']);
    expect(screen.getByTestId('period-sites-count').textContent).toBe('2 из 4 строек');
  });

  it('в избранном все стройки — таблица не названа урезанной', async () => {
    await shown({ favorites: SITES.map(s => s.id) });
    expect(contours()).toEqual(['Компания', ...SITES.map(s => s.name)]);
    expect(screen.queryByTestId('period-sites-count')).toBeNull();
    expect(screen.queryByTestId('site-filter-note')).toBeNull();
  });

  it('пока фильтр не известен — компания с кнопками, строек нет', async () => {
    await shown({ status: 'loading' });
    expect(contours()).toEqual(['Компания']);
    expect(loadingSites()).not.toBeNull();
    expect(screen.getAllByRole('button', { name: 'Закрыть период' })).toHaveLength(1);
  });

  it('без фильтра — все стройки, без свидетельства', async () => {
    await shown(NO_FILTER);
    expect(contours()).toEqual(['Компания', ...SITES.map(s => s.name)]);
    expect(screen.queryByTestId('period-sites-count')).toBeNull();
    expect(screen.queryByTestId('site-filter-note')).toBeNull();
  });

  it('неизбранная стройка в истории — по имени, а не «удалена»', async () => {
    await shown();
    const history = screen.getAllByRole('table')[1];
    expect(history.textContent).toContain('ТЦ «Меридиан»');
    expect(history.textContent).not.toContain('Стройка удалена');
  });

  it('закрытие компании под фильтром уходит тем же, что без него, и называет скрытые', async () => {
    await shown();
    const filtered = await closeCompany();
    cleanup();
    vi.clearAllMocks();
    await shown(NO_FILTER);
    const plain = await closeCompany();

    expect(filtered.note).toContain('все стройки — 4, в том числе 2, скрытых фильтром избранного');
    expect(plain.note).toContain('все стройки — 4.');
    // Положительный якорь: без закрытия сравнение двух пустых списков прошло бы всегда.
    expect(plain.sent.map(([url]) => url)).toEqual(['/periods/close/preview', '/periods/close']);
    expect(filtered.sent).toEqual(plain.sent);
  });

  it('отмена закрытия компании называет скрытые стройки, которым откроет дни', async () => {
    await shown({}, REOPENABLE);
    const company = within(screen.getAllByRole('table')[0]).getAllByRole('row')[1];
    fireEvent.click(within(company).getByRole('button', { name: 'Отменить закрытие' }));
    await tick();
    // «Школа» закрыта своим закрытием и названа отдельно; дни откроются «Северу» и двум скрытым.
    expect(screen.getByRole('dialog').textContent)
      .toContain('Дни откроются и у строек — 3, в том числе 2, скрытых фильтром избранного.');
  });
});

describe('тексты периода под фильтром', () => {
  it('склоняются по числу', () => {
    expect(shownSitesCount(3, 14)).toBe('3 из 14 строек');
    expect(shownSitesCount(1, 21)).toBe('1 из 21 стройки');
    expect(companyClosingNote({ total: 14, hidden: 11 }))
      .toBe('Закрытие компании закрывает и все стройки — 14, в том числе 11, скрытых фильтром избранного.');
    expect(companyClosingNote({ total: 2, hidden: 1 })).toContain('в том числе 1, скрытая фильтром избранного');
    expect(companyClosingNote({ total: 14, hidden: 0 })).toBe('Закрытие компании закрывает и все стройки — 14.');
    // Строек нет — без числа: «все стройки — 0» ничего не говорит.
    expect(companyClosingNote({ total: 0, hidden: 0 })).toBe('Закрытие компании закрывает и все стройки.');
    expect(companyReopenNote({ total: 0, hidden: 0 })).toBeNull();
    expect(companyReopenNote({ total: 5, hidden: 0 })).toBe('Дни откроются и у строек — 5.');
  });
});
