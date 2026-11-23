// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { WorkLinePhoto } from '@/shared/api/work';
import { LinePhotos, type ReportPhotos } from './LinePhotos';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
afterEach(cleanup);

it('держит открытый снимок, пока более ранний файл догружается', async () => {
  vi.mocked(apiClient.get).mockImplementation(() => new Promise(() => {}));
  const client = new QueryClient();
  const controls: ReportPhotos = {
    locked: true, closed: false, busy: false, remove: vi.fn(),
    uploads: { transfers: {}, queue: vi.fn(), forget: vi.fn(), deliver: vi.fn() },
  };
  const photos: WorkLinePhoto[] = [
    { id: 'first', state: 'pending', originalName: 'Первое.jpg' },
    { id: 'second', state: 'stored', originalName: 'Второе.jpg' },
    { id: 'third', state: 'stored', originalName: 'Третье.jpg' },
  ];
  const view = (items: WorkLinePhoto[]) => <QueryClientProvider client={client}>
    <LinePhotos lineId="line" photos={items} declared={items} controls={controls} add={vi.fn()} />
  </QueryClientProvider>;
  const { rerender } = render(view(photos));
  fireEvent.click(screen.getByRole('button', { name: 'Открыть фото: Второе.jpg' }));
  const dialog = screen.getByRole('dialog', { name: 'Фото строки' });
  expect(within(dialog).getByText('1 из 2')).toBeTruthy();
  await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/work/photos/second', expect.anything()));
  rerender(view([{ ...photos[0], state: 'stored' }, ...photos.slice(1)]));
  expect(within(dialog).getByText('1 из 2')).toBeTruthy();
  expect(apiClient.get).not.toHaveBeenCalledWith('/work/photos/first', expect.anything());
  fireEvent.click(within(dialog).getByRole('button', { name: 'Следующее фото' }));
  await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/work/photos/third', expect.anything()));
});
