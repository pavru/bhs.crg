// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Assignment, AssignmentsPage } from '@/shared/api/workAssignments';
import type { AssignmentCandidatesPage } from '@/shared/api/work';
import { AssignmentsPanel } from './AssignmentsPanel';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * Панель «Назначения» и её окна (change `work-12-assignments-ui`, задачи 3.1–3.4): действия — только по ответу
 * сервера, отказы — словами сервера, окно с отказом не закрывается и набранного не теряет.
 */

afterEach(cleanup);

const row = (over: Partial<Assignment>): Assignment => ({
  id: 'a1', employeeId: 'e1', employeeName: 'Белов Андрей Сергеевич', personnelNumber: '0102', constructionId: 's1',
  kind: 'installer', dateFrom: '2026-11-01', dateTo: '2026-11-30', active: true, version: 'v1',
  canChange: true, changeReason: null, canRemove: true, removeReason: null, ...over,
});

const manager = row({
  id: 'a0', employeeId: 'e0', employeeName: 'Гришин Павел Николаевич', personnelNumber: '0117', kind: 'manager',
  dateFrom: '2026-09-01', dateTo: null, canRemove: false, removeReason: 'на стройке не останется менеджера',
});

const page = (over: Partial<AssignmentsPage['construction']> = {}, items: Assignment[] = [manager, row({})]): AssignmentsPage => ({
  construction: {
    id: 's1', name: 'ЖК «Северный», корпус 2', timeZoneId: 'Europe/Moscow', timeZoneInherited: true, today: '2026-11-20',
    canAssign: true, reason: null, ...over,
  },
  items,
});

const refusal = (status: number, data: unknown) => Object.assign(new Error(String(status)), { response: { status, data } });

let list: AssignmentsPage;
let candidates: AssignmentCandidatesPage;

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.put).mockReset();
  vi.mocked(apiClient.post).mockReset();
  vi.mocked(apiClient.delete).mockReset();
  try { localStorage.clear(); } catch { /* нет хранилища — вид по умолчанию */ }
  list = page();
  candidates = { items: [], inArchive: 0, limit: 200, more: false };
  vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
    if (url === '/work/assignments') return { data: list };
    if (url === '/work/assignments/candidates') return { data: candidates };
    throw new Error(`неожиданный адрес ${url}`);
  }) as never);
});

function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter><AssignmentsPanel constructionId="s1" /></MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('панель «Назначения»', () => {
  it('менеджер своей стройки видит назначения и «Назначить на период»; порядок — сервера', async () => {
    mount();
    await screen.findByText('Гришин Павел Николаевич');
    expect(screen.getByRole('button', { name: /Назначить на период/ })).toBeTruthy();
    const rows = screen.getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('Гришин Павел Николаевич')).toBeTruthy();
    expect(within(rows[0]).getByText('с 01.09.2026, без срока')).toBeTruthy();
  });

  it('без разрешения правки кнопок нет в разметке, причина — строкой над списком', async () => {
    list = page({ canAssign: false, reason: 'Вы не менеджер этой стройки: назначать на ней может её менеджер.' });
    mount();
    await screen.findByText('Гришин Павел Николаевич');
    expect(screen.queryByRole('button', { name: /Назначить на период/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Действия назначения/ })).toBeNull();
    expect(screen.getByTestId('assignments-reason').textContent).toMatch(/назначать на ней может её менеджер/);
  });

  it('«Снять» у последнего менеджера скрыт: в его меню только «Изменить период»', async () => {
    mount();
    fireEvent.pointerDown(await screen.findByRole('button', { name: 'Действия назначения: Гришин Павел Николаевич' }), { button: 0, ctrlKey: false });
    expect(await screen.findByRole('menuitem', { name: /Изменить период/ })).toBeTruthy();
    expect(screen.queryByRole('menuitem', { name: /Снять назначение/ })).toBeNull();
  });

  it('назначений нет — «на стройку никто не назначен» и предложение назначить', async () => {
    list = page({}, []);
    mount();
    expect(await screen.findByText('На стройку никто не назначен')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Назначить на период/ })).toHaveLength(1);
  });

  it('отбор «действующие и будущие» скрывает закончившиеся и говорит, сколько', async () => {
    list = page({}, [manager, row({ id: 'old', employeeName: 'Ушедший', active: false, dateTo: '2026-10-31', dateFrom: '2026-10-01' })]);
    mount();
    await screen.findByText('Гришин Павел Николаевич');
    expect(screen.queryByText('Ушедший')).toBeNull();
    expect(screen.getByText(/Ещё 1 закончившихся/)).toBeTruthy();
  });

  it('лента — те же строки, полоса открывает «Изменить период»', async () => {
    mount();
    await screen.findByText('Гришин Павел Николаевич');
    fireEvent.click(screen.getByRole('tab', { name: 'Лента' }));
    fireEvent.click(screen.getByRole('button', { name: /Белов Андрей Сергеевич, монтажник, 01\.11\.2026 — 30\.11\.2026 — изменить период/ }));
    expect(await screen.findByRole('dialog', { name: 'Изменить период' })).toBeTruthy();
  });
});

describe('окно «Изменить период»', () => {
  async function openEdit() {
    mount();
    fireEvent.click(await screen.findByRole('tab', { name: 'Лента' }));
    fireEvent.click(await screen.findByRole('button', { name: /Белов Андрей Сергеевич.*изменить период/ }));
    return screen.findByRole('dialog', { name: 'Изменить период' });
  }

  it('правка уходит с версией из ответа чтения', async () => {
    vi.mocked(apiClient.put).mockResolvedValue({ data: row({}) } as never);
    const dialog = await openEdit();
    fireEvent.click(within(dialog).getByRole('button', { name: /Сохранить/ }));
    await waitFor(() => expect(apiClient.put).toHaveBeenCalled());
    const [url, body, config] = vi.mocked(apiClient.put).mock.calls[0];
    expect(url).toBe('/work/assignments/a1');
    expect(body).toEqual({ dateFrom: '2026-11-01', dateTo: '2026-11-30' });
    expect(config).toEqual({ headers: { 'If-Match': 'v1' } });
  });

  it('409 — окно открыто, текст сервера и «Перечитать»; следующая правка — со свежей версией', async () => {
    vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, {
      error: 'Назначение тем временем изменили, и это действие не выполнено. Перечитайте назначения стройки и повторите.',
    }));
    const dialog = await openEdit();
    fireEvent.click(within(dialog).getByRole('button', { name: /Сохранить/ }));
    expect(await within(dialog).findByText(/тем временем изменили/)).toBeTruthy();

    list = page({}, [manager, row({ version: 'v2' })]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Перечитать' }));
    expect(await within(dialog).findByText(/Назначения перечитаны/)).toBeTruthy();

    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: row({ version: 'v3' }) } as never);
    fireEvent.click(within(dialog).getByRole('button', { name: /Сохранить/ }));
    await waitFor(() => expect(apiClient.put).toHaveBeenCalledTimes(2));
    expect(vi.mocked(apiClient.put).mock.calls[1][2]).toEqual({ headers: { 'If-Match': 'v2' } });
  });

  it('пересечение — словами сервера, окно не закрывается', async () => {
    vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Период пересекается с назначением 01.12.2026–31.12.2026.' }));
    const dialog = await openEdit();
    fireEvent.click(within(dialog).getByRole('button', { name: /Сохранить/ }));
    expect(await within(dialog).findByText('Период пересекается с назначением 01.12.2026–31.12.2026.')).toBeTruthy();
    expect(screen.getByRole('dialog', { name: 'Изменить период' })).toBeTruthy();
  });
});

describe('окно «Назначить на период»', () => {
  it('422 с одним отказавшим — «ни одно не заведено», причина у названного, отметки целы', async () => {
    candidates = {
      items: ['Белов', 'Волков', 'Лаптев', 'Мельник'].map(n => ({
        employeeId: n, name: n, personnelNumber: null, eligible: true, reason: null, assigned: null, account: { state: 'linked' as const },
      })),
      inArchive: 0, limit: 200, more: false,
    };
    vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(422, {
      error: 'Назначение не заведено ни одному сотруднику: Мельник — уволен с 20.12.2026. Уберите их из пачки или измените период.',
      refused: [{ employeeId: 'Мельник', name: 'Мельник', reason: 'уволен с 20.12.2026' }],
    }));
    list = page({}, [manager]);
    mount();
    fireEvent.click(await screen.findByRole('button', { name: /Назначить на период/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Назначить на период' });
    // Монтажнику нужен конец периода — до него кандидатов не спрашиваем.
    expect(within(dialog).getByText(/Задайте период/)).toBeTruthy();
    expect(vi.mocked(apiClient.get).mock.calls.some(([u]) => u === '/work/assignments/candidates')).toBe(false);

    // Менеджер — без срока: начало (сегодня стройки) уже задано, кандидаты приходят.
    fireEvent.keyDown(within(dialog).getByRole('combobox', { name: 'Вид' }), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Менеджер' }));
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Отметить всех годных' }));
    fireEvent.click(within(dialog).getByRole('button', { name: /Назначить · 4/ }));

    expect(await within(dialog).findByText(/Назначение не заведено ни одному сотруднику/)).toBeTruthy();
    expect(within(dialog).getByText('уволен с 20.12.2026').className).toContain('text-error');
    for (const n of ['Белов', 'Волков', 'Лаптев', 'Мельник'])
      expect((within(dialog).getByRole('checkbox', { name: `Отметить: ${n}` }) as HTMLInputElement).checked).toBe(true);
    expect(vi.mocked(apiClient.post).mock.calls[0]).toEqual(['/work/assignments/bulk', {
      constructionId: 's1', kind: 'manager', dateFrom: '2026-11-20', dateTo: null, employeeIds: ['Белов', 'Волков', 'Лаптев', 'Мельник'],
    }]);
  });

  it('смена периода перечитывает кандидатов; ставший негодным снимается, его строка — с причиной', async () => {
    const fit = (n: string, eligible = true, reason: string | null = null) => ({
      employeeId: n, name: n, personnelNumber: null, eligible, reason, assigned: null, account: { state: 'linked' as const },
    });
    candidates = { items: [fit('Белов'), fit('Мельник')], inArchive: 0, limit: 200, more: false };
    list = page({}, [manager]);
    mount();
    fireEvent.click(await screen.findByRole('button', { name: /Назначить на период/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Назначить на период' });
    fireEvent.keyDown(within(dialog).getByRole('combobox', { name: 'Вид' }), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Менеджер' }));
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Отметить всех годных' }));
    expect(within(dialog).getByRole('button', { name: /Назначить · 2/ })).toBeTruthy();

    candidates = { items: [fit('Белов'), fit('Мельник', false, 'уволен с 20.12.2026')], inArchive: 0, limit: 200, more: false };
    fireEvent.keyDown(within(dialog).getByRole('combobox', { name: 'Вид' }), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Монтажник' }));
    fireEvent.keyDown(within(dialog).getByRole('combobox', { name: 'Вид' }), { key: 'Enter' });
    fireEvent.click(screen.getByRole('option', { name: 'Менеджер' }));

    expect(await within(dialog).findByText('уволен с 20.12.2026')).toBeTruthy();
    await waitFor(() => expect(within(dialog).getByRole('button', { name: /Назначить · 1/ })).toBeTruthy());
    expect((within(dialog).getByRole('checkbox', { name: 'Отметить: Мельник' }) as HTMLInputElement).disabled).toBe(true);
  });
});

describe('снятие назначения', () => {
  it('409 «не останется менеджера» — причина в окне подтверждения, назначение остаётся', async () => {
    vi.mocked(apiClient.delete).mockRejectedValueOnce(refusal(409, { error: 'На стройке не останется менеджера.' }));
    mount();
    fireEvent.pointerDown(await screen.findByRole('button', { name: 'Действия назначения: Белов Андрей Сергеевич' }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole('menuitem', { name: /Снять назначение/ }));
    const dialog = await screen.findByRole('dialog', { name: /Снять назначение\?|Назначение не снято/ });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Снять назначение' }));
    expect(await within(dialog).findByText('На стройке не останется менеджера.')).toBeTruthy();
    expect(vi.mocked(apiClient.delete).mock.calls[0]).toEqual(['/work/assignments/a1', { headers: { 'If-Match': 'v1' } }]);
    expect(screen.getAllByText('Белов Андрей Сергеевич').length).toBeGreaterThan(0);
  });
});
