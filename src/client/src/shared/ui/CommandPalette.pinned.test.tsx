// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { paletteLabels as labels } from '@/shared/testing/palette';
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

const search = () => screen.getByRole('textbox', { name: 'Поиск и действия', hidden: true });

describe('«Закреплённые» в палитре', () => {
  it('пустой запрос — первыми, в порядке списка, с разделом второй строкой; недоступной нет', () => {
    shown(['/estimates', '/waybills', '/invoices']);
    const all = labels();
    // «Накладные» без права costs.waybill.read в группу не попадают.
    expect(all.slice(0, 2)).toEqual(['СметыПланирование', 'СчетаУчёт затрат']);
    expect(all.filter(l => l?.startsWith('Накладные'))).toEqual([]);
  });

  it('страница вне пространства — с пометкой', () => {
    shown(['/estimates'], { inside: item => item.module !== 'plan' });
    expect(labels()[0]).toBe('СметыПланирование · вне пространства');
  });

  it('с запросом группы нет — страница находится на своём месте', () => {
    shown(['/estimates']);
    fireEvent.change(search(), { target: { value: 'смет' } });
    expect(labels().filter(l => l?.includes('Планирование'))).toEqual([]);
    expect(labels()).toContain('Сметы');
  });

  it('в «Действиях» группы нет: закреплённая страница — переход, а не действие', () => {
    shown(['/estimates']);
    fireEvent.click(screen.getByRole('button', { name: 'Действия', hidden: true }));
    expect(labels().filter(l => l?.startsWith('Сметы'))).toEqual([]);
  });
});
