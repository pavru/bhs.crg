import { QueryClient } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { sendScan } from './invoiceRecognition';

vi.mock('./client', () => ({ apiClient: { post: vi.fn() } }));

/**
 * Сознательный повтор (COST-6.7): «Завести ещё один черновик» уходит полем формы `acceptRepeat`, и
 * только при «да» — иначе сервер помечал бы повтор, которого человек не принимал.
 */

const created = { data: { invoice: { id: 'новый', sameFile: [] }, recognition: { state: 'failed' } } };

const sentForm = (): FormData => vi.mocked(apiClient.post).mock.calls[0][1] as FormData;

beforeEach(() => {
  vi.mocked(apiClient.post).mockReset();
  vi.mocked(apiClient.post).mockResolvedValueOnce(created);
});

describe('сознательный повтор при заведении из файла', () => {
  it('уходит полем формы, когда повтор принят', async () => {
    await sendScan(new QueryClient(), new File(['x'], 'Счёт.pdf'), { acceptRepeat: true });

    expect(sentForm().get('acceptRepeat')).toBe('true');
    expect(sentForm().get('file')).toBeInstanceOf(File);
  });

  it.each([
    ['без вопроса', undefined],
    ['при «нет»', { acceptRepeat: false }],
  ])('не уходит вовсе — %s', async (_, options) => {
    await sendScan(new QueryClient(), new File(['x'], 'Счёт.pdf'), options);

    expect(sentForm().has('acceptRepeat')).toBe(false);
  });
});
