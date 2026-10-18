// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SchemaField } from '@/shared/api/schema';
import { ExtraField } from './InvoiceExtraFields';

/** Поле, дописанное в тип «Счёт» (issue #1482), — то, что поправило ревью PR #1506. */

afterEach(cleanup);

const field = (over: Partial<SchemaField>): SchemaField =>
  ({ key: 'Аванс', title: 'Аванс', type: 'number', required: false, ...over }) as SchemaField;

describe('дописанное число', () => {
  it('принимает запятую и пробелы, как суммы шапки, и уезжает числом', () => {
    const set = vi.fn();
    render(<ExtraField field={field({})} value={null} set={set} locked={false} frame="" />);
    fireEvent.change(screen.getByLabelText('Аванс'), { target: { value: '1 234,50' } });
    expect(set).toHaveBeenLastCalledWith('Аванс', 1234.5);
  });

  it('не число — названо у поля и уезжает как набрано: отказ сервера назовёт поле', () => {
    const set = vi.fn();
    render(<ExtraField field={field({})} value={null} set={set} locked={false} frame="" />);
    fireEvent.change(screen.getByLabelText('Аванс'), { target: { value: 'двести' } });
    expect(set).toHaveBeenLastCalledWith('Аванс', 'двести');
    expect(screen.getByText('Не число')).toBeTruthy();
  });

  it('стёртое — `null`, а не пустая строка', () => {
    const set = vi.fn();
    render(<ExtraField field={field({})} value={12} set={set} locked={false} frame="" />);
    expect((screen.getByLabelText('Аванс') as HTMLInputElement).value).toBe('12');
    fireEvent.change(screen.getByLabelText('Аванс'), { target: { value: '' } });
    expect(set).toHaveBeenLastCalledWith('Аванс', null);
  });
});

describe('поле со своим типом', () => {
  it('пока определение не пришло — значение только читается и сказано почему, а не «нет вариантов»', () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <ExtraField field={field({ key: 'Статус', title: 'Статус', type: 'enum', typeId: 'e1' })}
          value="active" set={() => {}} locked={false} frame="" />
      </QueryClientProvider>,
    );
    expect(screen.queryByText(/Нет вариантов/)).toBeNull();
    expect(screen.getByText('Тип поля загружается…')).toBeTruthy();
    expect((screen.getByLabelText('Статус') as HTMLInputElement).readOnly).toBe(true);
  });
});
