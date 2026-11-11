// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { PinnedPagesContext, type PinnedPages } from '@/shared/ui/pinnedPagesContext';
import { PinnedSectionsSection } from './PinnedSectionsSection';

/**
 * «Закреплённые страницы» в профиле (ТЗ AUTH-16.4, AUTH-16.7; work-04). Главное — каждое закрепление
 * видно строкой с причиной, и убирает его только «Открепить».
 */

let access: AccessInfo = { permissions: [], modules: [] };
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access }),
}));

afterEach(() => { cleanup(); access = { permissions: [], modules: [] }; });

function show(list: string[]) {
  const value: PinnedPages = {
    list, has: to => list.includes(to), pin: vi.fn(), unpin: vi.fn(), move: vi.fn(),
    collapsed: false, setCollapsed: vi.fn(),
  };
  render(
    <MemoryRouter>
      <PinnedPagesContext.Provider value={value}><PinnedSectionsSection /></PinnedPagesContext.Provider>
    </MemoryRouter>);
  return value;
}

const rows = () => screen.getAllByTestId('pinned-row');

describe('PinnedSectionsSection', () => {
  it('шесть видов строки — каждое закрепление на месте, с причиной', () => {
    access = {
      permissions: ['costs.invoice.read'],
      modules: [{ code: 'costs', title: 'Счета и накладные', available: true },
        { code: 'plan', title: 'Планирование', available: false }],
    };
    show(['/invoices', '/waybills', '/estimates', '/quality-docs', '/reports/brigades']);

    expect(rows().map(r => r.dataset.kind)).toEqual(['ok', 'permission', 'module-not-granted', 'module-off', 'gone']);
    expect(within(rows()[0]).getByText('Учёт затрат')).toBeTruthy();
    expect(within(rows()[1]).getByText('Нет доступа: нужно право costs.waybill.read')).toBeTruthy();
    expect(within(rows()[2]).getByText('Модуль «Планирование» вам не выдан — его выдаёт администратор')).toBeTruthy();
    expect(within(rows()[3]).getByText('Модуль страницы выключен на этом экземпляре')).toBeTruthy();
    expect(within(rows()[4]).getByText('/reports/brigades')).toBeTruthy();
    expect(within(rows()[4]).getByText('Страницы больше нет в навигации')).toBeTruthy();
    expect(screen.getByText('5 из 20')).toBeTruthy();
  });

  it('доступная страница — ссылка на неё; недоступная — без цели', () => {
    access = { permissions: ['costs.invoice.read'], modules: [{ code: 'costs', title: 'Счета', available: true }] };
    show(['/invoices', '/waybills']);
    expect(within(rows()[0]).getByRole('link').getAttribute('href')).toBe('/invoices');
    expect(within(rows()[1]).queryByRole('link')).toBeNull();
  });

  it('«Открепить» у недоступной строки снимает именно её', () => {
    const value = show(['/invoices', '/waybills']);
    fireEvent.click(within(rows()[1]).getByRole('button', { name: 'Открепить' }));
    expect(value.unpin).toHaveBeenCalledWith('/waybills');
  });

  it('«Выше» и «Ниже» переставляют строку; у краёв списка кнопка не действует', () => {
    const value = show(['/invoices', '/waybills', '/estimates']);
    fireEvent.click(within(rows()[2]).getByRole('button', { name: 'Выше: Сметы' }));
    expect(value.move).toHaveBeenCalledWith(2, 1);
    fireEvent.click(within(rows()[0]).getByRole('button', { name: 'Выше: Счета' }));
    fireEvent.click(within(rows()[2]).getByRole('button', { name: 'Ниже: Сметы' }));
    expect(value.move).toHaveBeenCalledTimes(1);
  });

  it('пусто — подсказка, как закрепить', () => {
    show([]);
    expect(screen.queryAllByTestId('pinned-row')).toHaveLength(0);
    expect(screen.getByText(/Закрепите страницу из меню «⋮»/)).toBeTruthy();
  });
});
