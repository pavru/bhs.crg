// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { AllocationPartView, InvoiceView } from '@/shared/api/invoices';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import type { Places } from './places';

/**
 * Выбор в шапке счёта (COST-12.1, D3): уточнение раздела в пределах стройки пишется сразу, несколько мест или
 * другая стройка — матрица. Сервер подменён: проверяется, ЧТО шапка шлёт, а не как сервер раскладывает.
 */

const K36 = { id: 'k', name: 'Комарова 36', sections: [{ id: 's4', name: '4 эт.' }, { id: 's5', name: '5 эт.' }] };
const PLACES: Places = { sites: [K36, { id: 'l', name: 'Ленина 5', sections: [] }], articles: [] };

const api = vi.hoisted(() => ({
  preview: vi.fn(async () => ({ apply: { lines: [], document: [] } })),
  replace: vi.fn(async () => ({})),
}));

vi.mock('@/shared/api/access', () => ({ NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true }));
vi.mock('@/shared/api/allocationMatrix', () => ({
  usePreviewAllocation: () => ({ mutateAsync: api.preview, isPending: false }),
  useReplaceMatrix: () => ({ mutateAsync: api.replace, isPending: false }),
}));
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ apiError: vi.fn() }) }));
vi.mock('./useCorrectionDraft', () => ({ useCorrecting: () => null }));
vi.mock('./AllocationMatrix', () => ({ AllocationMatrix: () => <div data-testid="matrix" /> }));
vi.mock('./places', async original => ({ ...await original<typeof import('./places')>(), usePlaces: () => PLACES }));

const { InvoiceObject } = await import('./InvoiceObject');

const filter: SiteFilterValue = {
  status: 'ready', favorites: new Set(), on: false, active: false,
  sites: PLACES.sites!, visible: () => true, setOn: () => {}, setFavorite: async () => {}, setFavorites: async () => {},
};

beforeEach(() => {
  window.matchMedia = vi.fn().mockImplementation(() => ({
    matches: true, addEventListener: () => {}, removeEventListener: () => {},
  })) as unknown as typeof window.matchMedia;
  api.preview.mockClear();
  api.replace.mockClear();
});
afterEach(cleanup);

const part = (construction: string, section: string | null = null) =>
  ({ constructionId: construction, sectionId: section, articleId: null }) as AllocationPartView;

/** Счёт из двух строк; `parts` — части каждой строки, `allocated` — разнесён ли полностью. */
function invoice(parts: AllocationPartView[][], allocated = true): InvoiceView {
  return {
    id: 'i1', version: 'v1', requisites: {},
    lines: parts.map((p, i) => ({ id: `l${i}`, ordinal: i + 1, unit: 'м', quantity: 10, amount: 10, allocation: { parts: p } })),
    allocation: { allocated, stamp: 'st1', document: { parts: [] } },
  } as unknown as InvoiceView;
}

function show(view: InvoiceView) {
  render(<SiteFilterContext.Provider value={filter}><InvoiceObject view={view} locked={false} /></SiteFilterContext.Provider>);
}

/** Выбрать раздел стройки «Комарова 36» вторым шагом поля. */
function pickSection(name: string) {
  fireEvent.click(screen.getByRole('combobox', { name: 'Объект счёта' }));
  fireEvent.click(screen.getByRole('option', { name: 'Комарова 36' }).nextElementSibling!);
  fireEvent.click(screen.getByRole('option', { name }));
}

const section = (id: string | null) => ({ construction: 'k', section: id, article: null });

describe('шапка счёта: сразу или матрица', () => {
  it('весь счёт на стройке → раздел: предпросмотр и запись с отметкой того вида, матрицы нет', async () => {
    show(invoice([[part('k')], [part('k')]]));
    pickSection('4 эт.');
    await waitFor(() => expect(api.replace).toHaveBeenCalled());
    expect(api.preview).toHaveBeenCalledWith({ id: 'i1', method: 'equal', targets: [section('s4')] });
    expect(api.replace).toHaveBeenCalledWith(expect.objectContaining({ id: 'i1', seen: 'v1', state: expect.objectContaining({ stamp: 'st1' }) }));
    expect(screen.queryByTestId('matrix')).toBeNull();
  });

  it('неполная разноска на стройке — тоже сразу: остаток ложится на раздел', async () => {
    show(invoice([[part('k')], []], false));
    pickSection('4 эт.');
    await waitFor(() => expect(api.replace).toHaveBeenCalled());
    expect(screen.queryByTestId('matrix')).toBeNull();
  });

  it('весь счёт на разделе → «вся стройка»: сразу, без раздела', async () => {
    show(invoice([[part('k', 's4')], [part('k', 's4')]]));
    pickSection('— вся стройка —');
    await waitFor(() => expect(api.replace).toHaveBeenCalled());
    expect(api.preview).toHaveBeenCalledWith(expect.objectContaining({ targets: [section(null)] }));
  });

  it('два раздела — матрица, записи нет', async () => {
    show(invoice([[part('k', 's4')], [part('k', 's5')]]));
    pickSection('4 эт.');
    await screen.findByTestId('matrix');
    expect(api.replace).not.toHaveBeenCalled();
  });

  it('другая стройка — матрица', async () => {
    show(invoice([[part('k', 's4')], [part('k', 's4')]]));
    fireEvent.click(screen.getByRole('combobox', { name: 'Объект счёта' }));
    fireEvent.click(screen.getByRole('option', { name: 'Ленина 5' }));
    await screen.findByTestId('matrix');
    expect(api.replace).not.toHaveBeenCalled();
  });
});

describe('шапка называет разноску по разделам', () => {
  it('весь счёт на раздел — стройка и раздел, «весь счёт на этот объект»', () => {
    show(invoice([[part('k', 's4')], [part('k', 's4')]]));
    expect(screen.getByRole('combobox', { name: 'Объект счёта' }).textContent).toBe('Комарова 36 / 4 эт.');
    expect(screen.getByText('весь счёт на этот объект').className).toContain('text-fg4');
  });

  it('несколько разделов — «разнесён на 2 раздела» без тона предупреждения', () => {
    show(invoice([[part('k', 's4')], [part('k', 's5')]]));
    expect(screen.getByRole('combobox', { name: 'Объект счёта' }).textContent).toBe('Комарова 36 · несколько разделов');
    expect(screen.getByText('разнесён на 2 раздела').className).toContain('text-fg4');
  });

  it('запертый счёт — стройка и раздел текстом, без выбора', () => {
    render(<SiteFilterContext.Provider value={filter}>
      <InvoiceObject view={invoice([[part('k', 's4')], [part('k', 's4')]])} locked />
    </SiteFilterContext.Provider>);
    expect(screen.queryByRole('combobox', { name: 'Объект счёта' })).toBeNull();
    expect(screen.getByText('Комарова 36 / 4 эт.')).toBeTruthy();
  });
});
