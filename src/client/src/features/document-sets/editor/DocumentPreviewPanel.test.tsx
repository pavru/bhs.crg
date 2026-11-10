// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useEffect } from 'react';
import type { PreviewResult } from '@/shared/api/documentSets';
import { DocumentPreviewPanel } from './DocumentPreviewPanel';

/**
 * Отказ обновления предпросмотра (вариант В макета «Отказ обновления предпросмотра»): прежний PDF остаётся в том
 * же просмотрщике под тем же ключом документа — лист, масштаб и поворот не теряются, — а отказ говорит строка
 * под шапкой. Плашка вместо PDF — только у отказа первого показа.
 */

const preview = vi.hoisted(() => vi.fn<(id: string, req: unknown) => Promise<PreviewResult>>());
vi.mock('@/shared/api/documentSets', () => ({ previewDocument: preview }));
vi.mock('./documentPreviewOpen', () => ({
  useDocumentPreview: () => ({ open: true, available: true }), setDocumentPreviewOpen: vi.fn(),
}));
vi.mock('@/shared/ui/viewer/viewerGate', () => ({ preloadPdfViewer: vi.fn() }));

// Просмотрщик — подставной: считает свои монтирования и показывает, что ему дали. Монтирование заново —
// это и есть потеря листа: настоящий просмотрщик держит место документа в своём состоянии.
const viewer = vi.hoisted(() => ({ mounts: 0 }));
vi.mock('@/shared/ui/viewer/PdfViewer', () => ({
  PdfViewer: ({ source, documentKey, dimmed }: { source: Blob; documentKey: string; dimmed?: boolean }) => {
    useEffect(() => { viewer.mounts++; }, []);
    return <div role="region" aria-label="Предпросмотр документа" data-testid="viewer"
      data-key={documentKey} data-source={(source as Blob & { tag?: string }).tag} data-dimmed={String(!!dimmed)} />;
  },
}));

const realMatchMedia = window.matchMedia;
beforeEach(() => {
  viewer.mounts = 0;
  preview.mockReset();
  vi.useFakeTimers();
  // Шире 768: standard-лист стоит в раскладке, а не окном во весь экран.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: true, media: q, addEventListener: vi.fn(), removeEventListener: vi.fn(), addListener: vi.fn(), removeListener: vi.fn(),
  })) as unknown as typeof window.matchMedia;
});
afterEach(() => { cleanup(); vi.useRealTimers(); window.matchMedia = realMatchMedia; });

function pdf(tag: string): PreviewResult {
  return { kind: 'pdf', blob: Object.assign(new Blob(['%PDF']), { tag }) };
}
const COMPILER = 'error: unknown variable: дата_акта\n   ┌─ main.typ:42:14';

/** Дебаунс 1,5 с и ответ сервера. */
async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(1500); });
}

describe('DocumentPreviewPanel — отказ обновления', () => {
  it('держит прежний PDF под тем же ключом, говорит отказ строкой и после исправления не перемонтирует просмотрщик', async () => {
    preview.mockResolvedValueOnce(pdf('v1'));
    const { rerender } = render(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 1 }} />);
    await settle();
    expect(screen.getByTestId('viewer').dataset.source).toBe('v1');
    expect(screen.queryByTestId('preview-stale')).toBeNull();
    // Живая область стоит в панели с самого начала, пустой: вставленную сразу с текстом диктор бы пропустил.
    const live = screen.getByTestId('preview-live');
    expect(live.getAttribute('role')).toBe('status');
    expect(live.textContent).toBe('');

    // Правка ломает шаблон — сервер отказал.
    preview.mockResolvedValueOnce({ kind: 'error', message: COMPILER });
    rerender(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 2 }} />);
    await settle();

    const shown = screen.getByTestId('viewer');
    expect(shown.dataset.source).toBe('v1');
    expect(shown.dataset.key).toBe('doc-1');
    expect(viewer.mounts).toBe(1);
    expect(screen.queryByTestId('preview-failure')).toBeNull();
    const stale = screen.getByTestId('preview-stale');
    expect(stale.dataset.state).toBe('error');
    expect(screen.getByTestId('preview-stale-text').textContent).toMatch(/^Не обновлён: не удалось построить · вариант от \d\d:\d\d$/);
    expect(screen.getByTestId('preview-live')).toBe(live);
    expect(live.textContent).toBe('Предпросмотр не обновлён: не удалось построить');
    // Устаревший лист приглушает сам просмотрщик.
    expect(shown.dataset.dimmed).toBe('true');

    // Текст сервера — по «Подробнее», целиком, с переносами строк. Свёрнутый — в разметке, но скрыт:
    // `aria-controls` кнопки указывает на него и тогда.
    const toggle = screen.getByRole('button', { name: 'Подробнее' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    const details = document.getElementById(toggle.getAttribute('aria-controls')!)!;
    expect(details.hidden).toBe(true);
    fireEvent.click(toggle);
    expect(details.hidden).toBe(false);
    expect(screen.getByTestId('preview-stale-message').textContent).toBe(COMPILER);
    expect(screen.getByRole('button', { name: 'Скрыть' }).getAttribute('aria-expanded')).toBe('true');

    // Ещё один отказ при наборе — раскрытое не схлопывается, текст сменился на месте.
    preview.mockResolvedValueOnce({ kind: 'error', message: 'error: expected expression' });
    rerender(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 3 }} />);
    await settle();
    expect(screen.getByTestId('preview-stale-message').textContent).toBe('error: expected expression');

    // «Повторить» после исправления: пока запрос в пути, кнопка занята и второго запроса не шлёт; затем новый
    // вариант в том же просмотрщике, строка отказа ушла.
    let answer!: (r: PreviewResult) => void;
    preview.mockImplementationOnce(() => new Promise(r => { answer = r; }));
    const retry = screen.getByRole('button', { name: 'Повторить' });
    await act(async () => { fireEvent.click(retry); });
    expect((retry as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(retry);
    expect(preview).toHaveBeenCalledTimes(4);
    await act(async () => { answer(pdf('v2')); });
    expect(screen.getByTestId('viewer').dataset.source).toBe('v2');
    expect(viewer.mounts).toBe(1);
    expect(screen.queryByTestId('preview-stale')).toBeNull();
    expect(live.textContent).toBe('');
    expect(screen.getByText('обновлено только что')).toBeTruthy();
    expect(screen.getByTestId('viewer').dataset.dimmed).toBe('false');
  });

  it('пропавший шаблон после показанного PDF — тоже строка, PDF на месте', async () => {
    preview.mockResolvedValueOnce(pdf('v1')).mockResolvedValueOnce({ kind: 'no-template' });
    const { rerender } = render(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 1 }} />);
    await settle();
    rerender(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 2 }} />);
    await settle();
    expect(screen.getByTestId('viewer').dataset.source).toBe('v1');
    expect(screen.getByTestId('preview-stale').dataset.state).toBe('no-template');
    expect(screen.getByTestId('preview-stale-text').textContent).toMatch(/^Не обновлён: нет шаблона/);
    // Подсказка человеку — обычным текстом, а не моноширинным блоком отказа компилятора.
    expect(screen.getByTestId('preview-stale-message').tagName).toBe('P');
  });

  it('отказ первого показа — плашка вместо PDF, как раньше', async () => {
    preview.mockResolvedValueOnce({ kind: 'error', message: COMPILER });
    render(<DocumentPreviewPanel instanceId="doc-1" requisites={{ n: 1 }} />);
    await settle();
    const failure = screen.getByTestId('preview-failure');
    expect(failure.dataset.state).toBe('error');
    expect(failure.getAttribute('role')).toBe('alert');
    expect(failure.textContent).toContain('unknown variable: дата_акта');
    expect(screen.queryByTestId('viewer')).toBeNull();
    expect(screen.queryByTestId('preview-stale')).toBeNull();

    preview.mockResolvedValueOnce(pdf('v1'));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Повторить' })); });
    expect(screen.getByTestId('viewer').dataset.source).toBe('v1');
    expect(screen.queryByTestId('preview-failure')).toBeNull();
  });
});
