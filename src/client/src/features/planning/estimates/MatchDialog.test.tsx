// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { MatchDialog, type MatchTarget } from './MatchDialog';
import { REVISION, ROWS, SECTIONS, WORK_TYPES } from './matchFixtures';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), apiError: vi.fn() }));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => toast }));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));

/**
 * Окно сопоставления строк (plan-09, задача 4.1, ТЗ PLAN-6.1, CORE-8): раздел целиком одной единицей, коэффициент с
 * обеими единицами, «запомнено» с отменой, «новый вид работы» только при праве, отказ 409 — без потери набранного.
 */

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});

beforeEach(() => {
  permissions = ['plan.estimate.edit'];
  vi.mocked(apiClient.get).mockImplementation(async (url: string, config?: { params?: Record<string, unknown> }) => {
    if (url === '/work-types') return { data: WORK_TYPES } as never;
    if (url === '/plan/unit-factor') {
      const unitId = config?.params?.unitId;
      return { data: { factor: 100, sourceUnit: config?.params?.sourceUnit, unitId, unit: unitId === 'u-sht' ? 'шт' : 'м', reason: null } } as never;
    }
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount(target: MatchTarget, onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MatchDialog revision={REVISION} rows={ROWS} sections={SECTIONS} target={target} onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

/** Выбрать вид работы и дождаться коэффициента сервера: до него сопоставлять нечем. */
async function pickWorkType(name: string) {
  const select = await screen.findByRole('combobox', { name: 'Вид работы' });
  await waitFor(() => expect(select.hasAttribute('disabled')).toBe(false));
  fireEvent.keyDown(select, { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name }));
  await screen.findByText(/коэффициент предложен единицами/);
}

describe('сопоставление раздела ЛСР', () => {
  it('раздел целиком: строки выбранной единицы, коэффициент сервера с примером; другие единицы ждут', async () => {
    const onClose = mount({ kind: 'section', code: '9' });
    await pickWorkType('02 Монтаж электроустановочных изделий');

    expect(await screen.findByText(/например, 9\.4: 2,16 × 100 =/)).toBeTruthy();
    expect(screen.getByText('другая единица — останется ждать')).toBeTruthy();

    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      matched: 2, skipped: [], version: '6', units: { sourceUnit: '100 шт', unit: 'шт', factor: 100 },
      memory: { remembered: 2, replaced: 0, failed: false },
    } } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Сопоставить 2 строки' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(apiClient.post).toHaveBeenCalledWith('/plan/revisions/rev3/lines/match', expect.objectContaining({
      lineIds: ['r1', 'r2'], workTypeId: 'w2', unitId: 'u-sht', sourceUnit: '100 шт', remember: true,
    }), { headers: { 'If-Match': '5' } });
    expect(toast.success).toHaveBeenCalledWith(expect.stringMatching(/Сопоставлено: 2 строки .* · запомнено/));
  });

  it('«и к следующему»: окно переходит к следующему разделу, где ждут, и правит по новой версии', async () => {
    mount({ kind: 'section', code: '9' });
    await pickWorkType('02 Монтаж электроустановочных изделий');
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      matched: 2, skipped: [], version: '6', units: { sourceUnit: '100 шт', unit: 'шт', factor: 100 },
      memory: { remembered: 0, replaced: 0, failed: false },
    } } as never);
    fireEvent.click(await screen.findByRole('button', { name: 'Сопоставить и к следующему' }));

    expect(await screen.findByRole('heading', { name: 'Сопоставить раздел 10' })).toBeTruthy();
    await pickWorkType('02 Монтаж электроустановочных изделий');
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      matched: 1, skipped: [], version: '7', units: { sourceUnit: '100 шт', unit: 'шт', factor: 100 },
      memory: { remembered: 0, replaced: 0, failed: false },
    } } as never);
    fireEvent.click(await screen.findByRole('button', { name: 'Сопоставить 1 строку' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenLastCalledWith('/plan/revisions/rev3/lines/match',
      expect.objectContaining({ lineIds: ['r6'] }), { headers: { 'If-Match': '6' } }));
  });

  it('409: причина словами сервера, окно открыто, выбор цел', async () => {
    const onClose = mount({ kind: 'section', code: '9' });
    await pickWorkType('02 Монтаж электроустановочных изделий');
    vi.mocked(apiClient.post).mockRejectedValueOnce({
      response: { status: 409, data: { error: 'Редакцию тем временем изменили — перечитайте её и повторите.' } },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Сопоставить 2 строки' }));

    expect((await screen.findByRole('alert')).textContent).toMatch(/тем временем изменили — перечитайте/);
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('combobox', { name: 'Вид работы' }).textContent).toContain('Монтаж электроустановочных изделий');
    expect(screen.getByRole('button', { name: 'Сопоставить 2 строки' }).hasAttribute('disabled')).toBe(false);
  });

  it('другая единица раздела — набранный коэффициент сброшен: он был для прежней', async () => {
    mount({ kind: 'section', code: '9' });
    await pickWorkType('02 Монтаж электроустановочных изделий');
    const factor = screen.getByRole('textbox', { name: 'Коэффициент пересчёта' }) as HTMLInputElement;
    fireEvent.change(factor, { target: { value: '100' } });
    expect(factor.value).toBe('100');

    fireEvent.click(screen.getByRole('button', { name: /^100 м/ }));
    expect((screen.getByRole('textbox', { name: 'Коэффициент пересчёта' }) as HTMLInputElement).value).toBe('');
  });
});

describe('новый вид работы — только с правом на классификатор', () => {
  it('без core.worktypes.edit действия нет в разметке', async () => {
    mount({ kind: 'section', code: '9' });
    await screen.findByRole('combobox', { name: 'Вид работы' });
    expect(screen.queryByRole('button', { name: 'Новый вид работы' })).toBeNull();
  });

  it('с правом — шаг окна, заведённый вид сразу выбран', async () => {
    permissions = ['plan.estimate.edit', 'core.worktypes.edit'];
    vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
      if (url === '/work-types') return { data: WORK_TYPES } as never;
      if (url === '/document-types') return { data: [{ id: 't-unit', code: 'ЕдиницаИзмерения' }] } as never;
      if (url === '/common-data') return { data: [{ id: 'u-sht', displayName: 'шт' }, { id: 'u-m', displayName: 'м' }] } as never;
      if (url === '/plan/unit-factor') return { data: { factor: 100, sourceUnit: '100 шт', unitId: 'u-new', unit: 'шт', reason: null } } as never;
      throw new Error(`неожиданный адрес ${url}`);
    });
    mount({ kind: 'section', code: '9' });
    fireEvent.click(await screen.findByRole('button', { name: 'Новый вид работы' }));
    fireEvent.change(await screen.findByLabelText(/^Код/), { target: { value: '03' } });
    fireEvent.change(screen.getByLabelText(/^Наименование/), { target: { value: 'Монтаж розеток' } });
    // Единица предложена по единице расценки: «100 шт» → «шт».
    await waitFor(() => expect(screen.getByRole('combobox', { name: /Единица измерения/ }).textContent).toContain('шт'));

    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      id: 'w3', displayName: '03 Монтаж розеток', data: { Код: '03', ЕдиницаИзмерения: { entryId: 'u-sht', displayName: 'шт' } },
    } } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Завести и выбрать' }));

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/work-types', expect.objectContaining({
      displayName: '03 Монтаж розеток',
    })));
    const body = JSON.parse((vi.mocked(apiClient.post).mock.calls[0][1] as { data: string }).data);
    expect(body).toEqual({ Код: '03', Наименование: 'Монтаж розеток', ЕдиницаИзмерения: { $ref: 'catalog', entryId: 'u-sht', displayName: 'шт' } });
  });
});

describe('строка: «запомнено» с отменой', () => {
  it('«Отменить подстановку» снимает сопоставление строки — она снова ждёт', async () => {
    const onClose = mount({ kind: 'row', rowId: 'r4' });
    expect(await screen.findByText('запомнено')).toBeTruthy();
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: {
      row: { ...ROWS[3], workTypeId: null, marks: [{ code: 'unmatched', text: 'Не сопоставлена' }] }, version: '6',
      units: { sourceUnit: '100 м', unit: null, factor: null }, memory: { remembered: 0, replaced: 0, failed: false },
    } } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Отменить подстановку' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(apiClient.put).toHaveBeenCalledWith('/plan/revisions/rev3/lines/r4/match', { clear: true }, { headers: { 'If-Match': '5' } });
    expect(toast.success).toHaveBeenCalledWith('Строка 9.1 снова ждёт сопоставления');
  });

  it('у сопоставленной строки — нынешний выбор подставлен в поля', async () => {
    mount({ kind: 'row', rowId: 'r4' });
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Вид работы' }).textContent).toContain('Прокладка кабеля до 1 кВ'));
    expect((screen.getByRole('textbox', { name: 'Коэффициент пересчёта' }) as HTMLInputElement).value).toBe('100');
  });
});
