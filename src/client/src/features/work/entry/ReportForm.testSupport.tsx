import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { EntrySites, ReportInput, ReportMode, WorkReport } from '@/shared/api/workReports';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { SnackbarCtx } from '@/shared/ui/Snackbar';
import { ReportForm } from './ReportForm';

export const sites: EntrySites = {
  items: [
    { id: 'site-a', name: 'Стройка А', today: '2026-11-01', timeZoneId: 'Asia/Vladivostok',
      suggested: { date: '2026-10-31', shift: 'night', label: 'Ночная смена 31.10' } },
    { id: 'site-b', name: 'Стройка Б', today: '2026-10-31', timeZoneId: 'Europe/Moscow',
      suggested: { date: '2026-10-31', shift: 'day', label: 'Отчёт 31.10' } },
  ],
  boundary: { text: 'ваши стройки (2)', count: 2, reason: null },
};
export const report: WorkReport = {
  id: 'r1', constructionId: 'site-a', employeeId: 'e1', employeeName: 'Петров', constructionName: 'Стройка А',
  date: '2026-10-31', shift: 'day', status: 'draft', version: '1', periodClosed: false, hours: 8, note: null,
  today: '2026-11-01', timeZoneId: 'Asia/Vladivostok', returnComment: null, autoSubmitAt: null,
  enteredBy: null, marks: [], currentVersion: 1, versions: [], acceptWouldMark: false,
  lines: [{ id: 'l1', workTypeId: 'w1', variantId: null, unitId: 'u1', volume: 5, sectionId: null, location: null,
    comment: null, details: {}, isCrew: false, materials: [], participants: [], crewRole: null,
    photos: [{ id: 'p1', state: 'pending' }] }],
};
export const refusal = (status: number, data: object = {}) => ({ response: { status, data } });
const units = [{ id: 'u1', name: 'м', archived: false }, { id: 'u2', name: 'шт.', archived: false }];
export const types = [
  { id: 'w1', displayName: 'Прокладка кабеля', unitId: 'u1', unitName: 'м', archived: false,
    requiresLocation: true, requiresMaterials: true, variants: [{ id: 'v1', name: 'В лотке', archived: false }] },
  { id: 'w2', displayName: 'Монтаж коробки', unitId: 'u2', unitName: 'шт.', archived: false,
    requiresLocation: false, requiresMaterials: false, variants: [] },
];
export const toast = { success: vi.fn(), info: vi.fn(), error: vi.fn(), apiError: vi.fn() };
export const backend = { current: report, materialReason: null as string | null };

beforeEach(() => {
  vi.resetAllMocks();
  backend.current = report;
  backend.materialReason = null;
  vi.mocked(apiClient.get).mockImplementation(async path => {
    if (path === '/work/line-details-schema') return { data: { fields: [] } };
    if (path === '/work/photos/limits') return { data: { count: 5, megabytes: 15, maxBytes: 15 * 1024 * 1024,
      countText: 'Не больше 5 фото на строку.', sizeText: 'Фото больше 15 МБ.' } };
    if (path.endsWith('/preview')) return new Promise(() => {});
    if (path.endsWith('/catalog/work-types')) return { data: { items: types } };
    if (path.endsWith('/catalog/units')) return { data: { items: units } };
    if (path.endsWith('/catalog/sections')) return { data: { items: [{ id: 's1', name: 'Корпус А' }] } };
    if (path.endsWith('/catalog/materials')) return { data: { reason: backend.materialReason,
      items: backend.materialReason ? [] : [{ id: 'm1', displayName: 'Кабель ВВГ', archived: false }] } };
    if (path === '/work/materials/hint') return { data: {
      issued: { state: 'ok', items: [{ id: 'm1', displayName: 'Кабель ВВГ' }] },
      estimate: { state: 'ok', items: [] },
      catalog: backend.materialReason ? { state: 'not-connected', reason: backend.materialReason, items: [] } : { state: 'ok', items: [] },
    } };
    if (path.endsWith('/crew-candidates')) return { data: { items: [{ id: 'e2', fullName: 'Сергеев' }] } };
    return { data: path === '/work/sites/mine' || path.endsWith('/sites') ? sites : backend.current };
  });
});
afterEach(cleanup);

export function mount(id: string, mode: ReportMode = 'own', client = new QueryClient()) {
  const path = mode === 'own' ? '/my-reports' : '/work-review/entry';
  render(<SnackbarCtx.Provider value={toast}><QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[`${path}/${id}`]}>
      <NavigationGuardProvider><Routes>
        <Route path={path} element={<h2>Список отчётов</h2>} />
        <Route path={`${path}/:reportId`} element={<ReportForm mode={mode} />} />
      </Routes></NavigationGuardProvider>
    </MemoryRouter>
  </QueryClientProvider></SnackbarCtx.Provider>);
}

export const button = (name: string) => screen.getByRole('button', { name });

export function currentFromCreate(input: unknown) {
  const form = input as ReportInput & { id: string };
  const lines = form.lines.map(line => ({ ...line,
    photos: (line.photos ?? []).map(photo => ({ ...photo, state: 'pending' as const, canDeliver: true })),
  }));
  backend.current = { ...report, ...form, employeeId: report.employeeId, lines };
  return backend.current;
}
