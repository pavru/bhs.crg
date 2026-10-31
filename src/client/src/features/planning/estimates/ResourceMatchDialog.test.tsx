// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { EstimateMaterial } from '@/shared/api/planningEstimates';
import { ResourceMatchDialog } from './ResourceMatchDialog';
import { REVISION, line } from './matchFixtures';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), apiError: vi.fn() }));
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => toast }));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));

/**
 * Окно материала (plan-09, задача 4.2, ТЗ PLAN-9.1): незнакомый материал — подбор говорит «не нашлось» и не
 * выдаёт это за «позиции нет», поиск находит, выбор ложится на все строки материала, окно идёт к следующему.
 */

const material = (id: string, sourceName: string, sourceCode: string): EstimateMaterial => ({
  id, order: 1, nomenclatureId: null, sourceName, sourceCode, sourceUnit: 'м', quantity: 10,
  marks: [{ code: 'unmatched', text: 'Не сопоставлен' }],
});
const ROWS = [
  line({ id: 'r1', number: '9.4', materials: [material('m1', 'Провод ПуГВ 1х2,5', 'ФССЦ-0071')] }),
  line({ id: 'r2', number: '9.5', materials: [material('m2', 'Кабель-канал 40х25', 'ФССЦ-0090')] }),
];
const group = (key: string, sourceName: string, sourceCode: string) => ({
  key, sourceName, sourceCode, sourceUnit: 'м', rows: 1, quantity: 10, nomenclatureId: null,
  marks: [{ code: 'unmatched', text: 'Не сопоставлен: нет позиции номенклатуры' }],
});

beforeEach(() => {
  permissions = ['plan.estimate.edit'];
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/revisions/rev3/materials')
      return { data: { items: [group('k1', 'Провод ПуГВ 1х2,5', 'ФССЦ-0071'), group('k2', 'Кабель-канал 40х25', 'ФССЦ-0090')] } } as never;
    if (url.endsWith('/candidates'))
      return { data: { items: [], more: false, inArchive: 0, unreadable: 0, recalledUnusable: null } } as never;
    if (url === '/plan/nomenclature')
      return { data: { items: [{ id: 'n1', name: 'Провод ПуГВ 1х2,5 белый', type: 'Материал', matchedAlias: null }], more: false, inArchive: 1 } } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <ResourceMatchDialog revision={REVISION} rows={ROWS} onClose={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe('окно материала: незнакомый материал', () => {
  it('подбор не нашёл — сказано, что это не «позиции нет»; поиск находит, выбор — на все строки материала', async () => {
    mount();
    expect(await screen.findByText('Материал 1 из 2 · ред. 3')).toBeTruthy();
    expect(await screen.findByText('По словам строки подходящих не нашлось.')).toBeTruthy();
    expect(screen.getByText(/Это не значит, что позиции в справочнике нет/)).toBeTruthy();
    // Без права на номенклатуру «Новой позиции» нет вовсе.
    expect(screen.queryByRole('button', { name: 'Новая позиция…' })).toBeNull();
    expect(apiClient.get).toHaveBeenCalledWith('/plan/revisions/rev3/resources/m1/candidates', expect.anything());

    fireEvent.change(screen.getByRole('textbox', { name: 'Поиск позиции' }), { target: { value: 'ПуГВ' } });
    const found = await screen.findByText('Провод ПуГВ 1х2,5 белый');
    expect(screen.getByText(/В архиве под этот запрос: 1/)).toBeTruthy();

    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { resources: 1, matched: 1, version: '6', memory: { remembered: 1, replaced: 0, failed: false } } } as never);
    fireEvent.click(found);

    await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/plan/revisions/rev3/materials/match',
      { key: 'k1', nomenclatureId: 'n1', remember: true }, { headers: { 'If-Match': '5' } }));
    // Следующий материал — в этом же окне, по версии из ответа.
    expect(await screen.findByText('Материал 2 из 2 · ред. 3')).toBeTruthy();
    expect(screen.getByText('Кабель-канал 40х25')).toBeTruthy();
    expect(toast.success).toHaveBeenCalledWith(expect.stringMatching(/Провод ПуГВ 1х2,5» → «Провод ПуГВ 1х2,5 белый» в 1 строке · запомнено/));
  });

  it('«Пропустить» оставляет материал ждать и ведёт к следующему; после последнего — «пройдены все»', async () => {
    mount();
    await screen.findByText('Материал 1 из 2 · ред. 3');
    fireEvent.click(screen.getByRole('button', { name: 'Пропустить' }));
    expect(await screen.findByText('Материал 2 из 2 · ред. 3')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Пропустить' }));
    expect(await screen.findByText('Пройдены все материалы без номенклатуры.')).toBeTruthy();
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  it('с правом на номенклатуру — «Новая позиция…» есть', async () => {
    permissions = ['plan.estimate.edit', 'core.nomenclature.edit'];
    mount();
    expect(await screen.findByRole('button', { name: 'Новая позиция…' })).toBeTruthy();
  });
});
