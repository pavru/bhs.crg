// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { CommandPalette } from './CommandPalette';
import { NavigationGuardProvider } from './NavigationGuardProvider';

let access: AccessInfo;
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ setTheme: vi.fn() }) }));
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access }),
}));

afterEach(cleanup);

const costs = [{ code: 'costs', title: 'Счета и накладные', available: true }];
const reader: AccessInfo = { permissions: ['costs.invoice.read'], modules: costs };
const clerk: AccessInfo = { permissions: ['costs.invoice.read', 'costs.invoice.edit'], modules: costs };

function Address() {
  const { pathname, search } = useLocation();
  return <output aria-label="адрес">{pathname + search}</output>;
}

function shown(who: AccessInfo) {
  access = who;
  render(
    <MemoryRouter initialEntries={['/']}>
      <NavigationGuardProvider>
        <Address />
        <CommandPalette open onOpenChange={() => {}} />
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
}

const search = () => screen.getByRole('textbox', { name: 'Поиск и действия', hidden: true });
const labels = () => screen.queryAllByRole('button', { hidden: true })
  .filter(b => b.closest('ul')).map(b => b.textContent?.trim().replace(/\s*Enter$/, ''));
function type(text: string) { fireEvent.change(search(), { target: { value: text } }); }
const address = () => screen.getByLabelText('адрес').textContent;

/** Командная палитра — один вход в «сделать» и «перейти» (issue #1433). */
describe('командная палитра', () => {
  it('действие ведёт на экран, который его исполнит', () => {
    shown(clerk);
    type('новый счет');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices?do=invoice-new');
  });

  it('название раздела ведёт в раздел, а не в действие со словом раздела', () => {
    shown(clerk);
    type('счета');
    expect(labels()[0]).toBe('Счета');
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(address()).toBe('/invoices');
  });

  it('без права кнопки действия нет — раздел остаётся', () => {
    shown(reader);
    type('счёт');
    expect(labels()).not.toContain('Новый счёт');
    expect(labels()).not.toContain('Счёт из файла');
    expect(labels()).toContain('Счета');
  });

  it('чип области сужает выдачу', () => {
    shown(clerk);
    fireEvent.click(screen.getByRole('button', { name: 'Разделы', hidden: true }));
    expect(labels()).toContain('Счета');
    expect(labels()).not.toContain('Новый счёт');
    expect(labels()).not.toContain('Выйти');

    fireEvent.click(screen.getByRole('button', { name: 'Действия', hidden: true }));
    expect(labels()).toContain('Новый счёт');
    expect(labels()).toContain('Тема: тёмная');
    expect(labels()).not.toContain('Счета');
  });

  it('«тёмная тема» находит тему, совпадение подсвечено', () => {
    shown(clerk);
    type('тёмная тема');
    expect(labels()).toEqual(['Тема: тёмная']);
    expect([...document.querySelectorAll('mark')].map(m => m.textContent)).toEqual(['Тема', 'тёмная']);
  });
});
