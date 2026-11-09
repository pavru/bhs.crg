// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Distribution } from '@/shared/api/planningDistribution';
import type { EstimateRevision } from '@/shared/api/planningEstimates';
import { distribution, item, line } from './distributionFixtures';
import { RevisionDistribution } from './RevisionDistribution';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));
vi.mock('@/shared/ui/Snackbar', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/ui/Snackbar')>()),
  useSnackbar: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), apiError: vi.fn() }),
}));

/**
 * Режим распределения карточки сметы (plan-10, задача 2.3, ТЗ PLAN-5.1): 409 — «перечитайте» с набранным на месте;
 * перенос решается в строке; правка — своим флагом, и у зафиксированной редакции.
 */

const REVISION = { id: 'r1', number: 3 } as EstimateRevision;

function serve(d: Distribution) {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/revisions/r1/distribution') return { data: d } as never;
    if (url === '/plan/revisions/r1/rows') return { data: { items: [], next: null } } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
}

function mount(initialQueue?: 'all' | 'carry-over') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <RevisionDistribution revision={REVISION} constructionId="c1" initialQueue={initialQueue} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const openSection = async () => fireEvent.click(await screen.findByRole('button', { name: /Раздел 1\. Кабельные линии/ }));

beforeEach(() => {
  permissions = ['plan.estimate.read', 'plan.estimate.edit'];
  window.matchMedia = vi.fn().mockReturnValue({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('окно строки', () => {
  it('409 — banner «перечитайте» с тем, кто изменил, и набранное остаётся после «Перечитать»', async () => {
    serve(distribution({ lines: [line({ source: 'parts', status: 'partial', targets: ['s1'],
      parts: [{ sectionId: 's1', quantity: { row: 7, plan: 700 } }], rest: { row: 5, plan: 500 } })] }));
    vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status: 409, data: {
      error: 'Распределение редакции тем временем изменено', code: 'distribution-changed', version: '5',
      changedBy: 'Петрова О.', changedAt: '2026-11-09T10:42:00Z',
    } } });
    mount();
    await openSection();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить…' }));
    const dialog = await screen.findByRole('dialog');
    const field = within(dialog).getByRole('textbox', { name: 'В ед. строки' });
    expect((field as HTMLInputElement).value).toBe('7');
    fireEvent.change(field, { target: { value: '8' } });
    expect((within(dialog).getByRole('textbox', { name: 'В ед. плана' }) as HTMLInputElement).value).toBe('800');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Записать' }));

    expect(await screen.findByText(/Петрова О\./)).toBeTruthy();
    expect(apiClient.put).toHaveBeenCalledWith('/plan/revisions/r1/distribution/lines',
      { lines: [{ rowId: 'l1', unit: 'row', parts: [{ sectionId: 's1', quantity: 8 }] }] }, { headers: { 'If-Match': '4' } });
    const before = vi.mocked(apiClient.get).mock.calls.filter(c => c[0] === '/plan/revisions/r1/distribution').length;
    fireEvent.click(screen.getByRole('button', { name: 'Перечитать' }));
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.filter(c => c[0] === '/plan/revisions/r1/distribution').length).toBe(before + 1));
    expect((within(screen.getByRole('dialog')).getByRole('textbox', { name: 'В ед. строки' }) as HTMLInputElement).value).toBe('8');
  });

  it('превышение называет обе единицы и не даёт записать', async () => {
    serve(distribution({ lines: [line({ source: 'parts', targets: ['s1'], parts: [{ sectionId: 's1', quantity: { row: 7, plan: 700 } }] })] }));
    mount();
    await openSection();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить…' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'В ед. строки' }), { target: { value: '13' } });
    expect(within(dialog).getByText(/13 · 100 м = 1\s300 м при 1\s200 м/)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Записать' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('раздел без количества не записывается: «Записать» не превращает строку в «к правилу» и не стирает части', async () => {
    serve(distribution({ lines: [line({ source: 'parts', targets: ['s1'], parts: [{ sectionId: 's1', quantity: { row: 7, plan: 700 } }] })] }));
    mount();
    await openSection();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить…' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'В ед. строки' }), { target: { value: '' } });
    expect(within(dialog).getByText(/У каждой части — раздел и количество/)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Записать' }) as HTMLButtonElement).disabled).toBe(true);
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  it('часть на удалённый раздел остаётся видна и не уходит на сервер, пока её не перенесли', async () => {
    serve(distribution({
      targets: {
        sections: [{ sectionId: 's1', name: 'Корпус 1', deleted: false, rows: 0 }, { sectionId: 's3', name: 'Корпус 3', deleted: true, rows: 1 }],
        wholeRows: 0, undistributedRows: 0,
      },
      lines: [line({ source: 'parts', targets: ['s3'], parts: [{ sectionId: 's3', quantity: { row: 7, plan: 700 } }] })],
    }));
    mount();
    await openSection();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить…' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Раздела части в стройке больше нет/)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Записать' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('экран', () => {
  it('отказ чтения строк редакции — отказом, а не вечным «читаем распределение»', async () => {
    const d = distribution();
    vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
      if (url === '/plan/revisions/r1/distribution') return { data: d } as never;
      throw { response: { status: 500, data: { error: 'Сервер упал' } } };
    });
    mount();
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('Читаем распределение…')).toBeNull();
  });

  it('действия выбора — только над видимыми строками: отмеченная и спрятанная отбором не уходит', async () => {
    serve(distribution({ lines: [line(), line({ rowId: 'l3', order: 3, number: '3', name: 'Монтаж светильника', source: 'rule',
      status: 'distributed', targets: ['s1'] })] }));
    mount();
    await openSection();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать строку 1' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать строку 3' }));
    expect(screen.getAllByText('Выбрано: 2').length).toBeGreaterThan(0);
    // Отбор итогом по целям: «не распределено» прячет строку 3, отметка с неё не снята.
    fireEvent.click(within(screen.getByRole('group', { name: 'Итог по целям' })).getByRole('button', { name: /не распределено/ }));
    await waitFor(() => expect(screen.getAllByText('Выбрано: 1').length).toBeGreaterThan(0));
  });
});

describe('перенос распределения', () => {
  const carry = (items = [item(), item({ id: 'i2', category: 'lost', number: '5', name: 'Позиция Z', candidates: [] })]) => distribution({
    carryOver: { counts: { sourceRevisionId: 'r2', sourceNumber: 2, carried: 280, shifted: 0, ambiguous: 1, overflow: 0, lost: 1 }, pending: items.length, items },
    counts: { rows: 2, distributed: 0, partial: 0, whole: 0, undistributed: 2, carryOverPending: items.length, changedAt: null },
  });

  it('ambiguous и lost показаны позициями и решаются в строке', async () => {
    serve(carry());
    vi.mocked(apiClient.post).mockResolvedValue({ data: { version: '5', previous: { lines: [] }, distribution: carry([]) } } as never);
    mount('carry-over');
    const ambiguous = (await screen.findByRole('rowheader', { name: /Позиция X/ })).closest('tr')!;
    expect(within(ambiguous).getByText('неоднозначно — выберите строку')).toBeTruthy();
    fireEvent.click(within(ambiguous).getByRole('button', { name: 'Не переносить' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/plan/revisions/r1/distribution/carry-over/i1',
      { action: 'skip' }, { headers: { 'If-Match': '4' } }));

    serve(carry());
    cleanup();
    mount('carry-over');
    const lost = (await screen.findByRole('rowheader', { name: /Позиция Z/ })).closest('tr')!;
    expect(within(lost).getByText('не перенесено')).toBeTruthy();
    expect(within(lost).queryByRole('button', { name: 'Не переносить' })).toBeNull();
    fireEvent.click(within(lost).getByRole('button', { name: 'Понятно' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/plan/revisions/r1/distribution/carry-over/i2',
      { action: 'acknowledge' }, { headers: { 'If-Match': '4' } }));
  });

  it('плашка называет источник и ведёт на чип «Перенос: проверить»', async () => {
    serve(carry());
    mount();
    expect(await screen.findByText(/перенесено из ред\. 2/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Проверить' }));
    expect(await screen.findByRole('rowheader', { name: /Позиция X/ })).toBeTruthy();
  });
});

describe('правка — своим флагом', () => {
  it('у зафиксированной редакции правило и части правятся, со словами о пометке графика', async () => {
    serve(distribution({ frozen: true }));
    mount();
    expect(await screen.findByText(/пометит график/)).toBeTruthy();
    expect(screen.getByRole('combobox', { name: /Правило: Раздел 1/ })).toBeTruthy();
    await openSection();
    expect(screen.getByRole('button', { name: 'Разделить…' })).toBeTruthy();
  });

  it('с одним read правила и части видны, действий нет', async () => {
    permissions = ['plan.estimate.read'];
    serve(distribution({ rules: [{ lsrSectionCode: '1', lsrSectionName: 'Кабельные линии', target: { kind: 'section', sectionId: 's1' }, rows: 2, exceptions: 0, undistributed: 0 }],
      lines: [line({ source: 'rule', status: 'distributed', targets: ['s1'] })] }));
    mount();
    await openSection();
    expect(screen.queryByRole('combobox', { name: /Правило/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Разделить…' })).toBeNull();
    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(screen.getByText('Корпус 1 · по разделу')).toBeTruthy();
  });

  it('у стройки без разделов — путь в карточку стройки, а не выключенный режим', async () => {
    serve(distribution({ targets: { sections: [], wholeRows: 0, undistributedRows: 2 } }));
    mount();
    expect(await screen.findByText('У стройки нет разделов')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Открыть стройку' }).getAttribute('href')).toBe('/document-sets/c1');
  });
});
