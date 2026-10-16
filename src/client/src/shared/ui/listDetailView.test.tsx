// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { ListDetailShell, NavItem } from './ListDetailShell';
import { useListDetailView } from './listDetailView';

afterEach(cleanup);

/**
 * Очередь «список / карточка» на узком месте (issue #1355). Сами классы ширины jsdom не считает —
 * проверяется то, что их выбирает: состояние очереди.
 */
describe('useListDetailView', () => {
  it('без записи из адреса начинает со списка', () => {
    const { result } = renderHook(() => useListDetailView());
    expect(result.current.view).toBe('list');
  });

  it('запись из адреса — карточка с первого кадра', () => {
    const { result } = renderHook(() => useListDetailView('inv-1'));
    expect(result.current.view).toBe('detail');
  });

  /** Уведомление или палитра меняют адрес, не трогая строку списка (ревью PR #1383). */
  it('смена записи в адресе показывает карточку, даже если перед этим вернулись к списку', () => {
    const { result, rerender } = renderHook(({ id }) => useListDetailView(id), { initialProps: { id: 'inv-1' as string | null } });
    act(() => result.current.showList());
    expect(result.current.view).toBe('list');

    rerender({ id: 'inv-2' });
    expect(result.current.view).toBe('detail');
  });

  it('та же запись при перерисовке не отменяет «К списку»', () => {
    const { result, rerender } = renderHook(({ id }) => useListDetailView(id), { initialProps: { id: 'inv-1' as string | null } });
    act(() => result.current.showList());
    rerender({ id: 'inv-1' });
    expect(result.current.view).toBe('list');
  });
});

function Page({ filter }: { filter?: boolean }) {
  const view = useListDetailView();
  return (
    <MemoryRouter>
      <span data-testid="view">{view.view}</span>
      <ListDetailShell title="Документы" view={view} detail={<div />}
        nav={<NavItem icon={null} label="Пункт" filter={filter} onClick={() => {}} />} />
    </MemoryRouter>
  );
}

describe('NavItem в сплите', () => {
  it('пункт, меняющий карточку, показывает её', () => {
    render(<Page />);
    fireEvent.click(screen.getByRole('button', { name: 'Пункт' }));
    expect(screen.getByTestId('view').textContent).toBe('detail');
  });

  /** Отбор по состоянию сужает список: показать карточку значило бы спрятать отобранное (ревью PR #1383). */
  it('пункт-отбор оставляет список', () => {
    render(<Page filter />);
    fireEvent.click(screen.getByRole('button', { name: 'Пункт' }));
    expect(screen.getByTestId('view').textContent).toBe('list');
  });
});

const sources = import.meta.glob<string>(['/src/features/**/*.tsx', '!/src/**/*.test.*'], { query: '?raw', import: 'default' });

/**
 * Страницы уровня: колонка — разделы уровня на `NavItem`, сплит ведёт очередь сам. Остальным
 * страницам со сплитом очередь обязана прийти от них: свой порядок сплита, начатый с карточки,
 * после «К списку» к карточке вернуть не может — строки не `NavItem`, и экран застревает на списке.
 */
const OWN_ORDER = [
  '/src/features/document-sets/ConstructionDetail.tsx',
  '/src/features/document-sets/SectionDetail.tsx',
  '/src/features/document-sets/SetDetail.tsx',
];

describe('страницы со сплитом', () => {
  it('со списком записей передают очередь `view`', async () => {
    const missing: string[] = [];
    let shells = 0;
    for (const [file, load] of Object.entries(sources)) {
      const text = await load();
      for (const [tag] of text.matchAll(/<ListDetailShell\b[^>]*/g)) {
        shells++;
        if (!OWN_ORDER.includes(file) && !/\bview=\{/.test(tag)) missing.push(file);
      }
    }
    expect(missing).toEqual([]);
    expect(shells).toBeGreaterThanOrEqual(12);
  }, 20_000);
});
