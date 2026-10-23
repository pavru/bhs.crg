// @vitest-environment jsdom
import { useState, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { SchemaField } from '@/shared/api/schema';
import type { DocumentType } from '@/shared/api/types';
import { EditGrid } from '@/shared/ui/EditGrid';
import { ExtraField } from '@/features/costs/InvoiceExtraFields';
import { ComplexFieldGroup } from './ComplexFields';
import { TableCell } from './ComplexCells';
import { PrimitiveInput } from './PrimitiveInput';
import { SuggestOwner } from './suggestContext';

/**
 * Подключение подсказок к полям записей (issue #1533): зовёт ли поле адрес и с каким типом-владельцем.
 * Ответ адреса подменён — проверяется, КОГО спрашивают, а поведение списка проверено у `SuggestField`.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

afterEach(() => { cleanup(); vi.resetAllMocks(); });

/** Адрес подсказок отвечает `items`, остальные чтения формы (реестры видов) — пустым списком. */
function answer(items: { value: string; here: boolean }[]) {
  vi.mocked(apiClient.get).mockImplementation(url =>
    Promise.resolve({ data: url === '/field-suggestions' ? { items } : [] }));
}
const asked = () => vi.mocked(apiClient.get).mock.calls.filter(([url]) => url === '/field-suggestions');
const pause = (ms: number) => act(() => new Promise(r => setTimeout(r, ms)));

function wrap(children: ReactNode) {
  return render(<QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>);
}

const field = (over: Partial<SchemaField> = {}): SchemaField =>
  ({ key: 'Производитель', title: 'Производитель', type: 'string', required: false, ...over }) as SchemaField;

function Input({ f }: { f: SchemaField }) {
  const [value, setValue] = useState<unknown>('');
  return <PrimitiveInput field={f} value={value} label={f.title} onChange={setValue} invalid={false} />;
}

/** Корень формы документа: тип документа и комплект. */
const Doc = ({ children }: { children: ReactNode }) =>
  <SuggestOwner type="doc-type" scope="Set" scopeId="set-1">{children}</SuggestOwner>;

describe('поле формы (PrimitiveInput)', () => {
  it('строка в форме записи зовёт адрес с типом записи и её уровнем', async () => {
    answer([{ value: 'Камкабель', here: true }]);
    wrap(<Doc><Input f={field()} /></Doc>);
    fireEvent.change(screen.getByRole('combobox', { name: 'Производитель' }), { target: { value: 'Кам' } });
    await waitFor(() => expect(asked()).toHaveLength(1));
    expect(asked()[0][1]?.params).toMatchObject({
      type: 'doc-type', field: 'Производитель', q: 'Кам', scope: 'Set', scopeId: 'set-1',
    });
  });

  it.each([
    ['многострочный текст', field({ type: 'text' })],
    ['пользовательский вид', field({ type: 'primitive' })],
    ['вычисляемое', field({ computed: true })],
    ['выключенное вручную', field({ tags: ['input.noSuggest'] })],
  ])('%s адрес не зовёт', async (_, f) => {
    answer([{ value: 'Камкабель', here: true }]);
    wrap(<Doc><Input f={f} /></Doc>);
    const box = screen.queryByRole('textbox', { name: 'Производитель' }) ?? screen.getByLabelText('Производитель');
    fireEvent.change(box, { target: { value: 'Кам' } });
    await pause(300);
    expect(asked()).toHaveLength(0);
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  it('без формы записи вокруг — обычное поле, адрес не зовётся', async () => {
    answer([{ value: 'Камкабель', here: true }]);
    wrap(<Input f={field()} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Производитель' }), { target: { value: 'Кам' } });
    await pause(300);
    expect(asked()).toHaveLength(0);
  });

  it('вложенный тип без корня формы молчит: уровня записи ему взять неоткуда', async () => {
    answer([{ value: 'Камкабель', here: true }]);
    wrap(<SuggestOwner type="row-type"><Input f={field()} /></SuggestOwner>);
    fireEvent.change(screen.getByRole('textbox', { name: 'Производитель' }), { target: { value: 'Кам' } });
    await pause(300);
    expect(asked()).toHaveLength(0);
  });

  it('дописанное поле счёта запросов не шлёт: формы записи ядра вокруг него нет (D9)', async () => {
    answer([{ value: 'Камкабель', here: true }]);
    wrap(<ExtraField field={field()} value={null} set={() => {}} locked={false} frame="" />);
    fireEvent.change(screen.getByLabelText('Производитель'), { target: { value: 'Кам' } });
    await pause(300);
    expect(asked()).toHaveLength(0);
  });
});

describe('поле составного', () => {
  const address: DocumentType = {
    id: 'address-type', name: 'Адрес', code: 'Адрес', kind: 'Composite', parentId: null,
    schema: { fields: [{ key: 'Регион', title: 'Регион', type: 'string' }] },
  } as unknown as DocumentType;

  it('зовёт адрес с типом составного, а не документа, и с уровнем документа', async () => {
    answer([{ value: 'Московская область', here: true }]);
    wrap(
      <Doc>
        <ComplexFieldGroup field={field({ key: 'Адрес', title: 'Адрес', type: 'complex', typeId: 'address-type' })}
          allDocTypes={[address]} value={{}} onChange={() => {}} showValidation={false} />
      </Doc>,
    );
    fireEvent.click(screen.getByRole('button', { name: /Заполнить вручную/ }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Регион' }), { target: { value: 'Мос' } });
    await waitFor(() => expect(asked()).toHaveLength(1));
    expect(asked()[0][1]?.params).toMatchObject({ type: 'address-type', field: 'Регион', scope: 'Set', scopeId: 'set-1' });
  });
});

describe('ячейка таблицы массива (TableCell)', () => {
  const rows = [{ key: 'a' }, { key: 'b' }];
  const Grid = ({ f }: { f: SchemaField }) => (
    <Doc>
      <SuggestOwner type="row-type">
        <EditGrid rows={rows} rowKey={r => r.key} columns={[{
          key: f.key, label: f.title,
          cell: () => <TableCell field={f} value="" onChange={() => {}} compositeType={null} allDocTypes={[]} />,
        }]} />
      </SuggestOwner>
    </Doc>
  );

  it('пустой ответ — списка нет, ↓ ведёт на строку ниже', async () => {
    answer([]);
    wrap(<Grid f={field({ key: 'Марка', title: 'Марка' })} />);
    const [first, second] = screen.getAllByRole('combobox', { name: 'Марка' }) as HTMLInputElement[];
    first.focus();
    fireEvent.change(first, { target: { value: 'ВВГ' } });
    await waitFor(() => expect(asked()).toHaveLength(1));
    expect(asked()[0][1]?.params).toMatchObject({ type: 'row-type', field: 'Марка', scope: 'Set' });
    await pause(50);
    expect(first.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('listbox', { hidden: true })).toBeNull();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(second);
  });

  it('текст в клетке однострочный, но не подсказывается; подпись клетки — имя поля', async () => {
    answer([{ value: 'абзац', here: true }]);
    wrap(<Grid f={field({ key: 'Примечание', title: 'Примечание', type: 'text' })} />);
    const [first] = screen.getAllByRole('textbox', { name: 'Примечание' });
    fireEvent.change(first, { target: { value: 'аб' } });
    await pause(300);
    expect(asked()).toHaveLength(0);
  });
});
