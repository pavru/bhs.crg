// @vitest-environment jsdom
import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import type { AccessInfo } from '@/shared/api/access';
import { PALETTE_ACTIONS } from './paletteActions';
import { usePaletteIntent } from './usePaletteIntent';

let access: AccessInfo | undefined;
vi.mock('@/shared/api/access', () => ({
  useAccess: () => ({ data: access }),
  hasPermission: (a: AccessInfo, code: string) => a.permissions.includes(code),
}));

afterEach(() => { cleanup(); access = undefined; });

/** Экран, принимающий «Новый счёт» из палитры (issue #1433). */
function Screen({ run }: { run: () => void }) {
  usePaletteIntent(PALETTE_ACTIONS.invoiceNew, run);
  const { pathname, search } = useLocation();
  return <output aria-label="адрес">{pathname + search}</output>;
}

function shown(url: string) {
  const run = vi.fn();
  // Новый элемент на каждую отрисовку: тот же объект React пропустил бы, не перерисовав.
  const view = () => (
    <StrictMode>
      <MemoryRouter initialEntries={[url]}><Screen run={run} /></MemoryRouter>
    </StrictMode>
  );
  const { rerender } = render(view());
  return { run, again: () => rerender(view()) };
}

const address = () => screen.getByLabelText('адрес').textContent;

describe('экран принимает действие палитры', () => {
  it('исполняет ровно раз — и в строгом режиме — и снимает параметр из адреса', () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const { run } = shown('/invoices?do=invoice-new&q=x');
    expect(run).toHaveBeenCalledTimes(1);
    expect(address()).toBe('/invoices?q=x');
  });

  it('чужое действие не трогает', () => {
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    const { run } = shown('/invoices?do=invoice-scan');
    expect(run).not.toHaveBeenCalled();
    expect(address()).toBe('/invoices?do=invoice-scan');
  });

  it('ждёт загрузки прав, потом исполняет', () => {
    const { run, again } = shown('/invoices?do=invoice-new');
    expect(run).not.toHaveBeenCalled();
    access = { permissions: ['costs.invoice.edit'], modules: [] };
    again();
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('без права кнопки параметр снимается молча', () => {
    access = { permissions: [], modules: [] };
    const { run } = shown('/invoices?do=invoice-new');
    expect(run).not.toHaveBeenCalled();
    expect(address()).toBe('/invoices');
  });
});
