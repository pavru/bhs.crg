// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EstimateVsCosts } from './EstimateVsCosts';
import { comparisonGroup } from './estimateVsCostsRows';
import { SiteCostsPage } from './SiteCostsPage';
import { useEstimateVsCosts, useEstimateVsCostsConstructions, exportEstimateVsCosts, type EstimateVsCostsRow, type EstimateVsCostsView } from '@/shared/api/estimateVsCosts';

vi.mock('@/shared/api/estimateVsCosts', () => ({
  useEstimateVsCosts: vi.fn(), useEstimateVsCostsConstructions: vi.fn(), exportEstimateVsCosts: vi.fn(),
}));
const row: EstimateVsCostsRow = {
  nomenclatureId: 'cable', name: 'Кабель', allocated: 120000, paid: 90000,
  allocatedVatUnknown: false, paidVatUnknown: false,
  allocatedIncomplete: false, paidIncomplete: false,
  estimate: { state: 'shown', amount: 100000, basisUnknown: false }, difference: 20000,
};
const report: EstimateVsCostsView = {
  constructionId: 'site', sectionId: null, construction: 'Комарова, 36', section: null,
  estimateState: 'shown', permission: null, message: null, rows: [row], allocated: 120000, paid: 90000, undecided: 0,
  estimateCurrency: 'RUB', factCurrency: 'RUB', currencyMismatch: false,
};

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount(data: EstimateVsCostsView | undefined, entry = false) {
  vi.mocked(useEstimateVsCostsConstructions).mockReturnValue({
    data: [{ id: 'site', name: 'Комарова, 36', sections: [] }], isPending: false,
  } as unknown as ReturnType<typeof useEstimateVsCostsConstructions>);
  vi.mocked(useEstimateVsCosts).mockReturnValue({
    data, isError: false, isFetching: data === undefined,
  } as ReturnType<typeof useEstimateVsCosts>);
  return render(<QueryClientProvider client={new QueryClient()}>
    <MemoryRouter initialEntries={['/site-costs?tab=estimate&site=site']}>
      {entry ? <SiteCostsPage /> : <EstimateVsCosts />}
    </MemoryRouter>
  </QueryClientProvider>);
}

it('открывает выбранную вкладку через обычную страницу и группирует превышение', () => {
  mount(report, true);
  expect(screen.getByRole('tab', { name: 'План против факта' }).getAttribute('aria-selected')).toBe('true');
  expect(screen.getByRole('heading', { name: 'Превышение сметы' })).toBeTruthy();
  expect(screen.getByText('Кабель')).toBeTruthy();
});

it('показывает скрытую смету словами с правом и сохраняет доступные суммы', () => {
  mount({ ...report, estimateState: 'hidden', permission: 'plan.estimate.cost',
    message: 'Скрыто правами — нужно право plan.estimate.cost',
    rows: [{ ...row, estimate: { state: 'hidden', basisUnknown: false }, difference: undefined }] });
  expect(screen.getByRole('heading', { name: 'Сравнение недоступно' })).toBeTruthy();
  expect(screen.getAllByText(/plan.estimate.cost/).length).toBeGreaterThan(0);
  expect(screen.queryByText(/100.000/)).toBeNull();
  expect(screen.getByText(/120.000/)).toBeTruthy();
});

it('различает отсутствие сметы, неподтверждённую основу и известный ноль', () => {
  const missing = { ...row, estimate: { state: 'not-in-estimate' as const, basisUnknown: false }, difference: undefined };
  const unknown = { ...row, nomenclatureId: 'washer', name: 'Шайба',
    estimate: { ...row.estimate, basisUnknown: true }, difference: undefined };
  mount({ ...report, rows: [missing, unknown, { ...row, nomenclatureId: 'water', name: 'Вода', difference: 0 }] });
  const cableRow = screen.getByText('Кабель').closest('tr')!;
  expect(within(cableRow).getAllByText(/Нет в смете/).length).toBeGreaterThan(0);
  expect(within(cableRow).queryByText(/^0[,.]00/)).toBeNull();
  expect(screen.getAllByText(/Основа сметной суммы/).length).toBeGreaterThan(0);
  expect(screen.getByRole('heading', { name: 'Совпадает со сметой' })).toBeTruthy();
  expect(comparisonGroup(missing)).toBe('Сравнение недоступно');
});

it('во время запроса показывает загрузку и блокирует выгрузку', () => {
  mount(undefined);
  expect(screen.getByRole('status').textContent).toContain('Отчёт строится');
  expect((screen.getByRole('button', { name: 'Выгрузить XLSX' }) as HTMLButtonElement).disabled).toBe(true);
});

it('называет нулевой subtotal известной частью при доле без цены', () => {
  mount({ ...report, rows: [{ ...row, allocated: 0, allocatedIncomplete: true, difference: undefined }] });
  expect(screen.getByText('Известная часть')).toBeTruthy();
  expect(screen.getByText('Стоимость остальных долей неизвестна')).toBeTruthy();
  expect(screen.getByRole('heading', { name: 'Сравнение недоступно' })).toBeTruthy();
  expect(screen.queryByRole('heading', { name: 'В пределах сметы' })).toBeNull();
});

it('при разных валютах подписывает обе колонки и оставляет сравнение неопределённым', () => {
  mount({ ...report, factCurrency: 'USD', currencyMismatch: true,
    message: 'Смета — RUB, факт — USD.', rows: [{ ...row, difference: undefined }] });
  expect(screen.getByRole('columnheader', { name: 'Смета без НДС, RUB' })).toBeTruthy();
  expect(screen.getByRole('columnheader', { name: 'Разнесено без НДС, USD' })).toBeTruthy();
  expect(screen.getByText(/Разные валюты сметы и факта/)).toBeTruthy();
  expect(screen.getByRole('heading', { name: 'Сравнение недоступно' })).toBeTruthy();
});

it('выгружает тот же отбор', async () => {
  mount(report);
  fireEvent.click(screen.getByRole('button', { name: 'Выгрузить XLSX' }));
  expect(exportEstimateVsCosts).toHaveBeenCalledWith({ site: 'site', section: null, search: '', onlyExcess: false });
  await vi.waitFor(() => expect((screen.getByRole('button', { name: 'Выгрузить XLSX' }) as HTMLButtonElement).disabled).toBe(false));
});
