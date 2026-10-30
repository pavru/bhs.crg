// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { LinkPickerModal } from './LinkPickerModal';

/**
 * Вкладки окна выбора документа качества ([UI-44], #1416): прежде ряд кнопок без ролей. Теперь —
 * `Tabs`: стрелка ведёт фокус, не переключая, а Enter на «Поиск в интернете» идёт через `enterSearch`
 * и сразу ищет по материалу.
 */

const search = vi.fn<(q: string) => Promise<unknown[]>>(async () => []);
vi.mock('@/shared/api/qualityDocs', () => ({
  useListQualityDocs: () => ({ data: [], isLoading: false }),
  searchQualityDocs: (q: string) => search(q),
  importQualityDocFromUrl: vi.fn(),
  SEARCH_SOURCE_LABEL: {},
}));
vi.mock('@/shared/api/primitiveTypes', () => ({ useListPrimitiveTypes: () => ({ data: [] }) }));
vi.mock('@/shared/api/enumTypes', () => ({ useListEnumTypes: () => ({ data: [] }) }));
vi.mock('@/shared/hooks/useOpenAttachment', () => ({ useOpenAttachment: () => vi.fn() }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const material = { key: 'm1', label: 'Кабель ВВГнг 3×2,5', idValues: ['ВВГнг', '3×2,5'] };

describe('LinkPickerModal — вкладки', () => {
  it('стрелка ведёт фокус, Enter открывает поиск и ищет по материалу', async () => {
    render(<LinkPickerModal open onClose={() => {}} allDocTypes={[]} scope="System" scopeId={null}
      materials={[material]} onPick={() => {}} />);

    const pick = screen.getByRole('tab', { name: 'Из библиотеки' });
    expect(pick.getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tabpanel').id).toBe(pick.getAttribute('aria-controls'));

    pick.focus();
    fireEvent.keyDown(pick, { key: 'ArrowRight' });
    const web = screen.getByRole('tab', { name: 'Поиск в интернете' });
    expect(document.activeElement).toBe(web);
    // Стрелка только переводит фокус: вкладка та же, поиска нет.
    expect(pick.getAttribute('aria-selected')).toBe('true');
    expect(search).not.toHaveBeenCalled();

    // Enter на кнопке-вкладке — щелчок, как в браузере.
    await act(async () => { fireEvent.click(web); });
    expect(web.getAttribute('aria-selected')).toBe('true');
    expect(search).toHaveBeenCalledWith('ВВГнг 3×2,5');
    expect(screen.getByRole('tabpanel').id).toBe(web.getAttribute('aria-controls'));
  });
});
