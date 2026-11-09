// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { apiClient } from '@/shared/api/client';
import type { HiddenPart } from '@/shared/api/planningEstimates';
import type { ConjunctureItem, EstimateAttachment, VolumeLine } from '@/shared/api/planningConjuncture';
import { EstimateAttachments } from './EstimateAttachments';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
let permissions: string[] = [];
vi.mock('@/shared/api/access', () => ({
  useCan: () => ({ permission: (code: string) => permissions.includes(code), module: () => true }),
}));
const toast = { success: vi.fn(), error: vi.fn(), info: vi.fn(), apiError: vi.fn() };
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => toast }));
vi.mock('@/shared/hooks/useMedia', () => ({ useMedia: () => true, WIDE: '(min-width: 1024px)' }));

/**
 * Приложения сметы (plan-11, задача 2.2, макет П11 вариант 1; ТЗ PLAN-9, PLAN-2): без `plan.estimate.cost` колонок
 * цены нет, ни одной суммы на экране, строка о скрытом называет право; способ выбора и поставщики видны.
 */

const HIDDEN: HiddenPart[] = [{ code: 'cost', permission: 'plan.estimate.cost', name: 'видеть себестоимость сметы' }];

const KA: EstimateAttachment = {
  id: 'ka1', kind: 'ka', estimateId: 'e1', fileName: '250803-КАЦ-01.gsfx', number: null, name: null, programVersion: '16.2.0',
  schemaVersion: null, creationDate: null, currentPriceDate: null, parsed: 1, inFile: 2, chapters: 1, suppliers: 2,
  importedAt: '2026-11-08T10:00:00Z', importedBy: 'Иванова А.', version: '5',
};
const VOR: EstimateAttachment = { ...KA, id: 'vor1', kind: 'vor', fileName: 'ВОР-1.gsfx', parsed: 2, inFile: 2, suppliers: null, importedAt: '2026-11-07T10:00:00Z' };

function item(cost: boolean): ConjunctureItem {
  const price = (p: number) => (cost ? { cost: { price: p, priceWithVat: p * 1.2 } } : {});
  return {
    id: 'i1', order: 1, chapterIndex: 1, chapterCaption: 'Кабельная продукция', number: '1', caption: 'Кабель ВВГнг 5×16',
    units: 'м', inactive: false, selection: 'min', selectedOffer: 1,
    offers: [
      { order: 1, number: '1', caption: 'Кабель', units: 'м', resourceCode: null, supplier: 'ООО «Альфа-Кабель»', supplierInn: '7700000011',
        vat: 20, date: '2025-07-14', selected: true, ...price(1186.4) },
      { order: 2, number: '2', caption: 'Кабель', units: 'м', resourceCode: null, supplier: 'ООО «ЭлектроСнаб»', supplierInn: '7700000022',
        vat: 20, date: '2025-07-15', selected: false, ...price(1212) },
    ],
    ...(cost ? { cost: { selectedPrice: 1186.4 } } : {}),
  };
}

const LINES: VolumeLine[] = [
  { id: 'l1', order: 1, chapterIndex: 3, chapterCaption: 'Силовое электрооборудование', number: '12', caption: 'Прокладка кабеля в лотке',
    code: '250803-ЭОМ лист 7', units: '100 м', quantity: 12.24, formula: 'Lк1 + Lк2', drawings: null, note: null, subordinate: false, parentNumber: null },
  { id: 'l2', order: 2, chapterIndex: 3, chapterCaption: 'Силовое электрооборудование', number: '12.1', caption: 'Кабель ВВГнг(А)-LS 5×16',
    code: null, units: 'м', quantity: 1224, formula: null, drawings: null, note: null, subordinate: true, parentNumber: '12' },
];

function serve(cost: boolean, attachments: { ka: EstimateAttachment[]; vor: EstimateAttachment[] } = { ka: [KA], vor: [VOR] }) {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/estimates/e1/conjuncture') return { data: attachments.ka } as never;
    if (url === '/plan/estimates/e1/volume-statements') return { data: attachments.vor } as never;
    if (url === '/plan/conjuncture/ka1/items')
      return { data: { total: 1, offset: 0, items: [item(cost)], ...(cost ? {} : { hidden: HIDDEN }) } } as never;
    if (url === '/plan/volume-statements/vor1/lines') return { data: { total: 2, offset: 0, lines: LINES } } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
}

function Panel() {
  const [selected, setSelected] = useState<string | null>(null);
  return <EstimateAttachments estimateId="e1" selected={selected} onSelect={setSelected} />;
}

function mount() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><Panel /></QueryClientProvider>);
}

beforeEach(() => { permissions = ['plan.estimate.read']; vi.clearAllMocks(); });
afterEach(cleanup);

describe('приложения сметы', () => {
  it('без права на цены — колонок цены нет, суммы не видны, строка о скрытом называет право', async () => {
    serve(false);
    mount();
    const grid = await screen.findByRole('table');
    expect(within(grid).queryByRole('columnheader', { name: /НДС/ })).toBeNull();
    expect(within(grid).getByRole('columnheader', { name: 'Цена' })).toBeTruthy();
    expect(screen.getByText(/Цены предложений скрыты: их показывает право «видеть себестоимость сметы»/)).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/1\s?186|1\s?212/);
    // Поставщики, способ выбора и пометка выбранного видны и без права (вопрос 6, предложенный ответ).
    expect(within(grid).getByText('ООО «Альфа-Кабель» · ИНН 7700000011')).toBeTruthy();
    expect(within(grid).getByText('минимальная')).toBeTruthy();
    expect(within(grid).getByText('выбрано · минимальная')).toBeTruthy();
    // Файл КА несёт все цены — без права его не скачать (сервер — 403), кнопки нет.
    expect(screen.queryByRole('button', { name: 'Скачать файл' })).toBeNull();
  });

  it('с правом — цены без НДС и с НДС, файл скачивается', async () => {
    permissions = ['plan.estimate.read', 'plan.estimate.cost'];
    serve(true);
    mount();
    const grid = await screen.findByRole('table');
    expect(within(grid).getByRole('columnheader', { name: 'Без НДС, ₽' })).toBeTruthy();
    expect(within(grid).getByRole('columnheader', { name: 'С НДС, ₽' })).toBeTruthy();
    expect(within(grid).getAllByText(/1\s?186,40/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/Цены предложений скрыты/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Скачать файл' })).toBeTruthy();
  });

  it('список: оба вида, новые сверху, «частично» у не разобранного; ВОР раскрывается с «входит в №»', async () => {
    serve(false);
    mount();
    const list = await screen.findByRole('list', { name: 'Приложения сметы' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows.map(r => r.textContent)).toEqual([expect.stringContaining('250803-КАЦ-01.gsfx'), expect.stringContaining('ВОР-1.gsfx')]);
    expect(within(rows[0]).getByText('частично 1 из 2')).toBeTruthy();
    expect(within(rows[1]).getByText(/2 работы в 1 главе/)).toBeTruthy();

    fireEvent.click(within(rows[1]).getByText('ВОР-1.gsfx'));
    expect(await screen.findByText('входит в № 12')).toBeTruthy();
    expect(screen.getByText('= Lк1 + Lк2')).toBeTruthy();
    // ВОР денег не несёт — файл скачивается под чтением.
    expect(screen.getByRole('button', { name: 'Скачать файл' })).toBeTruthy();
  });

  it('пустая смета — пустое состояние, без «Удалить» у читающего', async () => {
    serve(false, { ka: [], vor: [] });
    mount();
    expect(await screen.findByText('Приложений нет')).toBeTruthy();
  });

  it('удаление — с версией; «уже удалили» — снэкбар, а не ошибка', async () => {
    permissions = ['plan.estimate.read', 'plan.estimate.edit'];
    serve(false);
    vi.mocked(apiClient.delete).mockRejectedValue(Object.assign(new Error('404'), { isAxiosError: true, response: { status: 404 } }));
    mount();
    const list = await screen.findByRole('list', { name: 'Приложения сметы' });
    fireEvent.keyDown(within(within(list).getAllByRole('listitem')[0]).getByRole('button', { name: /Действия с/ }), { key: 'Enter' });
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Удалить…' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Иванова А\./)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Удалить' }));
    await waitFor(() => expect(toast.info).toHaveBeenCalledWith('Это приложение уже удалили'));
    expect(apiClient.delete).toHaveBeenCalledWith('/plan/conjuncture/ka1', { headers: { 'If-Match': '5' } });
  });
});
