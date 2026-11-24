// @vitest-environment jsdom
import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { SnackbarCtx } from '@/shared/ui/Snackbar';
import { ReportCustomerFields } from './ReportCustomerFields';
import { withDetailValue } from './reportDetails';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

const scope = { siteId: 'site-a', date: '2026-11-16', mode: 'own', reportId: 'report-id' } as const;
const target = { id: 'target-type', parentId: null, schema: {}, name: 'Документ' };
const choice = {
  id: 'record-id', compositeTypeId: 'subtype', displayName: 'Документ стройки', scope: 'Construction', archived: false,
};

beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

function mount(fields: object[]) {
  const queryClient = new QueryClient();
  const changes = vi.fn();
  vi.mocked(apiClient.get).mockImplementation(async path => {
    if (path === '/work/line-details-schema') return { data: { fields, types: [target], enumTypes: [], primitiveTypes: [] } };
    if (path.endsWith('/catalog/field-references')) return { data: { items: [choice], types: [{ id: 'subtype', parentId: target.id }] } };
    return { data: { permissions: [], modules: [] } };
  });
  function Form() {
    const [details, setDetails] = useState<Record<string, unknown>>({});
    return <ReportCustomerFields details={details} disabled={false} scope={scope} onChange={(key, value) =>
      setDetails(current => {
        const next = withDetailValue(current, key, value);
        changes(next);
        return next;
      })} />;
  }
  const toast = { success: vi.fn(), info: vi.fn(), error: vi.fn(), apiError: vi.fn() };
  render(<SnackbarCtx.Provider value={toast}>
    <QueryClientProvider client={queryClient}><Form /></QueryClientProvider>
  </SnackbarCtx.Provider>);
  return changes;
}

it('выбирает одну ссылку и список ссылок через справочник Work', async () => {
  const paths = [
    { key: 'single', title: 'Один документ', type: 'doc-ref', typeId: target.id },
    { key: 'multiple', title: 'Документы', type: 'doc-array', typeId: target.id },
  ];
  const changes = mount(paths);
  const ref = { $ref: 'catalog', entryId: choice.id, displayName: choice.displayName, scope: choice.scope };
  fireEvent.click(await screen.findByRole('button', { name: /Выбрать: Документ/ }));
  fireEvent.click(await screen.findByRole('option', { name: choice.displayName }));
  const group = screen.getByRole('group', { name: 'Поля заказчика' });
  fireEvent.click(within(group).getByRole('button', { name: 'Добавить' }));
  fireEvent.click(await screen.findByRole('option', { name: choice.displayName }));
  expect(changes).toHaveBeenLastCalledWith({ single: ref, multiple: [ref] });
  expect(vi.mocked(apiClient.get).mock.calls.some(([path]) => path.startsWith('/common-data'))).toBe(false);
});

it('завершение загрузки сохраняет соседнюю правку сведений', async () => {
  const attachment = { blobPath: 'blob/act.pdf', fileName: 'act.pdf', size: 15, mimeType: 'application/pdf' };
  let uploadDone!: (response: { data: typeof attachment }) => void;
  vi.mocked(apiClient.post).mockImplementationOnce(() => new Promise(resolve => { uploadDone = resolve; }));
  const changes = mount([
    { key: 'attachment', title: 'Вложение', type: 'file' },
    { key: 'act', title: 'Номер акта', type: 'string' },
  ]);
  const input = await screen.findByRole('textbox', { name: 'Номер акта' });
  const fieldset = screen.getByRole('group', { name: 'Поля заказчика' });
  fireEvent.change(within(fieldset).getByLabelText(/Нажмите для выбора файла/), {
    target: { files: [new File(['Акт'], attachment.fileName, { type: attachment.mimeType })] },
  });
  fireEvent.change(input, { target: { value: 'А-17' } });
  await act(async () => uploadDone({ data: attachment }));
  await screen.findByText(/act\.pdf/);
  expect(changes).toHaveBeenLastCalledWith({ act: 'А-17', attachment: { ...attachment, $type: 'file' } });
});
