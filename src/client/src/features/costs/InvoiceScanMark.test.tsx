// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useRef } from 'react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import type { InvoiceView } from '@/shared/api/invoices';
import { BLOCKS, K, UNCONFIRMED_HINT } from './invoiceFields';
import { BlockFields } from './InvoiceFormFields';
import { InvoiceFileButton } from './InvoiceFileButton';
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
  function Probe({ file, onToggle, none }: { file: 'over' | 'beside' | 'closed'; onToggle: () => void; none: () => void }) {
    const show = useRef<(() => void) | null>(null);
    return (
      <>
        <InvoiceFileButton invoiceId="i1" blobPath="files/a" fileName="Счёт" file={file} onToggle={onToggle} showRef={show} />
        <button onClick={() => (show.current ? show.current() : none())}>показать</button>
      </>
    );
  }
  const probe = (file: 'over' | 'beside' | 'closed') => {
    const onToggle = vi.fn();
    const none = vi.fn();
    render(<QueryClientProvider client={client()}><SnackbarProvider><Probe file={file} onToggle={onToggle} none={none} /></SnackbarProvider></QueryClientProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'показать' }));
    return { onToggle, none };
  };

  it('закрытый рядом — открывается панель', () => {
    const { onToggle, none } = probe('closed');
    expect(onToggle).toHaveBeenCalledTimes(1);
    expect(none).not.toHaveBeenCalled();
  });

  it('открытый рядом — не закрывается: «показать» его не переключает', () => {
    // Показывать его страница будет фокусом в панель — у кнопки «показать» для этого места нет.
    const { onToggle, none } = probe('beside');
    expect(onToggle).not.toHaveBeenCalled();
    expect(none).toHaveBeenCalledTimes(1);
  });
});
