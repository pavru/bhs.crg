// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { ConstructionProgress, ProgressRow, ProgressValue } from '@/shared/api/progress';
import { ProgressPanel } from './ProgressPanel';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

const path = '/constructions/site/progress';
const unit = 'м';
const volume = (value: number | null): ProgressValue => ({ volume: value, start: null, end: null });
const row = (id: string, name: string, cells: ProgressRow['cells']): ProgressRow =>
  ({ workTypeId: id, unitId: 'meter', workTypeName: name, unitName: unit, cells });
const revision = '/document-sets/site/estimates/est?revision=rev&queue=unmatched';
const payload: ConstructionProgress = {
  constructionId: 'site', sectionId: null,
  columns: [
    { code: 'plan:estimate', title: 'План по смете', kind: 'Volume', state: 'ok' },
    { code: 'fact', title: 'Факт по учёту', kind: 'Volume', state: 'partial' },
    { code: 'plan:schedule', title: 'Срок по графику', kind: 'Dates', state: 'ok' },
  ],
  rows: [
    row('planned', 'Лотки', { 'plan:estimate': volume(20), 'plan:schedule': { start: '2026-11-03', end: null, volume: null } }),
    row('fact', 'Кабель', { fact: volume(12) }),
    row('zero', 'Щиты', { 'plan:estimate': volume(0), fact: volume(null) }),
  ],
  strips: [
    { code: 'fact-scope', text: 'Диагностика факта по всей стройке за весь период', state: 'partial' },
    { code: 'fact-crew-multiple-keys', text: 'Бригадных групп — разные позиции, разделы или единицы: 2', state: 'partial' },
    { code: 'estimate-unmapped', text: 'Строк сметы не сопоставлено: 17', state: 'ok', link: revision },
  ],
  costs: { state: 'ok', totals: { allocated: 1200, paid: 890, linkedToWorks: {
    state: 'not-tracked', reason: 'разноска затрат по работам не ведётся',
  } } },
};
let response: ConstructionProgress;

beforeEach(() => {
  response = payload;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener() {}, removeEventListener() {} }));
  vi.mocked(apiClient.get).mockReset().mockImplementation(async (url, config) => {
    if (url === '/constructions/site') return { data: { sections: [{ id: 'section', name: 'Корпус 1' }] } };
    if (url === path) {
      const section = config?.params?.section;
      return { data: section ? { ...response, sectionId: section, rows: [response.rows[1]] } : response };
    }
    throw new Error(`Unexpected request: ${url}`);
  });
});

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

function mount(sectionId?: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><MemoryRouter>
    <ProgressPanel constructionId="site" sectionId={sectionId} />
  </MemoryRouter></QueryClientProvider>);
}

const workRow = (name: string) => within(screen.getByRole('rowheader', { name: new RegExp(name) }).closest('tr')!);

it('на узкой панели оставляет план и факт рядом, выводит срок в названии и отличает пустой объём от нуля', async () => {
  mount();
  await screen.findByRole('table');
  expect(screen.queryByRole('complementary')).toBeNull();
  expect(screen.queryByRole('columnheader', { name: 'Срок по графику' })).toBeNull();
  expect(screen.getByRole('rowheader', { name: /Лотки.*Срок по графику/ })).toBeTruthy();
  expect(workRow('Кабель').getByText(/^12\sм$/)).toBeTruthy();
  expect(workRow('Лотки').getByLabelText('Нет включённого объёма')).toBeTruthy();
  expect(workRow('Щиты').getByText(/^0\sм$/)).toBeTruthy();
  expect(screen.getByText('Факт неполный')).toBeTruthy();
  expect(screen.getByText(/по всей стройке за весь период/)).toBeTruthy();
  expect(screen.getByText(/разные позиции, разделы или единицы: 2/)).toBeTruthy();
  expect(screen.getByRole('link', { name: /не сопоставлено/ }).getAttribute('href')).toBe(revision);
  expect(screen.getByText(/из них оплачено/)).toBeTruthy();
  expect(screen.getByText('разноска затрат по работам не ведётся')).toBeTruthy();
});

it('на широкой панели показывает сроки выбранной строки и меняет их с клавиатуры', async () => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ width: 832 } as DOMRect);
  mount();
  const details = within(await screen.findByRole('complementary', { name: 'Подробности работы' }));
  expect(details.getByRole('heading', { name: 'Лотки' })).toBeTruthy();
  expect(details.getByText('Срок по графику')).toBeTruthy();
  expect(details.getByText(/С 03/)).toBeTruthy();
  fireEvent.keyDown(screen.getByRole('rowheader', { name: 'Кабель' }).closest('tr')!, { key: 'Enter' });
  expect(details.getByRole('heading', { name: 'Кабель' })).toBeTruthy();
  expect(details.getByText(/^12\sм$/)).toBeTruthy();
  expect(details.queryByText(/^20\sм$/)).toBeNull();
});

it('сохраняет шесть знаков плана и четыре знака факта, отличая их от нуля', async () => {
  response = { ...payload, rows: [row('small', 'Провод', {
    'plan:estimate': volume(0.000001), fact: volume(0.0001),
  })] };
  mount();
  await screen.findByRole('rowheader', { name: /Провод/ });
  expect(workRow('Провод').getByText('0,000001 м')).toBeTruthy();
  expect(workRow('Провод').getByText('0,0001 м')).toBeTruthy();
  expect(workRow('Провод').queryByText('0 м')).toBeNull();
});

it('на узкой панели показывает нужное право на факт даже при пустой таблице', async () => {
  response = { ...payload, rows: [], strips: [], columns: [
    { ...payload.columns[1], state: 'hidden', permission: 'work.facts.read' },
  ] };
  mount();
  await screen.findByRole('table');
  expect(screen.getByText('work.facts.read').closest('[role="status"]')).toBeTruthy();
  expect(screen.queryByRole('complementary')).toBeNull();
});

it('запрашивает выбранный раздел и оставляет предупреждение в охвате стройки', async () => {
  mount();
  const filter = await screen.findByRole('combobox', { name: 'Раздел стройки' });
  fireEvent.keyDown(filter, { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name: 'Корпус 1' }));
  await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith(path, expect.objectContaining({ params: { section: 'section' } })));
  await screen.findByRole('rowheader', { name: /Кабель/ });
  expect(screen.queryByRole('rowheader', { name: /Лотки/ })).toBeNull();
  expect(screen.getByText(/по всей стройке за весь период/)).toBeTruthy();
  expect(screen.getByText('Затраты · выбранный раздел')).toBeTruthy();
});

it('на странице раздела с первого запроса читает только этот раздел', async () => {
  mount('section');
  await screen.findByRole('rowheader', { name: /Кабель/ });
  expect(screen.queryByRole('combobox', { name: 'Раздел стройки' })).toBeNull();
  expect(vi.mocked(apiClient.get).mock.calls.filter(([url]) => url === path)).toEqual([
    [path, expect.objectContaining({ params: { section: 'section' } })],
  ]);
});

it('скрывает денежные суммы и значения недоступных колонок, показывая права', async () => {
  response = { ...payload, columns: [
    { ...payload.columns[0], state: 'hidden', permission: 'plan.estimate.read' }, payload.columns[1],
  ], strips: [
    { code: 'estimate', state: 'hidden', permission: 'plan.estimate.read', text: 'скрыто', link: revision },
  ], costs: { state: 'hidden', permission: 'costs.totals.read' } };
  mount();
  const table = await screen.findByRole('table');
  expect(within(table).getAllByText('скрыто правами')).toHaveLength(3);
  expect(screen.getByText('plan.estimate.read')).toBeTruthy();
  expect(screen.getByText('costs.totals.read')).toBeTruthy();
  expect(screen.queryByText(/^20\sм$/)).toBeNull();
  expect(screen.queryByText(/оплачено|890/)).toBeNull();
  expect(screen.queryByRole('link')).toBeNull();
});

it('при выключенном Work показывает полосу и продолжает показывать план', async () => {
  response = { ...payload, columns: [payload.columns[0]], rows: [payload.rows[0]], strips: [
    { code: 'fact', text: 'Учёт работ не подключён', state: 'not-connected' },
  ], costs: { state: 'not-connected', text: 'Счета и накладные не подключены' } };
  mount();
  await screen.findByText('Учёт работ не подключён');
  expect(screen.getByText('Счета и накладные не подключены')).toBeTruthy();
  expect(screen.queryByRole('columnheader', { name: /Факт/ })).toBeNull();
  expect(workRow('Лотки').getByText(/^20\sм$/)).toBeTruthy();
});
