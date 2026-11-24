// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import type { SchemaField } from '@/shared/api/schema';
import { GroupedFieldsEditor } from './GroupedFieldsEditor';

vi.mock('./FieldBuilder', () => ({
  FieldCard: ({ acceptanceFlag }: { acceptanceFlag?: ReactNode }) => <div>{acceptanceFlag}</div>,
}));

afterEach(cleanup);

const fields: SchemaField[] = [
  { key: 'act', title: 'Акт', type: 'string', required: false },
  { key: 'module', title: 'Поле модуля', type: 'string', required: false, origin: 'module' },
  { key: 'formula', title: 'Формула', type: 'number', required: false, computed: true },
];

it('показывает колонку только при поддержке приёмки и разрешает менять признак своего поля', () => {
  const onFieldsChange = vi.fn();
  const props = {
    fields, onFieldsChange, groups: [], onGroupsChange: vi.fn(), parentEffectiveFields: [],
    reg: { compositeTypes: [], enumTypes: [], primitiveTypes: [], allDocTypes: [], tagRegistry: undefined },
  };
  const view = render(<GroupedFieldsEditor {...props} />);
  expect(screen.queryByText('Нужно к приёмке')).toBeNull();
  view.rerender(<GroupedFieldsEditor {...props} acceptanceFields />);
  expect(screen.getByText('Нужно к приёмке')).toBeTruthy();
  expect((screen.getByRole('checkbox', { name: 'Нужно к приёмке · Поле модуля' }) as HTMLInputElement).disabled).toBe(true);
  expect((screen.getByRole('checkbox', { name: 'Нужно к приёмке · Формула' }) as HTMLInputElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('checkbox', { name: 'Нужно к приёмке · Акт' }));
  expect(onFieldsChange).toHaveBeenCalledWith([{ ...fields[0], forAcceptance: true }, ...fields.slice(1)]);
});
