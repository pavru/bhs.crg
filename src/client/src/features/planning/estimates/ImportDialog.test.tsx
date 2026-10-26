// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { EstimateImport } from '@/shared/api/planningImports';
import { ImportDialog } from './ImportDialog';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn(), apiError: vi.fn() }) }));

/**
 * Окно загрузки сметы (plan-09, PR 2, задачи 3.1–3.3, ТЗ PLAN-6, PLAN-7): без цели загрузки нет (решение
 * В-12); при неполном разборе редакция создаётся только явным согласием; ВОР редакцией не становится (В-7).
 */

let shown: EstimateImport;
const report = (over: Partial<EstimateImport> = {}): EstimateImport => ({
  id: 'i1', constructionId: 'c1', fileName: 'ЛСР_02-01-03.gsfx', state: 'parsed', kind: 'lsr',
  kindName: 'Локальный сметный расчёт', programVersion: '2026.1', schemaVersion: '2026.1.1', progress: null,
  refusal: null, parsed: 300, inFile: 300, unparsed: [], skipped: [],
  summary: { lsrNumber: '02-01-03', lsrName: 'ЭОМ', method: 'rim', chapters: [], rows: 300, inactive: 0 },
  marks: [], revisionId: null, createdAt: '2026-10-26T10:00:00Z', createdBy: 'Смирнова А. В.', version: '5',
  target: { estimateId: 'e1', estimateName: 'ЭОМ корпус 2', newEstimate: null },
  ...over,
});
const unparsed = (n: number) =>
  Array.from({ length: n }, (_, i) => ({ place: `Раздел 9, поз. ${i + 1}`, line: 400 + i, reason: 'Нет единицы измерения.' }));

beforeEach(() => {
  shown = report();
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/estimates') return { data: { items: [{ id: 'e1', name: 'ЭОМ корпус 2', kind: 'main', revisions: [] }] } } as never;
    if (url === '/constructions/c1') return { data: { id: 'c1', name: 'Корпус 2', sections: [] } } as never;
    if (url === '/plan/imports/i1') return { data: shown } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount(props: { importId?: string; estimateId?: string } = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onCreated = vi.fn();
  render(
    <QueryClientProvider client={qc}>
      <ImportDialog open onClose={vi.fn()} constructionId="c1" onCreated={onCreated} {...props} />
    </QueryClientProvider>,
  );
  return onCreated;
}

const uploadButton = () => screen.getByRole('button', { name: 'Загрузить' });
const unavailable = (b: HTMLElement) => b.hasAttribute('disabled') || b.getAttribute('aria-disabled') === 'true';
const pickFile = () => fireEvent.change(screen.getByLabelText('Файл сметы'), {
  target: { files: [new File(['<xml/>'], 'ЛСР_02-01-03.gsfx')] },
});

describe('загрузка: файл и цель', () => {
  it('выбор цели обязателен: с файлом, но без сметы и без имени новой загрузки нет', async () => {
    mount();
    await screen.findByRole('tab', { name: 'Новая редакция сметы' });
    pickFile();
    expect(screen.getByText('ЛСР_02-01-03.gsfx')).toBeTruthy();
    // Смета не выбрана — ни одна не подставлена сама.
    expect(unavailable(uploadButton())).toBe(true);
    fireEvent.click(uploadButton());
    expect(apiClient.post).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('tab', { name: 'Новая смета' }));
    expect(unavailable(uploadButton())).toBe(true);
    fireEvent.change(screen.getByLabelText('Название сметы'), { target: { value: 'ЭОМ доп.' } });
    expect(unavailable(uploadButton())).toBe(false);

    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: report({ state: 'parsing', parsed: null, inFile: null, summary: null }) } as never);
    fireEvent.click(uploadButton());
    await waitFor(() => expect(apiClient.post).toHaveBeenCalled());
    const [url, form] = vi.mocked(apiClient.post).mock.calls[0] as [string, FormData];
    expect(url).toBe('/plan/imports');
    expect(form.get('newEstimate.name')).toBe('ЭОМ доп.');
    expect(form.get('newEstimate.kind')).toBe('main');
    expect(form.get('estimateId')).toBeNull();
  });

  it('из карточки сметы цель — её новая редакция: файла достаточно', async () => {
    mount({ estimateId: 'e1' });
    expect(await screen.findByText(/новая редакция сметы «ЭОМ корпус 2»/)).toBeTruthy();
    expect(screen.queryByRole('tab')).toBeNull();
    expect(unavailable(uploadButton())).toBe(true);
    pickFile();
    expect(unavailable(uploadButton())).toBe(false);
  });
});

describe('отчёт разбора', () => {
  it('неполный разбор: редакция не создаётся без явного согласия, согласие — acceptPartial', async () => {
    shown = report({ parsed: 298, unparsed: unparsed(2), marks: [{ code: 'partial', text: 'Разобрано 298 из 300.' }] });
    const onCreated = mount({ importId: 'i1' });
    expect(await screen.findByText(/Разобрано 298 из 300 позиций/)).toBeTruthy();
    expect(screen.getByText(/2 позиции не разобраны — в редакцию не войдут/)).toBeTruthy();
    // Прямой кнопки «Создать редакцию» нет — только называющая последствие.
    expect(screen.queryByRole('button', { name: 'Создать редакцию' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Создать редакцию без 2 строк…' }));
    expect(await screen.findByText(/добавить их можно только новой загрузкой/)).toBeTruthy();
    expect(apiClient.post).not.toHaveBeenCalled();

    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      import: report({ state: 'committed' }), revisionId: 'r4', revisionNumber: 4, estimateCreated: false, repeated: false,
      estimate: { id: 'e1', name: 'ЭОМ корпус 2', revisions: [{ id: 'r4', unmatched: 17, current: false }] },
    } } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Создать редакцию' }));
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith({ estimateId: 'e1', revisionId: 'r4', unmatched: true }));
    expect(apiClient.post).toHaveBeenCalledWith('/plan/imports/i1/commit', { acceptPartial: true }, { headers: { 'If-Match': '5' } });
  });

  it('полный разбор: создать редакцию одним действием, без согласия на частичный', async () => {
    mount({ importId: 'i1' });
    await screen.findByText(/Разобрано 300 из 300 позиций/);
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
      import: report({ state: 'committed' }), revisionId: 'r4', revisionNumber: 4, estimateCreated: false, repeated: false,
      estimate: { id: 'e1', name: 'ЭОМ корпус 2', revisions: [{ id: 'r4', unmatched: 0, current: false }] },
    } } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Создать редакцию' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/plan/imports/i1/commit', { acceptPartial: false },
      { headers: { 'If-Match': '5' } }));
  });

  it('ВОР не предлагает «создать редакцию» (решение В-7)', async () => {
    shown = report({
      kind: 'vor', kindName: 'Ведомость объёмов работ', parsed: null, inFile: null, summary: null,
      marks: [{ code: 'notEstimate', text: 'Это ведомость объёмов работ — редакцией сметы она не становится: уберите импорт.' }],
    });
    mount({ importId: 'i1' });
    expect(await screen.findByText(/редакцией сметы она не становится/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Создать редакцию/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Убрать импорт' })).toBeTruthy();
  });

  it('отказ разбора — полосой с причиной сервера; отказаться — с версией импорта', async () => {
    shown = report({ state: 'rejected', refusal: { code: 'UnsupportedVersion', text: 'Версия схемы 2019.2 не поддерживается.' },
      parsed: null, inFile: null, summary: null });
    mount({ importId: 'i1' });
    expect(await screen.findByText(/Версия схемы 2019\.2 не поддерживается/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Создать редакцию/ })).toBeNull();
    vi.mocked(apiClient.delete).mockResolvedValueOnce({} as never);
    fireEvent.click(screen.getByRole('button', { name: 'Убрать импорт' }));
    await waitFor(() => expect(apiClient.delete).toHaveBeenCalledWith('/plan/imports/i1', { headers: { 'If-Match': '5' } }));
  });
});
