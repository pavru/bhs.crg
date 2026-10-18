// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, renderHook, screen, within } from '@testing-library/react';
import { SiteFilterContext, type SiteFilterValue } from './siteFilterContext';
import { filterState, isVisible } from './rule';
import { SitesFilter } from './SitesFilter';
import { FAVORITE_SITES_GROUP, useFavoriteSitesGroup } from './favoriteSitesGroup';
import type { ReactNode } from 'react';

/**
 * Отбор «Стройки» над списком (issue #1432) — вместо сегмента «Избранные / Все» в шапке. Подпись
 * называет состояние словами, поповер переключает и правит избранное.
 */

beforeAll(() => {
  // Слой поповера меряет якорь — в jsdom этого нет.
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const SITES = [{ id: 'a', name: 'ЖК «Север»' }, { id: 'b', name: 'Школа №7' }, { id: 'c', name: 'Склад' }];

function filterOf(favorites: string[], { status = 'ready', on = true }: {
  status?: SiteFilterValue['status']; on?: boolean;
} = {}): SiteFilterValue {
  const state = filterState(favorites, on, new Set(SITES.map(s => s.id)));
  return {
    status, favorites: state.favorites, on, active: status === 'ready' && state.active, sites: SITES,
    visible: sites => isVisible(sites, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
  };
}

function shown(value: SiteFilterValue, props: { hidden?: number; partial?: boolean } = {}) {
  render(<SiteFilterContext.Provider value={value}><SitesFilter {...props} /></SiteFilterContext.Provider>);
}

describe('отбор «Стройки»', () => {
  it('справочник не открыт — отбора нет', () => {
    shown(filterOf([], { status: 'unavailable' }));
    expect(screen.queryByTestId('sites-filter')).toBeNull();
  });

  it('пока не известно — недоступный чип, а не «все» и не «избранные»', () => {
    shown(filterOf(['a'], { status: 'loading' }));
    const chip = screen.getByTestId('sites-filter');
    expect(chip.textContent).toBe('Стройки…');
    expect((chip as HTMLButtonElement).disabled).toBe(true);
  });

  it('не загрузилось — так и сказано: показаны все', () => {
    shown(filterOf(['a'], { status: 'error' }));
    expect(screen.getByTestId('sites-filter-error').textContent).toContain('избранные не загрузились');
  });

  it('включён — «Избранные стройки» и сколько скрыто; нажатым чип не бывает', () => {
    shown(filterOf(['a', 'b']), { hidden: 12 });
    const chip = screen.getByTestId('sites-filter');
    expect(chip.textContent).toContain('Избранные стройки');
    expect(chip.textContent).toContain('скрыто 12');
    expect(chip.getAttribute('aria-label')).toBe('Стройки: избранные, 2 из 3, скрыто 12');
    expect(chip.getAttribute('aria-pressed')).toBeNull();
    expect(chip.getAttribute('aria-haspopup')).toBe('dialog');
  });

  it('под поиском — «часть скрыта», без числа', () => {
    shown(filterOf(['a']), { hidden: 4, partial: true });
    const text = screen.getByTestId('sites-filter').textContent;
    expect(text).toContain('часть скрыта');
    expect(text).not.toContain('4');
  });

  it('выключен — «Все стройки»; выбор в поповере включает избранные', () => {
    const value = filterOf(['a'], { on: false });
    shown(value, { hidden: 0 });
    const chip = screen.getByTestId('sites-filter');
    expect(chip.textContent).toBe('Все стройки');
    fireEvent.click(chip);
    const popover = screen.getByTestId('sites-filter-popover');
    const choices = within(popover).getAllByRole('radio');
    expect(choices.map(c => [c.textContent, c.getAttribute('aria-checked')]))
      .toEqual([['Избранные стройки1 из 3', 'false'], ['Все стройки3', 'true']]);
    fireEvent.click(choices[0]);
    expect(value.setOn).toHaveBeenCalledWith(true);
  });

  it('уже выбранное положение повторно не пишется', () => {
    const value = filterOf(['a']);
    shown(value);
    fireEvent.click(screen.getByTestId('sites-filter'));
    fireEvent.click(within(screen.getByTestId('sites-filter-popover')).getByRole('radio', { name: /Избранные/ }));
    expect(value.setOn).not.toHaveBeenCalled();
  });

  it('избранного нет — «Все стройки», в поповере только выбор строек', () => {
    shown(filterOf([]));
    const chip = screen.getByTestId('sites-filter');
    expect(chip.textContent).toBe('Все стройки');
    fireEvent.click(chip);
    const popover = screen.getByTestId('sites-filter-popover');
    expect(within(popover).queryByRole('radiogroup')).toBeNull();
    expect(within(popover).getByTestId('favorites-picker')).toBeTruthy();
  });
});

describe('избранные стройки в подпанели «Стройки»', () => {
  const wrap = (value: SiteFilterValue) =>
    ({ children }: { children: ReactNode }) => <SiteFilterContext.Provider value={value}>{children}</SiteFilterContext.Provider>;

  it('ссылки на карточки избранных — по названию справочника', () => {
    const { result } = renderHook(() => useFavoriteSitesGroup(), { wrapper: wrap(filterOf(['c', 'a'])) });
    expect(result.current?.label).toBe(FAVORITE_SITES_GROUP);
    expect(result.current?.items.map(i => [i.label, i.to]))
      .toEqual([['ЖК «Север»', '/document-sets/a'], ['Склад', '/document-sets/c']]);
  });

  it('и при выключенном отборе: это переход, а не фильтр', () => {
    const { result } = renderHook(() => useFavoriteSitesGroup(), { wrapper: wrap(filterOf(['b'], { on: false })) });
    expect(result.current?.items.map(i => i.label)).toEqual(['Школа №7']);
  });

  it('избранного нет или оно не известно — группы нет', () => {
    expect(renderHook(() => useFavoriteSitesGroup(), { wrapper: wrap(filterOf([])) }).result.current).toBeNull();
    expect(renderHook(() => useFavoriteSitesGroup(), { wrapper: wrap(filterOf(['a'], { status: 'loading' })) })
      .result.current).toBeNull();
  });
});
