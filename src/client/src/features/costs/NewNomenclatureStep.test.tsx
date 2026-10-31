// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { IntakeKind } from '@/shared/api/nomenclatureIntake';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { NewNomenclatureStep } from './NewNomenclatureStep';

/**
 * Подсказка в шаге «Новая позиция» (issue #1533, ТЗ CORE-8, CORE-9): подсказывается ли поле, шаг знает
 * только из описания двери — кода `suggest` у поля; выбранное идёт тем же путём, что набор.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

afterEach(() => { cleanup(); vi.resetAllMocks(); });

const KIND: IntakeKind = {
  typeId: 'kind-1', code: 'Кабель', name: 'Кабель', refusals: [],
  fields: [
    { key: 'Наименование', title: 'Наименование', required: true, identity: true, options: null, suggest: 'suggested' },
    { key: 'Производитель', title: 'Производитель', required: false, identity: true, options: null, suggest: 'suggested' },
    { key: 'Артикул', title: 'Артикул', required: false, identity: true, options: null, tag: 'nomenclature.article', suggest: 'article' },
  ],
};

function serve() {
  vi.mocked(apiClient.get).mockImplementation(url => Promise.resolve({
    data: url === '/nomenclature/intake' ? { kinds: [KIND] }
      : url === '/field-suggestions' ? { items: [{ value: 'Камкабель', here: true }] } : [],
  }));
  vi.mocked(apiClient.post).mockResolvedValue({ data: { exact: null, similar: [], more: false, unreadable: 0 } });
}
const suggestCalls = () => vi.mocked(apiClient.get).mock.calls.filter(([url]) => url === '/field-suggestions');
const similarCalls = () => vi.mocked(apiClient.post).mock.calls.filter(([url]) => url === '/nomenclature/similar');
const pause = (ms: number) => act(() => new Promise(r => setTimeout(r, ms)));

function step() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <SnackbarProvider>
        <NewNomenclatureStep from={{ name: 'ВВГнг 3х2,5' }} plate={null} narrow onBack={() => {}} onPick={() => {}} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('шаг «Новая позиция»: подсказка по введённому ранее', () => {
  it('производитель — из справочника уровня «вся система»; подстановка запускает поиск похожих', async () => {
    serve();
    step();
    const maker = await screen.findByRole('combobox', { name: 'Производитель' }) as HTMLInputElement;
    maker.focus();
    fireEvent.change(maker, { target: { value: 'Кам' } });
    await waitFor(() => expect(suggestCalls()).toHaveLength(1));
    expect(suggestCalls()[0][1]?.params).toMatchObject({ type: 'kind-1', field: 'Производитель', q: 'Кам', scope: 'System' });

    await waitFor(() => expect(maker.getAttribute('aria-expanded')).toBe('true'));
    fireEvent.keyDown(maker, { key: 'ArrowDown' });
    fireEvent.keyDown(maker, { key: 'Enter' });
    expect(maker.value).toBe('Камкабель');

    // Похожие спрашиваются о подставленном, как о набранном: производитель — поле ключа.
    await waitFor(() => expect(similarCalls().some(([, body]) =>
      (body as { values: Record<string, string> }).values['Производитель'] === 'Камкабель')).toBe(true));
  });

  it('однозначное поле (артикул) адрес не зовёт', async () => {
    serve();
    step();
    const article = await screen.findByRole('textbox', { name: 'Артикул' });
    fireEvent.change(article, { target: { value: 'S201' } });
    await pause(300);
    expect(suggestCalls()).toHaveLength(0);
  });
});
