// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import type { PrintProfile } from '@/shared/api/printProfiles';
import { BuiltImage } from './InvoiceFileImage';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * Панель вида для чтения (issue #1456): строку о профиле и выбор профиля видит только тот, кто может
 * перестроить; выбор перестраивает по выбранному профилю. Пока чип показан, состояние профиля и пометка
 * о раскладке — в его подсказке и окне, а не строками (issue #1495).
 */

const NOTE = 'Лист вписан в ширину страницы по профилю печати «по умолчанию»: в Excel он печатается иначе.';
const FAILED = 'Раскладку по профилю применить не удалось — вид построен по файлу как есть.';
const SHEETS = 'Листов с данными: 2 — «Счёт», «Параметры»; каждый начинается с новой страницы.';

const profile = (id: string, name: string, isSystem = false): PrintProfile => ({
  id, name, paper: 'A4', orientation: isSystem ? 'portrait' : 'landscape', fit: 'width',
  margins: { top: 5, bottom: 5, left: 5, right: 5 }, isSystem, isFactory: true, factory: null,
});
const PROFILES = [profile('sys', 'по умолчанию', true), profile('wide', 'Альбомный')];

const image = (over: Partial<InvoiceImage> = {}): InvoiceImage => ({
  state: 'built', pages: 1, notes: [], converter: null, builtAt: null, reason: null, aboutFile: false,
  layout: 'profile', profile: { id: 'sys', name: 'по умолчанию' }, profileState: 'changed', layoutNote: NOTE, ...over,
});

let filesUse = true;

afterEach(cleanup);
beforeEach(() => {
  filesUse = true;
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.post).mockReset();
  vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
    if (url === '/settings/print-profiles') return { data: PROFILES };
    if (url === '/account/access') return { data: { permissions: filesUse ? ['core.files.use'] : [], modules: [] } };
    throw new Error('вида в тесте нет');
  }) as never);
});

function shown(view: InvoiceImage, canEdit: boolean) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SnackbarProvider>
        <BuiltImage invoiceId="i1" blobPath="files/a.xlsx" fileName="Счёт.xlsx" image={view} canEdit={canEdit} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('вид для чтения: профиль печати', () => {
  it('читателю — ни строки о профиле, ни выбора, и список профилей не спрашивается; пометка о раскладке — строкой', async () => {
    shown(image(), false);
    await waitFor(() => expect(screen.getByTestId('image-caveat')).toBeTruthy());
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    expect(screen.getByTestId('layout-note').textContent).toBe(NOTE);
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(false);
  });

  it('тому, кто может перестроить, — «изменён» в чипе, его подсказке и окне; выбор перестраивает по выбранному', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: image({ profile: { id: 'wide', name: 'Альбомный' }, profileState: 'current' }) } as never);
    shown(image({ notes: [SHEETS] }), true);
    const chip = await screen.findByTestId('layout-chip');
    expect(chip.textContent).toBe('Раскладка: по умолчанию · изменён');
    expect(chip.getAttribute('aria-description')).toContain('изменён после построения вида');
    expect(chip.getAttribute('aria-description')).toContain(NOTE);
    // Строками — только пометки другого рода: состояние и раскладка у чипа.
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    expect(screen.queryByTestId('layout-note')).toBeNull();
    expect(screen.getByText(SHEETS)).toBeTruthy();
    fireEvent.click(chip);
    expect((await screen.findByTestId('layout-notice')).textContent).toContain('изменён после построения вида');
    expect(screen.getByTestId('layout-note').textContent).toBe(NOTE);
    const sys = await screen.findByRole('radio', { name: /по умолчанию/ });
    expect(sys.getAttribute('aria-checked')).toBe('true');
    expect(sys.textContent).toContain('сейчас');
    fireEvent.click(screen.getByRole('radio', { name: /Альбомный/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Перестроить по выбранному' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/costs/invoices/i1/scan/image', { printProfileId: 'wide' }));
    expect(await screen.findByText('Вид для чтения перестроен по профилю «Альбомный».')).toBeTruthy();
  });

  it('без права работы с файлами выбора нет, и список профилей не спрашивается — а строка «изменён» есть', async () => {
    filesUse = false;
    shown(image(), true);
    expect(screen.getByTestId('layout-notice').textContent).toContain('изменён после построения вида');
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/account/access')).toBe(true));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(false);
  });

  it('у книги со своей раскладкой выбора нет, а «Перестроить» — без профиля', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: image({ layout: 'file', profile: null, profileState: null }) } as never);
    shown(image({ layout: 'file', profile: null, profileState: null }), true);
    fireEvent.click(screen.getByRole('button', { name: /Перестроить/ }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/costs/invoices/i1/scan/image', undefined));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(screen.queryByTestId('layout-notice')).toBeNull();
  });

  // Ни один текст состояния #1456 не потерян: подпись чипа называет состояние, полный текст — в окне.
  it.each([
    ['deleted', { profile: null, profileState: 'deleted' }, 'Раскладка: профиль удалён', 'по которому построен вид, удалён'],
    ['none', { profile: null, profileState: 'none', layoutNote: null }, 'Раскладка: до профилей', 'Вид построен до профилей печати'],
  ] as const)('состояние «%s» — в подписи чипа и в окне', async (_, over, label, said) => {
    shown(image(over), true);
    const chip = await screen.findByTestId('layout-chip');
    expect(chip.textContent).toBe(label);
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    fireEvent.click(chip);
    const notice = await screen.findByTestId('layout-notice');
    expect(notice.textContent).toContain(said);
    // Кнопка окна строит по отмеченному профилю — текст не обещает того, что делает «Перестроить» рядом.
    expect(notice.textContent).not.toContain('Перестроить');
  });

  it('профиль не применён — подпись чипа, а почему — пометка в подсказке и окне, не строкой', async () => {
    shown(image({ profile: null, profileState: null, layoutNote: FAILED }), true);
    const chip = await screen.findByTestId('layout-chip');
    expect(chip.textContent).toBe('Раскладка: профиль не применён');
    expect(chip.getAttribute('aria-description')).toContain(FAILED);
    expect(screen.queryByText(FAILED)).toBeNull();
    fireEvent.click(chip);
    expect((await screen.findByTestId('layout-note')).textContent).toBe(FAILED);
  });

  it('раскладка из файла — в подсказке фразы, чипа нет', async () => {
    shown(image({ layout: 'file', profile: null, profileState: null, layoutNote: null }), true);
    await waitFor(() => expect(screen.getByTestId('image-caveat-phrase').getAttribute('aria-description'))
      .toContain('Раскладка — из самого файла'));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
  });

  it('не книга — ни чипа, ни текстов о профиле; «документ — оригинал» на виду', async () => {
    shown(image({ layout: null, profile: null, profileState: null, layoutNote: null }), true);
    const phrase = await screen.findByTestId('image-caveat-phrase');
    expect(phrase.textContent).toBe('Вид для чтения · документ — оригинал');
    expect(phrase.getAttribute('aria-description')).toContain('вёрстка может отличаться');
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    expect(screen.queryByTestId('layout-note')).toBeNull();
  });

  it('профиль один — чипа нет, состояние и пометка о раскладке строками, как прежде', async () => {
    vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
      if (url === '/settings/print-profiles') return { data: [PROFILES[0]] };
      if (url === '/account/access') return { data: { permissions: ['core.files.use'], modules: [] } };
      throw new Error('вида в тесте нет');
    }) as never);
    shown(image(), true);
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(true));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect((await screen.findByTestId('layout-notice')).textContent).toContain('изменён после построения вида');
    expect(screen.getByTestId('layout-note').textContent).toBe(NOTE);
  });

  it('пока профили не пришли, строк состояния нет — иначе они мелькнули бы и ушли в чип', async () => {
    let answer: (v: unknown) => void = () => {};
    vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
      if (url === '/settings/print-profiles') return new Promise(resolve => { answer = resolve; });
      if (url === '/account/access') return { data: { permissions: ['core.files.use'], modules: [] } };
      throw new Error('вида в тесте нет');
    }) as never);
    shown(image({ notes: [SHEETS] }), true);
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(true));
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    expect(screen.queryByTestId('layout-note')).toBeNull();
    expect(screen.getByText(SHEETS)).toBeTruthy();
    answer({ data: PROFILES });
    await screen.findByTestId('layout-chip');
    expect(screen.queryByTestId('layout-notice')).toBeNull();
  });

  it('касанием фраза открывает то же, что её подсказка: «вёрстка может отличаться» не только при наведении', async () => {
    shown(image({ layout: null, profile: null, profileState: null, layoutNote: null, pages: 3 }), false);
    fireEvent.click(await screen.findByTestId('image-caveat-phrase'));
    const about = await screen.findByTestId('image-caveat-about');
    expect(about.textContent).toContain('вёрстка может отличаться');
    expect(about.textContent).toContain('3 стр.');
  });
});
