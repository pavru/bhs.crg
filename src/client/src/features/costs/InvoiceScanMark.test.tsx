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
import { SCAN_MARK_SPOKEN } from './InvoiceFieldScan';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/ui/viewer/viewerGate', () => ({ preloadPdfViewer: vi.fn() }));
vi.mock('@/shared/ui/viewer/PdfViewer', () => ({ PdfViewer: () => null, PdfViewerOverlay: () => null }));

/**
 * Метка «со скана, не проверено» в поле (costs-invoice-screen-11, задача 4.1, D-11.4, ТЗ COST-6.4): у поля на
 * `TextField` — кнопка хвоста «Показать на скане» вместо рамки и строки подсказки, подпись для диктора с
 * припиской. Поставщик, плательщик и даты ждут change `sharedui-*` (4.1a) — у них прежняя подсказка.
 */

afterEach(cleanup);

const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

function view(unconfirmed: string[]): InvoiceView {
  return {
    id: 'i1', version: 'v1', documentTypeId: 't', unconfirmed,
    requisites: { [K.number]: '417', [K.total]: 1830, [K.date]: '2026-10-05', Состояние: 'Черновик' },
    duplicates: [], sameFile: [], lines: [], readiness: { required: [], gaps: [] },
  } as unknown as InvoiceView;
}

function fields(v: InvoiceView, onShowScan?: () => void) {
  const value = (key: string) => v.requisites[key];
  render(
    <QueryClientProvider client={client()}>
      {/* Запертый: под полем нет строк распознавания — проверяется сама метка. */}
      <BlockFields block={BLOCKS[0]} view={v} edits={{}} organizations={[]} organizationsUnread={false}
        value={value} set={() => {}} locked onShowScan={onShowScan} />
    </QueryClientProvider>,
  );
}

describe('метка «со скана» в поле на TextField (4.1)', () => {
  it('кнопка хвоста «Показать на скане» открывает файл; рамки и подсказки нет; диктор слышит приписку', () => {
    const show = vi.fn();
    fields(view([K.number, K.total]), show);
    const marks = screen.getAllByRole('button', { name: 'Показать на скане' });
    expect(marks).toHaveLength(2);
    fireEvent.click(marks[0]);
    expect(show).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('textbox', { name: `Номер ${SCAN_MARK_SPOKEN}` })).toBeTruthy();
    expect(screen.queryByText(UNCONFIRMED_HINT)).toBeNull();
    expect(document.querySelector('.ring-warning')).toBeNull();
  });

  it('правка снимает метку — поле без метки и без приписки', () => {
    const v = view([K.number]);
    render(
      <QueryClientProvider client={client()}>
        <BlockFields block={BLOCKS[0]} view={v} edits={{ [K.number]: '418' }} organizations={[]} organizationsUnread={false}
          value={key => v.requisites[key]} set={() => {}} locked onShowScan={() => {}} />
      </QueryClientProvider>,
    );
    expect(screen.queryByRole('button', { name: 'Показать на скане' })).toBeNull();
    expect(screen.getByRole('textbox', { name: 'Номер' })).toBeTruthy();
  });

  it('без файла — прежняя рамка с подсказкой: показать нечего, а метка видна', () => {
    fields(view([K.number]));
    expect(screen.queryByRole('button', { name: 'Показать на скане' })).toBeNull();
    expect(screen.getByText(UNCONFIRMED_HINT)).toBeTruthy();
  });

  it('дата ждёт change sharedui (4.1a): у неё прежняя подсказка, кнопки нет', () => {
    fields(view([K.date]), () => {});
    expect(screen.queryByRole('button', { name: 'Показать на скане' })).toBeNull();
    expect(screen.getByText(UNCONFIRMED_HINT)).toBeTruthy();
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
