// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { useNavigationGuard } from '@/shared/ui/NavigationGuard';
import { apiClient } from '@/shared/api/client';
import type { WaybillView } from '@/shared/api/waybills';
import { WaybillForm } from './WaybillForm';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(() => new Promise(() => {})), put: vi.fn(), delete: vi.fn() },
  traceIdOf: () => undefined,
}));
vi.mock('@/shared/api/access', async original => ({
  ...(await original<typeof import('@/shared/api/access')>()),
  useAccess: () => ({ data: {} }), hasPermission: () => true,
  useCan: () => ({ access: {}, module: () => true, permission: () => true }),
}));

/**
 * Вопрос ухода — на настоящей форме накладной (ревью PR #1821): как она подключает `LeaveGuard`. Уходит
 * «страница» так же, как выбор другой накладной в `WaybillsPage`, — через охрану ухода.
 */

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: true, media: query, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);
beforeEach(() => { vi.mocked(apiClient.put).mockReset(); vi.mocked(apiClient.post).mockReset(); });

const draft: WaybillView = {
  id: 'w1', version: 'v1', number: 'РН-1', issuedOn: '2026-09-01', warehouse: null, constructionId: null,
  constructionName: null, constructionLost: false, receivedBy: null, note: null, state: 'Draft', postedAt: null,
  lines: [], totals: { count: 0, unmatched: 0 },
};
const locked: WaybillView = {
  ...draft, state: 'Posted', postedAt: '2026-09-01T10:00:00Z', accountingOn: '2026-09-01', locked: '2026-09-30',
  currentVersion: 1,
};

/** Выбор другой накладной: уходит через охрану, как `WaybillsPage`. */
function OtherWaybill({ onGo }: { onGo: () => void }) {
  const guard = useNavigationGuard();
  return <button type="button" onClick={() => { if (!guard?.attempt(onGo)) onGo(); }}>Другая накладная</button>;
}

function mount(view: WaybillView) {
  const go = vi.fn();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const tree = (gone: boolean) => (
    <MemoryRouter><QueryClientProvider client={qc}><SnackbarProvider><NavigationGuardProvider>
      <WaybillForm view={view} sites={[]} sitesFailed={false} canEdit gone={gone} onClose={() => {}} />
      <OtherWaybill onGo={go} />
    </NavigationGuardProvider></SnackbarProvider></QueryClientProvider></MemoryRouter>
  );
  const { rerender } = render(tree(false));
  return {
    go, gone: () => rerender(tree(true)),
    leave: () => fireEvent.click(screen.getByRole('button', { name: 'Другая накладная' })),
  };
}

/** Ответ запроса, который приходит по команде теста. */
function later(method: 'put' | 'post') {
  let settle!: { resolve: (v: { data: WaybillView }) => void; reject: (e: unknown) => void };
  vi.mocked(apiClient[method]).mockImplementationOnce(
    () => new Promise((resolve, reject) => { settle = { resolve, reject }; }) as never);
  return {
    answer: (value: WaybillView) => act(async () => { settle.resolve({ data: value }); }),
    refuse: () => act(async () => { settle.reject({ response: { status: 409, data: { error: 'Изменили (проба).' } } }); }),
  };
}

const question = () => screen.queryByRole('dialog', { name: 'Несохранённые изменения' });
const saveAndGo = () => screen.queryByRole('button', { name: 'Сохранить и перейти' });
const note = () => screen.getByLabelText('Примечание') as HTMLInputElement;

describe('вопрос ухода из накладной', () => {
  it('правка черновика: вопрос называет её и стоит, пока идёт запись; удачная — уход', async () => {
    const { go, leave } = mount(draft);
    fireEvent.change(note(), { target: { value: 'с бумаги' } });
    leave();
    expect(screen.getByText('Не сохранены изменения накладной. Сохранить их перед переходом?')).toBeTruthy();

    const write = later('put');
    fireEvent.click(saveAndGo()!);
    await vi.waitFor(() => expect(apiClient.put).toHaveBeenCalledOnce());
    expect(question()).toBeTruthy();
    expect(saveAndGo()!.hasAttribute('disabled')).toBe(true);
    expect(go).not.toHaveBeenCalled();

    await write.answer({ ...draft, version: 'v2', note: 'с бумаги' });
    expect(go).toHaveBeenCalledOnce();
    expect(question()).toBeNull();
  });

  it('отказ записи уход отменяет: набранное на экране', async () => {
    const { go, leave } = mount(draft);
    fireEvent.change(note(), { target: { value: 'с бумаги' } });
    leave();
    const write = later('put');
    fireEvent.click(saveAndGo()!);
    await vi.waitFor(() => expect(apiClient.put).toHaveBeenCalledOnce());
    await write.refuse();
    expect(question()).toBeNull();
    expect(go).not.toHaveBeenCalled();
    expect(note().value).toBe('с бумаги');
  });

  it('уход во время «Провести» ждёт и проведения, а не только записи черновика', async () => {
    const { go, leave } = mount(draft);
    fireEvent.change(note(), { target: { value: 'с бумаги' } });
    const write = later('put');
    const posting = later('post');
    fireEvent.click(screen.getByRole('button', { name: 'Провести' }));
    await vi.waitFor(() => expect(apiClient.put).toHaveBeenCalledOnce());
    leave();
    expect(screen.getByText(/^Накладная записывается\./)).toBeTruthy();

    await write.answer({ ...draft, version: 'v2', note: 'с бумаги' });
    await vi.waitFor(() => expect(apiClient.post).toHaveBeenCalledOnce());
    expect(go).not.toHaveBeenCalled();
    expect(question()).toBeTruthy();

    await posting.answer({ ...draft, version: 'v3', note: 'с бумаги', state: 'Posted' });
    await vi.waitFor(() => expect(go).toHaveBeenCalledOnce());
  });

  it('исправление: кнопки записи нет, причина названа текстом', () => {
    const { go, leave } = mount(locked);
    fireEvent.click(screen.getByRole('button', { name: 'Исправить' }));
    fireEvent.change(screen.getByLabelText(/Причина исправления/), { target: { value: 'опечатка' } });
    leave();
    expect(question()).toBeTruthy();
    expect(saveAndGo()).toBeNull();
    expect(screen.getByText(/«Записать исправление»/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Не сохранять' }));
    expect(go).toHaveBeenCalledOnce();
    expect(apiClient.put).not.toHaveBeenCalled();
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('накладную удалили посреди исправления — уход без вопроса: записывать некуда', () => {
    const { go, leave, gone } = mount(locked);
    fireEvent.click(screen.getByRole('button', { name: 'Исправить' }));
    fireEvent.change(screen.getByLabelText(/Причина исправления/), { target: { value: 'опечатка' } });
    gone();
    leave();
    expect(question()).toBeNull();
    expect(go).toHaveBeenCalledOnce();
  });
});
