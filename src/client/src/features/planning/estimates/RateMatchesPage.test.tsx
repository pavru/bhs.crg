// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import { RateMatchesPage } from './RateMatchesPage';
import { WORK_TYPES } from './matchFixtures';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), apiError: vi.fn() }));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => toast }));
vi.mock('@/shared/hooks/useMedia', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useMedia')>()), useMedia: () => true,
}));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));

/**
 * Список запомненного (plan-09, задача 4.3, ТЗ PLAN-6.1): «перенацелить» при 409 говорит словами сервера и не
 * теряет набранного; «забыть» — с подтверждением и версией записи; без права правки — только чтение.
 */

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});

let version = '3';
const match = () => ({
  id: 'm1', version, rateCode: 'ГЭСНм08-03-591-02', normativeBase: null, workTypeId: 'w1', workTypeName: 'Прокладка кабеля до 1 кВ',
  unitId: 'u-m', unitName: 'м', factor: 100, sourceUnit: '100 м', issue: null, updatedAt: '2026-10-20T10:00:00Z', updatedBy: 'Смирнова А. В.',
});

beforeEach(() => {
  version = '3';
  permissions = ['plan.estimate.read', 'plan.estimate.edit'];
  vi.mocked(apiClient.get).mockImplementation(async (url: string, config?: { params?: Record<string, unknown> }) => {
    if (url === '/plan/rate-matches') return { data: { items: [match()], total: 1 } } as never;
    if (url === '/work-types') return { data: WORK_TYPES } as never;
    if (url === '/plan/unit-factor')
      return { data: { factor: 1, sourceUnit: config?.params?.sourceUnit, unitId: config?.params?.unitId, unit: 'шт', reason: null } } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter><RateMatchesPage /></MemoryRouter>
    </QueryClientProvider>,
  );
}

async function rowAction(name: string) {
  fireEvent.keyDown(await screen.findByRole('button', { name: 'Действия' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('menuitem', { name }));
}

describe('запомненные шифры', () => {
  it('перенацелить: 409 — причина словами сервера, окно открыто с выбором; повтор — по перечитанной версии', async () => {
    mount();
    expect(await screen.findByText('ГЭСНм08-03-591-02')).toBeTruthy();
    await rowAction('Перенацелить…');

    const dialog = await screen.findByRole('dialog');
    const select = within(dialog).getByRole('combobox', { name: 'Вид работы' });
    await waitFor(() => expect(select.textContent).toContain('Прокладка кабеля до 1 кВ'));
    fireEvent.keyDown(select, { key: 'Enter' });
    fireEvent.click(await screen.findByRole('option', { name: '02 Монтаж электроустановочных изделий' }));
    // Другой вид работы — коэффициент снова предлагает сервер; до ответа перенацеливать нечем.
    await within(dialog).findByText(/коэффициент предложен единицами/);

    vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status: 409, data: {
      error: 'Запомненное сопоставление тем временем изменили — перечитайте список запомненного и повторите.',
    } } });
    version = '4';
    fireEvent.click(within(dialog).getByRole('button', { name: 'Перенацелить' }));

    expect((await within(dialog).findByRole('alert')).textContent).toMatch(/тем временем изменили — перечитайте/);
    expect(apiClient.put).toHaveBeenCalledWith('/plan/rate-matches/m1', expect.objectContaining({ workTypeId: 'w2' }),
      { headers: { 'If-Match': '3' } });
    expect(within(dialog).getByRole('combobox', { name: 'Вид работы' }).textContent).toContain('Монтаж электроустановочных изделий');

    // Список перечитан — повтор называет новую версию записи.
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.filter(([u]) => u === '/plan/rate-matches').length).toBeGreaterThan(1));
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { ...match(), workTypeId: 'w2', workTypeName: 'Монтаж электроустановочных изделий' } } as never);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Перенацелить' }));
    await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/plan/rate-matches/m1',
      expect.objectContaining({ workTypeId: 'w2' }), { headers: { 'If-Match': '4' } }));
  });

  it('забыть — с подтверждением и версией записи', async () => {
    mount();
    await rowAction('Забыть…');
    expect(await screen.findByText(/перестанут подставлять шифр «ГЭСНм08-03-591-02»/)).toBeTruthy();
    expect(apiClient.delete).not.toHaveBeenCalled();
    vi.mocked(apiClient.delete).mockResolvedValueOnce({} as never);
    fireEvent.click(screen.getByRole('button', { name: 'Забыть' }));
    await waitFor(() => expect(apiClient.delete).toHaveBeenCalledWith('/plan/rate-matches/m1', { headers: { 'If-Match': '3' } }));
  });

  it('без права правки — список читается, действий нет', async () => {
    permissions = ['plan.estimate.read'];
    mount();
    expect(await screen.findByText('ГЭСНм08-03-591-02')).toBeTruthy();
    expect(screen.getByText('100 м → м × 100')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Действия' })).toBeNull();
  });
});
