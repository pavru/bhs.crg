// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Estimate, EstimateRow, HiddenPart } from '@/shared/api/planningEstimates';
import { EstimateCard } from './EstimateCard';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn(), apiError: vi.fn() }) }));

/**
 * Карточка сметы (plan-09, задача 2.3, ТЗ PLAN-5.2, PLAN-2): без `plan.estimate.cost` колонки стоимости стоят в
 * состоянии «нет права» с именем права из каталога, и ни одной суммы на экране нет.
 */

const HIDDEN: HiddenPart[] = [{ code: 'cost', permission: 'plan.estimate.cost', name: 'видеть себестоимость сметы: стоимость строк, итоги, расценки' }];
const TOTAL = 412806;

function estimate(cost: boolean): Estimate {
  return {
    id: 'e1', constructionId: 'c1', sectionId: null, name: 'ЭОМ корпус 2', kind: 'main', version: '7',
    currentRevisionId: 'r1', currentRevisionNumber: 1,
    revisions: [
      {
        id: 'r1', number: 1, current: true, importedAt: '2026-10-20T00:00:00Z', importedBy: 'Смирнова А. В.',
        sourceFileName: 'ЛСР.gsfx', schemaVersion: null, rows: 1, unmatched: 1, materialsUnmatched: 0, unitChanged: 0,
        importId: null, partial: null, frozen: null, version: '3', ...(cost ? { cost: { total: TOTAL } } : {}),
      },
    ],
    ...(cost ? {} : { hidden: HIDDEN }),
  };
}

function line(cost: boolean): EstimateRow {
  return {
    id: 'l1', order: 1, lsrSection: { code: '9', name: 'Групповые сети' }, numberInSection: '1', number: '9.4',
    subordinate: false, kind: 'work', rateCode: 'ГЭСНм08-03-591-02', name: 'Розетка штепсельная', sourceVolume: 2.16,
    sourceUnit: '100 шт', workTypeId: null, unitId: null, factor: null, volume: null, planItemId: null,
    marks: [{ code: 'unmatched', text: 'Не сопоставлена: нет вида работы' }], materials: [], labor: [],
    ...(cost ? { cost: { unitCost: 1, total: TOTAL, data: {} } } : {}),
  };
}

function serve(cost: boolean) {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/estimates/e1') return { data: estimate(cost) } as never;
    if (url === '/plan/revisions/r1/rows') return { data: { items: [line(cost)], next: null, ...(cost ? {} : { hidden: HIDDEN }) } } as never;
    if (url === '/plan/revisions/r1/sections')
      return { data: { items: [{ code: '9', name: 'Групповые сети', rows: 1, unmatched: 1, materialsUnmatched: 0, units: [] }] } } as never;
    if (url === '/work-types') return { data: [] } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
}

function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <EstimateCard estimateId="e1" back={{ to: '/estimates', label: 'Сметы' }} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** Разделы ЛСР свёрнуты по умолчанию (макет П1) — строку показывает раскрытый раздел. */
async function openSection() {
  fireEvent.click(await screen.findByRole('button', { name: /Раздел 9/ }));
  return screen.findByText('Розетка штепсельная');
}

afterEach(() => { cleanup(); vi.clearAllMocks(); });
beforeEach(() => {
  permissions = ['plan.estimate.read'];
  window.matchMedia = vi.fn().mockReturnValue({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() });
});

describe('карточка сметы и право на стоимость', () => {
  it('без права: колонка стоимости — «скрыто правами», сумм нет ни в таблице, ни у редакции', async () => {
    serve(false);
    mount();
    await openSection();
    const head = screen.getByRole('columnheader', { name: /Стоимость/ });
    expect(head).toBeTruthy();
    expect(screen.getByText('скрыто правами')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/412\s?806/);
    expect(document.body.textContent).not.toMatch(/итог/);
  });

  it('с правом: стоимость строки и итог редакции видны', async () => {
    permissions = ['plan.estimate.read', 'plan.estimate.cost'];
    serve(true);
    mount();
    await openSection();
    expect(screen.queryByText('скрыто правами')).toBeNull();
    expect(document.body.textContent).toMatch(/итог/);
    expect(document.body.textContent).toMatch(/412\s?806/);
  });

  it('пометка строки — в клетке вида работы, а «Сделать действующей» без права правки не видно', async () => {
    serve(false);
    mount();
    const name = await openSection();
    const tr = name.closest('tr')!;
    expect(within(tr).getByText('не сопоставлена')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сделать действующей' })).toBeNull();
  });
});
