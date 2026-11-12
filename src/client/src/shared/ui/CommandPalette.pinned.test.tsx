// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { paletteLabels as labels, paletteOptions } from '@/shared/testing/palette';
import { CommandPalette } from './CommandPalette';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { PinnedPagesContext, type PinnedPages } from './pinnedPagesContext';
import { WorkspaceContext, type WorkspaceContextValue } from './workspaceContext';

/**
 * Закреплённые в палитре (ТЗ AUTH-16.4, work-04; макет Р0): первой группой при ПУСТОМ запросе, только
 * переход; с запросом и в «Действиях» их нет; недоступной страницы нет; вторая строка — раздел и
 * «вне пространства».
 */

const access: AccessInfo = {
  permissions: ['costs.invoice.read', 'plan.estimate.read'],
  modules: [{ code: 'costs', title: 'Счета', available: true }, { code: 'plan', title: 'Планирование', available: true }],
};
// Поиск записей — молчит: здесь проверяется только группа «Закреплённые».
vi.mock('@/shared/api/recordSearch', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/recordSearch')>(),
  useRecordSearch: () => ({ groups: undefined, failed: false, pending: false, tooLong: false }),
}));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ setTheme: vi.fn() }) }));
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access }),
}));

afterEach(cleanup);

function shown(list: string[], workspace?: Partial<WorkspaceContextValue>) {
  const pinned: PinnedPages = {
    list, has: to => list.includes(to), pin: vi.fn(), unpin: vi.fn(), move: vi.fn(), collapsed: false, setCollapsed: vi.fn(),
  };
  const palette = (
    <PinnedPagesContext.Provider value={pinned}>
      <CommandPalette open onOpenChange={() => {}} />
    </PinnedPagesContext.Provider>);
  render(
    <MemoryRouter>
      <NavigationGuardProvider>
        {workspace
          ? <WorkspaceContext.Consumer>
            {base => <WorkspaceContext.Provider value={{ ...base, ...workspace }}>{palette}</WorkspaceContext.Provider>}
          </WorkspaceContext.Consumer>
          : palette}
      </NavigationGuardProvider>
    </MemoryRouter>);
}

/** Вторая строка варианта — подпись без заголовка. */
const note = (i: number) => {
  const option = paletteOptions()[i];
  const label = option.querySelector('[data-palette-label]')?.textContent ?? '';
  // Подсказка «Enter» стоит у выделенного варианта последней — во вторую строку она не входит.
  return option.textContent?.replace(label, '').trim().replace(/\s*Enter$/, '');
};
const search = () => screen.getByRole('textbox', { name: 'Поиск и действия', hidden: true });

describe('«Закреплённые» в палитре', () => {
  it('пустой запрос — первыми, в порядке списка, с разделом второй строкой; недоступной нет', () => {
    shown(['/estimates', '/waybills', '/invoices']);
    const all = labels();
    // «Накладные» без права costs.waybill.read в группу не попадают.
    expect(all.slice(0, 2)).toEqual(['Сметы', 'Счета']);
    expect(all).not.toContain('Накладные');
    expect(note(0)).toBe('Планирование');
    expect(note(1)).toBe('Учёт затрат');
  });

  it('страница вне пространства — с пометкой', () => {
    shown(['/estimates'], { inside: item => item.module !== 'plan' });
    expect(labels()[0]).toBe('Сметы');
    expect(note(0)).toBe('Планирование · вне пространства');
  });

  it('с запросом группы нет — страница находится на своём месте', () => {
    shown(['/estimates']);
    fireEvent.change(search(), { target: { value: 'смет' } });
    expect(labels().filter(l => l === 'Сметы')).toHaveLength(1);
    expect(paletteOptions().some(o => o.textContent?.includes('Планирование · '))).toBe(false);
    expect(paletteOptions().filter(o => o.textContent?.startsWith('Сметы') && o.textContent.includes('Учёт затрат'))).toEqual([]);
  });

  it('в «Действиях» группы нет: закреплённая страница — переход, а не действие', () => {
    shown(['/estimates']);
    fireEvent.click(screen.getByRole('button', { name: 'Действия', hidden: true }));
    expect(labels()).not.toContain('Сметы');
  });
});
