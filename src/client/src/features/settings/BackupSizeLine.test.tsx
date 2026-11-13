// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { BackupSizeEstimate, BackupModuleWeight } from '@/shared/api/types';
import { BackupSizeLine } from './BackupSizeLine';

const fetchBackupSize = vi.fn<() => Promise<BackupSizeEstimate>>();
vi.mock('@/shared/api/backup', () => ({ fetchBackupSize: () => fetchBackupSize() }));

afterEach(cleanup);

const estimate = (modules: BackupModuleWeight[]): BackupSizeEstimate => ({
  scope: 'Full',
  limitBytes: 500 * 1024 * 1024,
  variant: { totalBytes: 4 * 1024 * 1024, manifestBytes: 1024, blobBytes: 3 * 1024 * 1024, blobCount: 20, missingBlobCount: 0, modules },
});

const renderLine = () => render(
  <QueryClientProvider client={new QueryClient()}>
    <BackupSizeLine scope="Full" />
  </QueryClientProvider>,
);

/** Строка веса копии называет долю файлов модулей (work-05, WORK-35). */
describe('BackupSizeLine', () => {
  it('пишет «из них файлы модулей», когда у модуля есть файлы', async () => {
    fetchBackupSize.mockResolvedValue(estimate([{ code: 'costs', title: 'Счета и накладные', bytes: 2 * 1024 * 1024, count: 7 }]));
    renderLine();
    expect(await screen.findByText('из них файлы модулей: 2 МБ (Счета и накладные: 2 МБ, файлов 7)')).toBeTruthy();
  });

  it('молчит о модулях, если файлов у них нет', async () => {
    fetchBackupSize.mockResolvedValue(estimate([{ code: 'costs', title: 'Счета и накладные', bytes: 0, count: 0 }]));
    renderLine();
    await screen.findByText(/Размер копии/);
    expect(screen.queryByText(/файлы модулей/)).toBeNull();
  });
});
