// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { AccountMenu } from './AccountMenu';
import { useLeaveGuard } from './NavigationGuard';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { railDef, type RailSection } from './railSections';

/**
 * Меню профиля в шапке (issue #1430): только своё — профиль, оформление, сообщение о проблеме, выход,
 * версия. Открывается с клавиатуры, Esc возвращает фокус на аватар, выход идёт через охрану правок.
 */

const logout = vi.fn();
const setTheme = vi.fn();
const openBugReport = vi.fn();
vi.mock('@/shared/hooks/useAuth', () => ({
  useAuth: () => ({ user: { displayName: 'Анна Королёва', email: 'a.koroleva@example.ru' }, logout }),
}));
vi.mock('@/shared/api/account', () => ({
  useAccount: () => ({ data: { avatar: null, roles: [{ title: 'Инженер ПТО' }, { title: 'Бухгалтер' }] } }),
}));
vi.mock('@/shared/api/version', () => ({ useAppVersion: () => ({ data: { version: '2.1.1', commit: 'abc1234' } }) }));
let updateAvailable = true;
vi.mock('@/shared/api/updates', () => ({ useUpdateStatus: () => ({ data: { updateAvailable, latest: '2.2.0' } }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ theme: 'system', setTheme }) }));
vi.mock('@/shared/ui/bugReportBus', () => ({ useBugReportDialog: () => ({ open: openBugReport }) }));

beforeAll(() => {
  // Слой меню меряет якорь — в jsdom этого нет.
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); updateAvailable = true; });

function Page({ dirty, asked }: { dirty: boolean; asked: (proceed: () => void) => void }) {
  useLeaveGuard(dirty, asked);
  return <output aria-label="адрес">{useLocation().pathname}</output>;
}

/** Раздел «Система» так, как его отдаёт оболочка телефона (issue #1434). */
const system: RailSection = {
  def: railDef('system'),
  groups: [{ items: [{ to: '/users', label: 'Пользователи', icon: railDef('system').icon, rail: 'system' }] }],
  home: '/users',
};

function shown(dirty = false, at = '/invoices', admin?: RailSection) {
  const asked = vi.fn<(proceed: () => void) => void>();
  render(
    <MemoryRouter initialEntries={[at]}>
      <NavigationGuardProvider>
        <Page dirty={dirty} asked={asked} />
        <AccountMenu admin={admin} />
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
  return asked;
}

const avatar = () => screen.getByRole('button', { name: /Анна Королёва — профиль и оформление/ });
function open() {
  act(() => avatar().focus());
  fireEvent.keyDown(avatar(), { key: 'Enter' });
  // Имя меню — имя аватара (Radix связывает их aria-labelledby).
  return screen.getByRole('menu', { name: /Анна Королёва — профиль и оформление/ });
}

describe('меню профиля', () => {
  it('аватар назван именем и объявляет меню', () => {
    shown();
    expect(avatar().getAttribute('aria-haspopup')).toBe('menu');
  });

  // Новость о новой версии — пассивная и видна без открытия меню (issue #813).
  it('новая версия отмечена на аватаре точкой и в его имени', () => {
    shown();
    expect(screen.getByTestId('update-dot')).toBeTruthy();
    expect(avatar().getAttribute('aria-label')).toMatch(/доступна версия 2\.2\.0/);
  });

  it('без новой версии точки нет', () => {
    updateAvailable = false;
    shown();
    expect(screen.queryByTestId('update-dot')).toBeNull();
    expect(avatar().getAttribute('aria-label')).not.toMatch(/доступна/);
  });

  it('в профиле пункт «Профиль и пароль» отмечен текущим', () => {
    shown(false, '/profile');
    open();
    expect(screen.getByRole('menuitem', { name: 'Профиль и пароль' }).getAttribute('aria-current')).toBe('page');
  });

  it('открывается с клавиатуры: кто я и только своё', () => {
    shown();
    const menu = open();
    expect(menu.textContent).toContain('Инженер ПТО, Бухгалтер');
    expect(menu.textContent).toContain('a.koroleva@example.ru');
    expect(screen.getAllByRole('menuitem').map(i => i.textContent)).toEqual(
      ['Профиль и пароль', 'Сообщить о проблеме', 'Выйти']);
    // Версия и новость о новой — пассивно, внизу (issue #813).
    expect(menu.textContent).toContain('Версия 2.1.1 · abc1234 · доступна 2.2.0');
  });

  it('тема подписана словами, выбранная отмечена, выбор меню не закрывает', () => {
    shown();
    open();
    const radios = screen.getAllByRole('menuitemradio');
    expect(radios.map(r => r.textContent)).toEqual(['Светлая', 'Тёмная', 'Авто']);
    expect(radios.map(r => r.getAttribute('aria-checked'))).toEqual(['false', 'false', 'true']);
    fireEvent.click(screen.getByRole('menuitemradio', { name: 'Тёмная' }));
    expect(setTheme).toHaveBeenCalledWith('dark');
    expect(screen.queryByRole('menu')).not.toBeNull();
  });

  it('Esc закрывает меню и возвращает фокус на аватар', async () => {
    shown();
    const menu = open();
    fireEvent.keyDown(menu, { key: 'Escape' });
    expect(screen.queryByRole('menu')).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(avatar()));
  });

  it('«Сообщить о проблеме» открывает форму и называет, откуда', () => {
    shown();
    open();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Сообщить о проблеме' }));
    expect(openBugReport).toHaveBeenCalledWith({ origin: 'account' });
  });

  it('«Профиль и пароль» ведёт в профиль', () => {
    shown();
    open();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Профиль и пароль' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/profile');
  });

  /** Телефон (issue #1434): рейла нет, и администрирование — пункт этого меню, отдельно от личных. */
  it('на телефоне «Администрирование» — отдельным блоком перед выходом и ведёт в раздел', () => {
    shown(false, '/invoices', system);
    open();
    expect(screen.getAllByRole('menuitem').map(i => i.textContent)).toEqual(
      ['Профиль и пароль', 'Сообщить о проблеме', 'Администрирование', 'Выйти']);
    const item = screen.getByRole('menuitem', { name: 'Администрирование' });
    // Черта отделяет пункт от личных.
    expect(item.parentElement?.previousElementSibling?.getAttribute('role')).toBe('separator');
    fireEvent.click(item);
    expect(screen.getByLabelText('адрес').textContent).toBe('/users');
  });

  it('на странице администрирования аватар показывает «вы здесь»', () => {
    shown(false, '/users', system);
    expect(avatar().className).toContain('ring-primary');
  });

  it('выход при несохранённых правках ждёт страницу', () => {
    const asked = shown(true);
    open();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Выйти' }));
    expect(logout).not.toHaveBeenCalled();
    expect(asked).toHaveBeenCalledTimes(1);
    act(() => asked.mock.calls[0][0]());
    expect(logout).toHaveBeenCalledTimes(1);
  });
});
