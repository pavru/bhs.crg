// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { SiteCosts } from '@/shared/api/siteCosts';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { SiteCostsPage } from './SiteCostsPage';

/**
 * Два взгляда «Затрат по стройке» (E2, issue #1100, PR 6b-3): полоса с переключателем — только когда
 * взгляды различаются; «на момент закрытия» — словом в адресе; стрелка в реестр — только у числа, которое
 * реестр под ней покажет. Чем ломается: стрелка у итога закрытого месяца ведёт в реестр, где сумма другая,
 * и человек сверяет закрытие с тем, чего в нём не было.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

const SITES = [{ id: 'a', name: 'Комарова 36' }, { id: 'b', name: 'Ленина 5' }];

const FILTER: SiteFilterValue = (() => {
  const state = filterState([], false, new Set(SITES.map(s => s.id)));
  return {
    status: 'ready', favorites: state.favorites, on: false, active: state.active, sites: SITES,
    visible: s => isVisible(s, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
  };
})();

/**
 * Ответ на взгляд, о котором спросили: «на момент закрытия» у Комарова 36 — другое число. Корректировки
 * закрытых периодов (PR 6d) — в любом ответе взгляда закрытия, и при совпавших взглядах тоже: показывать ли
 * их, решает экран.
 */
function report(closing: boolean, differs: SiteCosts['differs']): SiteCosts {
  const zero = { invoices: 0, amount: 0 };
  const diverged = closing && !!differs;
  const adjusted = <T extends object>(corrections: T) => closing ? { corrections } : {};
  return {
    site: null, from: '2026-09', to: '2026-09', withVat: true, months: ['09.2026'], articles: [], lost: null,
    unallocated: null, suppliers: [], unmatched: { invoices: 1, amount: 20 }, payable: zero, vatUnknown: null, payableVatUnknown: null,
    sites: [
      { id: 'a', name: 'Комарова 36', invoices: 1, amount: diverged ? 70 : 100, linked: !diverged,
        ...adjusted({ invoices: 1, amount: -30, whole: false }) },
      { id: 'b', name: 'Ленина 5', invoices: 1, amount: 20, linked: true, ...adjusted({ invoices: 1, amount: 20, whole: true }) },
      // Целиком из корректировок и без стрелки: место стрелки свободно, но значка в нём нет — у строки слово.
      ...(closing ? [{ id: 'c', name: 'Мира 1', invoices: 1, amount: 10, linked: false, corrections: { invoices: 1, amount: 10, whole: true } }] : []),
      // Смешанная и со стрелкой (число равно реестру): значок у неё всё равно есть — он не на месте стрелки.
      ...(closing ? [{ id: 'd', name: 'Садовая 2', invoices: 2, amount: 50, linked: true, corrections: { invoices: 1, amount: 5, whole: false } }] : []),
    ],
    total: { invoices: 2, amount: diverged ? 90 : 120, ...adjusted({ invoices: 2, amount: -10, whole: false }) },
    view: closing ? 'closing' : 'corrected', differs, totalLinked: !diverged, unmatchedLinked: true,
    other: differs ? { invoices: 2, amount: diverged ? 120 : 90 } : null,
    unmatchedNow: closing ? { invoices: 1, amount: 20 } : null,
  };
}

let differs: SiteCosts['differs'];

function mount(url = '/site-costs?from=2026-09&to=2026-09', filter = FILTER) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SiteFilterContext.Provider value={filter}>
        <MemoryRouter initialEntries={[url]}><SiteCostsPage /></MemoryRouter>
      </SiteFilterContext.Provider>
    </QueryClientProvider>,
  );
}

const asked = () => vi.mocked(apiClient.get).mock.calls
  .filter(([url]) => url === '/costs/site-costs')
  .map(([, c]) => (c as { params: { view?: string } }).params.view);

beforeEach(() => {
  differs = 'totals';
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async (url: string, config?: unknown) => {
    if (url === '/costs/constructions') return { data: SITES.map(s => ({ ...s, sections: [] })) };
    const params = (config as { params: { view?: string } }).params;
    return { data: report(params.view === 'closing', differs) };
  });
});
afterEach(cleanup);

const arrow = (name: string) => screen.queryByRole('link', { name: new RegExp(`^${name} — счета в реестре`) });

describe('два взгляда на закрытые периоды', () => {
  it('по умолчанию — «с исправлениями», со стрелками; полоса называет итог другого взгляда', async () => {
    mount();
    const band = await screen.findByRole('region', { name: 'Закрытые периоды' });
    expect(band.textContent).toContain('итоги взглядов различаются');
    expect(band.textContent).toContain('в другом взгляде итог');
    expect(screen.getByRole('tab', { name: 'С исправлениями' }).getAttribute('aria-selected')).toBe('true');
    expect(asked()).toEqual([undefined]);
    expect(arrow('Итого затраты')).not.toBeNull();
    expect(arrow('Комарова 36')).not.toBeNull();
    expect(screen.queryByText(/корректировк/)).toBeNull();
  });

  it('«на момент закрытия» спрашивается словом в адресе; стрелки — только у чисел, равных реестру', async () => {
    mount();
    fireEvent.click(await screen.findByRole('tab', { name: 'На момент закрытия' }));
    await waitFor(() => expect(asked()).toContain('closing'));
    await waitFor(() => expect(screen.getByRole('tab', { name: 'На момент закрытия' }).getAttribute('aria-selected')).toBe('true'));
    expect(screen.getByText(/· на момент закрытия/)).toBeTruthy();
    expect(arrow('Итого затраты')).toBeNull();
    expect(arrow('Комарова 36')).toBeNull();
    // Корректировки закрытых периодов (PR 6d, разбор Дизайнера п. 7): под итогом — «включая»; у смешанной
    // строки — значок после названия (не на месте стрелки: смешанная строка бывает и со стрелкой); у строки целиком
    // из корректировок — слово в названии и без значка.
    expect(screen.getByText(/включая корректировки закрытых периодов: 2 счёта/)).toBeTruthy();
    expect(screen.getByRole('img', { name: /^Включая корректировки закрытых периодов: 1 счёт, \S?30/ })).toBeTruthy();
    expect(screen.getAllByRole('img', { name: /корректировки закрытых периодов/ })).toHaveLength(2);
    expect(arrow('Садовая 2')).not.toBeNull();
    expect(screen.getByRole('img', { name: /^Включая корректировки закрытых периодов: 1 счёт, 5/ })).toBeTruthy();
    // Ленина 5 сошлась с реестром — её стрелка на месте.
    expect(arrow('Ленина 5')).not.toBeNull();
    expect(screen.getByText('Ленина 5').parentElement?.textContent).toContain('· корректировка');
    expect(screen.getByText('Мира 1').parentElement?.textContent).toContain('· корректировка');
    expect(screen.getByText('Комарова 36').closest('span')?.textContent).not.toContain('· корректировка');
    // «Без позиции» — не «из них» под итогом закрытия, а указатель на первый взгляд; он же переключает.
    expect(screen.queryByText(/из них без позиции/)).toBeNull();
    expect(screen.getByText(/Без позиции номенклатуры сейчас: 1 счёт/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'сумма и список во взгляде «с исправлениями»' }));
    await screen.findByRole('tab', { name: 'С исправлениями', selected: true });
    await screen.findByText(/из них без позиции номенклатуры/);
  });

  it('корректировки только у неизбранных строек — значок у свёрнутой строки, деньгами без счетов', async () => {
    const state = filterState(['a'], true, new Set(SITES.map(s => s.id)));
    mount('/site-costs?from=2026-09&to=2026-09&view=closing', { ...FILTER, on: true, active: state.active,
      favorites: state.favorites, visible: s => isVisible(s, state) });
    const folded = await screen.findByTestId('site-costs-folded');
    // Скрыта Ленина 5 (+20); Мира 1 и Садовая 2 справочнику неизвестны — они видны, как в списках.
    expect(folded.querySelector('[role="img"]')?.getAttribute('aria-label'))
      .toMatch(/^Включая корректировки закрытых периодов: 20/);
  });

  it('ноль счетов с корректировкой — не «оплаченных счетов нет»: пустота названа без противоречия строке под ней', async () => {
    vi.mocked(apiClient.get).mockImplementation(async (url: string) => url === '/costs/constructions'
      ? { data: SITES.map(s => ({ ...s, sections: [] })) }
      : { data: { ...report(true, 'totals'), sites: [],
        total: { invoices: 0, amount: 0, corrections: { invoices: 1, amount: -30, whole: false } } } });
    mount('/site-costs?from=2026-09&to=2026-09&view=closing');
    await screen.findByText('Затраты за период — ноль');
    expect(screen.queryByText('За период оплаченных счетов нет')).toBeNull();
    expect(screen.getByText(/включая корректировки закрытых периодов: 1 счёт/)).toBeTruthy();
  });

  it('вкладка следует адресу сразу, не дожидаясь ответа; вкладка указывает на панель затрат', async () => {
    mount();
    await screen.findByRole('region', { name: 'Закрытые периоды' });
    // Ответ на второй взгляд не приходит: на экране остаются прежние числа.
    vi.mocked(apiClient.get).mockImplementation(((url: string) => url === '/costs/constructions'
      ? Promise.resolve({ data: SITES.map(s => ({ ...s, sections: [] })) })
      : new Promise(() => {})) as never);
    fireEvent.click(screen.getByRole('tab', { name: 'На момент закрытия' }));
    const tab = await screen.findByRole('tab', { name: 'На момент закрытия', selected: true });
    expect(document.getElementById(tab.getAttribute('aria-controls')!)?.getAttribute('role')).toBe('tabpanel');
  });

  it('итоги равны — полоса говорит, что различаются дни, и итога соседа не повторяет', async () => {
    differs = 'details';
    mount();
    const band = await screen.findByRole('region', { name: 'Закрытые периоды' });
    expect(band.textContent).toContain('итоги равны, различаются разделы, поставщики или дни');
    expect(band.textContent).not.toContain('в другом взгляде итог');
  });

  it('взгляды совпали — ни полосы, ни подписи, даже если в адресе назван второй', async () => {
    differs = null;
    mount('/site-costs?from=2026-09&to=2026-09&view=closing');
    await screen.findByText('Комарова 36');
    expect(screen.queryByRole('region', { name: 'Закрытые периоды' })).toBeNull();
    expect(screen.queryByText(/· на момент закрытия/)).toBeNull();
    expect(arrow('Итого затраты')).not.toBeNull();
    // На экране числа основной таблицы — и корректировок взгляда закрытия при них нет.
    expect(screen.queryByText(/корректировк/)).toBeNull();
    expect(screen.queryByRole('img', { name: /корректировки/ })).toBeNull();
  });
});
