// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { ClipboardList } from 'lucide-react';
import type { AccessInfo } from '@/shared/api/access';
import { CommandPalette } from './CommandPalette';
import { paletteLabels as labels } from '@/shared/testing/palette';
import { NavigationGuardProvider } from './NavigationGuardProvider';

let access: AccessInfo;
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ setTheme: vi.fn() }) }));
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access }),
}));
// Пункт под «любым из» — тестовый: настоящий («Ход работ») вписывает plan-25.
vi.mock('./navConfig', async importOriginal => {
  const real = await importOriginal<typeof import('./navConfig')>();
  return {
    ...real,
    workNav: [...real.workNav, {
      to: '/progress', label: 'Ход работ', icon: ClipboardList, rail: 'plan',
      permission: ['plan.estimate.read', 'costs.report.read'],
    }],
  };
});

afterEach(cleanup);

const modules = [{ code: 'costs', title: 'Счета и накладные', available: true }];

function searchFor(who: AccessInfo, text: string) {
  access = who;
  render(
    <MemoryRouter>
      <NavigationGuardProvider>
        <CommandPalette open onOpenChange={() => {}} />
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
  fireEvent.change(screen.getByRole('textbox', { name: 'Поиск и действия', hidden: true }), { target: { value: text } });
}

/** Палитра (Ctrl+K) отбирает разделы тем же правилом, что меню (plan-02, ТЗ AUTH-16). */
describe('палитра и «любое из прав»', () => {
  it('раздел находится у носителя одного права из списка', () => {
    searchFor({ permissions: ['costs.report.read'], modules }, 'ход работ');
    expect(labels()).toContain('Ход работ');
  });

  it('без прав списка раздела в палитре нет', () => {
    searchFor({ permissions: ['costs.invoice.read'], modules }, 'ход работ');
    expect(labels()).not.toContain('Ход работ');
  });
});
