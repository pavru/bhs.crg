// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useParams } from 'react-router';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { TablePage } from './TablePage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
afterEach(cleanup);

const ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6';

function CardPath() {
  return <p>{useParams().reportId}</p>;
}

it('ссылка из строки таблицы отчётов открывает карточку с ключом этой строки', async () => {
  const declaration = {
    address: 'work.reports', title: 'Отчёты монтажников', grain: 'отчёт', recordType: 'ОтчётМонтажника',
    columns: [{ key: 'Сотрудник', label: 'Сотрудник', kind: 'text', system: true, operators: ['eq'] }], views: [],
  };
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url.endsWith('/columns')) return { data: declaration };
    if (url.endsWith('/views') || url.endsWith('/shortcuts')) return { data: [] };
    if (url === '/account/access') return { data: {
      permissions: ['work.report.review'], modules: [{ code: 'work', title: 'Учёт работ', available: true }],
    } };
    return { data: { ...declaration, rows: [{ Сотрудник: 'Монтажник А' }], count: 1, keys: [ID] } };
  });

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter initialEntries={['/tables/work.reports']}>
          <Routes>
            <Route path="/tables/:address" element={<TablePage />} />
            <Route path="/work-review/:reportId" element={<CardPath />} />
          </Routes>
        </MemoryRouter>
      </SnackbarProvider>
    </QueryClientProvider>,
  );

  fireEvent.click(await screen.findByRole('link', { name: 'Открыть отчёт: Монтажник А' }));
  expect(await screen.findByText(ID)).toBeTruthy();
});
