// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useRef } from 'react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceView } from '@/shared/api/invoices';
import { BLOCKS, K, UNCONFIRMED_HINT } from './invoiceFields';
import { BlockFields } from './InvoiceFormFields';
import { InvoiceFileButton, type FileShow } from './InvoiceFileButton';
import { SCAN_NOTE } from '@/shared/ui/fieldBox';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/ui/viewer/viewerGate', () => ({ preloadPdfViewer: vi.fn() }));
vi.mock('@/shared/ui/viewer/PdfViewer', () => ({ PdfViewer: () => null, PdfViewerOverlay: () => null }));

/**
 * Метка «со скана, не проверено» в поле (costs-invoice-screen-11, задачи 4.1 и 4.1a, D-11.4, ТЗ COST-6.4): общий
 * знак хвоста `fromScan` ([UI-88]) вместо рамки и строки подсказки — у всех полей шапки, на `TextField`, `Select` и
 * `DateField`; подпись для диктора с припиской. Кнопки в поле нет: «Показать на скане» — у пункта перечня о метках.
 */

afterEach(cleanup);

const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });
const scanMarks = () => document.querySelectorAll('[data-field-scan-mark]');

function view(unconfirmed: string[]): InvoiceView {
  return {
    id: 'i1', version: 'v1', documentTypeId: 't', unconfirmed,
    requisites: { [K.number]: '417', [K.total]: 1830, [K.date]: '2026-10-05', Состояние: 'Черновик' },
    duplicates: [], sameFile: [], lines: [], readiness: { required: [], gaps: [] },
  } as unknown as InvoiceView;
}

function fields(v: InvoiceView, edits: Record<string, unknown> = {}) {
  render(
    <QueryClientProvider client={client()}>
      {/* Запертый: под полем нет строк распознавания — проверяется сама метка. */}
      <BlockFields block={BLOCKS[0]} view={v} edits={edits} organizations={[]} organizationsUnread={false}
        value={key => v.requisites[key]} set={() => {}} locked />
    </QueryClientProvider>,
  );
}

describe('метка «со скана» в поле — общим знаком хвоста (4.1, 4.1a)', () => {
  it('у полей на TextField — знак, без кнопки, рамки и подсказки; диктор слышит приписку', () => {
    fields(view([K.number, K.total]));
    expect(scanMarks()).toHaveLength(2);
    expect(screen.queryByRole('button', { name: 'Показать на скане' })).toBeNull();
    expect(screen.getByRole('textbox', { name: `Номер (${SCAN_NOTE})` })).toBeTruthy();
    expect(screen.queryByText(UNCONFIRMED_HINT)).toBeNull();
    expect(document.querySelector('.ring-warning')).toBeNull();
  });

  it('правка снимает метку — поле без знака и без приписки', () => {
    fields(view([K.number]), { [K.number]: '418' });
    expect(scanMarks()).toHaveLength(0);
    expect(screen.getByRole('textbox', { name: 'Номер' })).toBeTruthy();
  });

  it('у поставщика (Select) — знак и приписка в имени выбора (4.1a)', () => {
    const v = view([K.supplier]);
    render(
      <QueryClientProvider client={client()}><SnackbarProvider>
        <BlockFields block={BLOCKS[0]} view={v} edits={{}} organizations={[{ id: 'o1', name: 'ООО Поставщик' }] as never}
          organizationsUnread={false} value={key => v.requisites[key]} set={() => {}} locked={false} />
      </SnackbarProvider></QueryClientProvider>,
    );
    expect(scanMarks()).toHaveLength(1);
    expect(screen.getByRole('combobox', { name: `Поставщик (${SCAN_NOTE})` })).toBeTruthy();
    expect(screen.queryByText(UNCONFIRMED_HINT)).toBeNull();
  });

  it('у даты (DateField) — тот же знак вместо рамки и подсказки (4.1a)', () => {
    fields(view([K.date]));
    expect(scanMarks()).toHaveLength(1);
    expect(screen.queryByText(UNCONFIRMED_HINT)).toBeNull();
    expect(document.querySelector('.ring-warning')).toBeNull();
  });
});

describe('«показать файл» у кнопки «Файл» — открыть, а не переключить', () => {
  function Probe({ file, onToggle, onReveal }: { file: 'over' | 'beside' | 'closed'; onToggle: () => void; onReveal: () => void }) {
    const show = useRef<FileShow>(null);
    return (
      <>
        <InvoiceFileButton invoiceId="i1" blobPath="files/a" fileName="Счёт" file={file} onToggle={onToggle}
          onReveal={onReveal} showRef={show} />
        <button onClick={() => show.current?.show()}>показать</button>
      </>
    );
  }
  const probe = (file: 'over' | 'beside' | 'closed') => {
    const onToggle = vi.fn();
    const onReveal = vi.fn();
    render(<QueryClientProvider client={client()}><SnackbarProvider><Probe file={file} onToggle={onToggle} onReveal={onReveal} /></SnackbarProvider></QueryClientProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'показать' }));
    return { onToggle, onReveal };
  };

  it('закрытый рядом — открывается панель', () => {
    const { onToggle, onReveal } = probe('closed');
    expect(onToggle).toHaveBeenCalledTimes(1);
    expect(onReveal).not.toHaveBeenCalled();
  });

  it('открытый рядом — не закрывается: страница показывает его панель', () => {
    const { onToggle, onReveal } = probe('beside');
    expect(onToggle).not.toHaveBeenCalled();
    expect(onReveal).toHaveBeenCalledTimes(1);
  });

  it('поверх формы, пока виды файлов не пришли, — ничего не открывает и не скачивает (ревью PR #1794)', () => {
    const save = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const { onToggle, onReveal } = probe('over');
    expect(onToggle).not.toHaveBeenCalled();
    expect(onReveal).not.toHaveBeenCalled();
    expect(save).not.toHaveBeenCalled();
    // Без запора открытие пошло бы за самим файлом — оригиналом, а не видом для чтения.
    expect(vi.mocked(apiClient.get).mock.calls.map(c => c[0])).not.toContain('/costs/invoices/i1/scan');
    save.mockRestore();
  });
});
