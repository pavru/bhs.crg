// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { InvoicePlaceField } from './InvoicePlaceField';
import type { HeaderObject } from './matrix';
import type { Place, Places } from './places';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';

/**
 * Поле «стройка / раздел» шапки счёта (вариант Б, COST-12.1) — живой слой: «›» ведёт к разделам, щелчок
 * по стройке — вся стройка, набор ищет по обеим частям, роли — как у списка `Select`.
 */

const K36 = { id: 'k', name: 'Комарова 36', sections: [{ id: 's5', name: '5 эт.' }, { id: 's4', name: '4 эт.' }] };
const PLACES: Places = {
  sites: [K36, { id: 'l', name: 'Ленина 5', sections: [] }],
  articles: [{ id: 'a', name: 'Склад', archived: false }],
};
const filter: SiteFilterValue = {
  status: 'ready', favorites: new Set(), on: false, active: false,
  sites: PLACES.sites!, visible: () => true, setOn: () => {}, setFavorite: async () => {}, setFavorites: async () => {},
};

beforeEach(() => {
  // Рабочий стол: слой — Popover. Телефон проверяет живой прогон.
  window.matchMedia = vi.fn().mockImplementation(() => ({
    matches: true, addEventListener: () => {}, removeEventListener: () => {},
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

const at = (construction: string, section: string | null = null): Place => ({ construction, section, article: null });
const whole = (section: string | null): HeaderObject => ({ kind: 'one', place: at('k'), section, sections: 1, complete: true });

function Under({ children }: { children: ReactNode }) {
  return <SiteFilterContext.Provider value={filter}>{children}</SiteFilterContext.Provider>;
}

function show(current: HeaderObject, places = PLACES) {
  const onPick = vi.fn();
  render(<Under><InvoicePlaceField current={current} places={places} label="Объект счёта" onPick={onPick} /></Under>);
  return onPick;
}

const field = () => screen.getByRole('combobox', { name: 'Объект счёта' });
const open = () => fireEvent.click(field());
const search = () => screen.getByRole('combobox', { name: /^Найти/ });
const groups = () => screen.getAllByRole('group').map(g => [
  document.getElementById(g.getAttribute('aria-labelledby')!)?.textContent,
  within(g).getAllByRole('option').map(o => o.textContent),
]);

describe('поле «стройка / раздел» в шапке счёта', () => {
  it('поле показывает стройку с разделом', () => {
    show(whole('s4'));
    expect(field().textContent).toBe('Комарова 36 / 4 эт.');
  });

  it('щелчок по стройке — вся стройка', () => {
    const onPick = show({ kind: 'none' });
    open();
    fireEvent.click(screen.getByRole('option', { name: 'Ленина 5' }));
    expect(onPick).toHaveBeenCalledWith(at('l'));
  });

  it('«›» открывает разделы: «вся стройка» первым, раздел выбирается со стройкой', () => {
    const onPick = show({ kind: 'none' });
    open();
    const chevron = screen.getByRole('option', { name: 'Комарова 36' }).nextElementSibling as HTMLElement;
    fireEvent.click(chevron);
    expect(screen.getByRole('listbox', { name: 'Разделы стройки «Комарова 36»' })).toBeTruthy();
    expect(groups()).toEqual([['Комарова 36', ['— вся стройка —', '5 эт.', '4 эт.']]]);
    fireEvent.click(screen.getByRole('option', { name: '4 эт.' }));
    expect(onPick).toHaveBeenCalledWith(at('k', 's4'));
  });

  it('у стройки без разделов и у статьи «›» нет', () => {
    show({ kind: 'none' });
    open();
    expect(screen.getByRole('option', { name: 'Ленина 5' }).nextElementSibling).toBeNull();
    expect(screen.getByRole('option', { name: 'Склад' }).nextElementSibling).toBeNull();
  });

  it('стрелки с клавиатуры: вправо — разделы, влево — назад, Enter — выбор', () => {
    const onPick = show(whole(null));
    open();
    fireEvent.keyDown(search(), { key: 'ArrowRight' });
    expect(screen.getByRole('listbox', { name: 'Разделы стройки «Комарова 36»' })).toBeTruthy();
    fireEvent.keyDown(search(), { key: 'ArrowLeft' });
    expect(screen.getByRole('listbox', { name: 'Объект счёта: стройки и статьи' })).toBeTruthy();
    fireEvent.keyDown(search(), { key: 'ArrowDown' });
    fireEvent.keyDown(search(), { key: 'Enter' });
    expect(onPick).toHaveBeenCalledWith(at('l'));
  });

  it('счёт на разделе: «→» открывает разделы ЕГО стройки, даже если она не первая в списке', () => {
    show(whole('s4'), { ...PLACES, sites: [...PLACES.sites!].reverse() });
    open();
    fireEvent.keyDown(search(), { key: 'ArrowRight' });
    expect(screen.getByRole('listbox', { name: 'Разделы стройки «Комарова 36»' })).toBeTruthy();
    expect(screen.getByRole('option', { name: '4 эт.' }).getAttribute('aria-selected')).toBe('true');
  });

  it('набор «4 эт» находит «Комарова 36 / 4 эт.»', () => {
    const onPick = show({ kind: 'none' });
    open();
    fireEvent.change(search(), { target: { value: '4 эт' } });
    fireEvent.click(screen.getByRole('option', { name: 'Комарова 36 / 4 эт.' }));
    expect(onPick).toHaveBeenCalledWith(at('k', 's4'));
  });

  it('смена стройки уходит без прежнего раздела', () => {
    const onPick = show(whole('s4'));
    open();
    fireEvent.click(screen.getByRole('option', { name: 'Ленина 5' }));
    expect(onPick).toHaveBeenCalledWith(at('l'));
  });

  it('справочник не пришёл — в поле «…», а не «раздел удалён»', () => {
    show(whole('s4'), { sites: undefined, articles: undefined });
    expect(field().textContent).toBe('…');
  });

  it('удалённый раздел подписан «раздел удалён» и в поле, и пунктом второго шага', () => {
    show(whole('старый'));
    expect(field().textContent).toBe('Комарова 36 / раздел удалён');
    open();
    fireEvent.keyDown(search(), { key: 'ArrowRight' });
    expect(within(screen.getByRole('listbox')).getAllByRole('option').map(o => o.textContent))
      .toEqual(['— вся стройка —', 'раздел удалён', '5 эт.', '4 эт.']);
  });

  it('несколько разделов — поле приглушено и называет это словами', () => {
    show({ kind: 'one', place: at('k'), section: 'many', sections: 2, complete: true });
    expect(field().textContent).toBe('Комарова 36 · несколько разделов');
  });
});
