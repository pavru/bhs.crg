// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { SchemaField } from '@/shared/api/schema';
import type { DocumentInstance } from '@/shared/api/types';
import { RequisitesTab } from './RequisitesTab';

/**
 * Хвост поля реквизита из источника и под замком (D7 sharedui-md3-19, [UI-86]) — на самой вкладке, а не на
 * её кусках: что кнопка привязки ушла из угла ячейки в хвост, решает условие формы, и проверить его можно
 * только целой вкладкой (ревью PR #1775). Адреса подменены: привязка документа отвечает одним источником,
 * распознанным со скана, остальные чтения — пусто.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

const fields: SchemaField[] = [
  { key: 'Дата', title: 'Дата начала работ', type: 'date', required: true, locked: true },
  { key: 'Количество', title: 'Количество экземпляров', type: 'number', required: true, locked: true },
  { key: 'Примечание', title: 'Примечание', type: 'string', required: true },
  { key: 'Номер', title: 'Номер', type: 'string', required: false },
] as SchemaField[];

const binding = { id: 'b1', ownerId: 'doc', sourceId: 's1', targetFieldKey: null,
  mapping: { Количество: 'Количество', Примечание: 'Наименование' },
  source: { id: 's1', name: 'Шапка счёта', origin: 'Recognized', recognitionStale: false, staleReason: null } };

function renderTab() {
  vi.mocked(apiClient.get).mockImplementation(url =>
    Promise.resolve({ data: url === '/datasets/bindings' ? [binding] : [] }));
  vi.mocked(apiClient.post).mockImplementation(() => Promise.resolve({ data: [] }));
  const instance = { id: 'doc', documentSetId: 'set', documentTypeId: 'type', name: 'АОСР',
    requisites: { Дата: '2026-05-12' }, updatedAt: '' } as unknown as DocumentInstance;
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <RequisitesTab instance={instance} setId="set" schemaFields={fields} allDocTypes={[]} docType={undefined}
          otherInstances={[]} onClose={() => {}} onDirty={() => {}} saveRef={{ current: null } as never}
          onBaseState={() => {}} baseControlRef={{ current: null }} />
      </MemoryRouter>
    </QueryClientProvider>);
}

/** Ячейка поля по его подписи в вырезе. */
const cellOf = (name: string) => screen.getByLabelText(name).closest('.col-span-1') as HTMLElement;

describe('RequisitesTab: хвост поля из источника и под замком', () => {
  it('у привязанного — одна кнопка в хвосте, у запертого с привязкой — замок и кнопка, у запертого — замок', async () => {
    renderTab();
    fireEvent.click(await screen.findByRole('button', { name: /Заполняются автоматически/ }));
    await screen.findByRole('button', { name: 'Снять привязку к источнику' });

    const bound = cellOf('Примечание');
    expect(within(bound).getAllByRole('button').map(b => b.getAttribute('aria-label'))).toEqual(['Привязка к источнику']);
    const both = cellOf('Количество экземпляров');
    expect(within(both).getAllByRole('button').map(b => b.getAttribute('aria-label'))).toEqual(['Снять привязку к источнику']);
    const lockOnly = cellOf('Дата начала работ');
    expect(within(lockOnly).queryAllByRole('button')).toEqual([]);

    // Кнопка — в слоте хвоста поля, а не в углу ячейки: в углу она накрыла бы хвост.
    for (const cell of [bound, both]) expect(within(cell).getByRole('button').closest('.absolute.top-0\\.5')).toBeNull();
    // Замок — знак перед кнопкой: место хвоста шире.
    expect(screen.getByLabelText('Количество экземпляров').className).toContain('pr-[calc(4.125rem+var(--field-mark,0px))]');
    // Происхождение диктор слышит вместе с полем, а пояснение замка — подсказкой поля, без второго чтения у значка.
    expect(screen.getByLabelText('Примечание').getAttribute('aria-describedby')).toContain('doc-Примечание-origin');
    expect(document.getElementById('doc-Примечание-origin')!.textContent).toContain('распознано со скана');
    expect(within(lockOnly).getByText('Заполняет модуль — вручную не правится')).toBeDefined();
    expect(within(lockOnly).queryByRole('img', { name: 'Заполняет модуль — вручную не правится' })).toBeNull();
  });

  it('у редактируемого поля кнопка привязки осталась в углу ячейки', async () => {
    renderTab();
    const own = await screen.findByLabelText('Номер');
    const lens = within(own.closest('.col-span-1') as HTMLElement).getByRole('button', { name: 'Привязать к источнику' });
    expect(lens.closest('.absolute.top-0\\.5')).not.toBeNull();
  });
});
