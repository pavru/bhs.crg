// @vitest-environment jsdom
import { useState, type ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { PlaceSelect } from './PlaceSelect';
import { NO_PLACE, type Place, type Places } from './places';
import { LOST } from './lostReferences';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { SiteRevealScope } from '@/shared/siteFilter/SiteOptions';

/**
 * Выбор стройки под фильтром «Избранные стройки» (issue #1219) — на живом `Select` (Radix, #1353):
 * пункт-выход не меняет значения и оставляет список открытым, неизбранное стоящее не теряется,
 * удалённое — по-прежнему потеря.
 */

// Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const site = (id: string, name: string) => ({ id, name, sections: [] });
const PLACES: Places = {
  sites: [site('a', 'ЖК «Север»'), site('b', 'Школа №7'), site('c', 'ТЦ «Меридиан»'), site('d', 'Склад-2')],
  articles: [],
};

const filter: SiteFilterValue = {
  status: 'ready', favorites: new Set(['a', 'b']), on: true, active: true,
  sites: PLACES.sites!, visible: () => true, setOn: () => {}, setFavorite: async () => {}, setFavorites: async () => {},
};

function Under({ children }: { children: ReactNode }) {
  return <SiteFilterContext.Provider value={filter}>{children}</SiteFilterContext.Provider>;
}

/** Поле с настоящим состоянием формы: значение меняет только `onChange`. */
function Field({ initial, label, onChange }: { initial: Place; label: string; onChange?: (p: Place) => void }) {
  const [value, setValue] = useState(initial);
  return <PlaceSelect value={value} places={PLACES} label={label}
    onChange={p => { onChange?.(p); setValue(p); }} />;
}

const at = (construction: string): Place => ({ ...NO_PLACE, construction });

// Поле берём ДО открытия: пока список открыт, Radix прячет остальное от дерева доступности.
const field = (name: string) => screen.getByRole('combobox', { name });
const open = (trigger: HTMLElement) => fireEvent.keyDown(trigger, { key: 'Enter' });
const close = () => fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Escape' });
/** Подписи групп открытого списка и их пункты — то, что видит человек. */
const groups = () => screen.getAllByRole('group').map(g => [
  document.getElementById(g.getAttribute('aria-labelledby')!)?.textContent,
  within(g).getAllByRole('option').map(o => o.textContent),
]);

describe('PlaceSelect под фильтром избранного (#1219)', () => {
  it('пункт-выход не меняет значение и раскрывает список, не закрывая его', () => {
    const onChange = vi.fn();
    render(<Under><Field initial={at('a')} label="Объект" onChange={onChange} /></Under>);
    const trigger = field('Объект');
    open(trigger);

    fireEvent.click(screen.getByRole('option', { name: 'Показать все стройки (ещё 2)…' }));

    expect(onChange).not.toHaveBeenCalled();
    expect(trigger.textContent).toContain('ЖК «Север»');
    expect(screen.getByRole('listbox')).toBeTruthy();
    expect(groups()).toEqual([
      ['Избранные стройки', ['ЖК «Север»', 'Школа №7']],
      ['Остальные стройки', ['ТЦ «Меридиан»', 'Склад-2']],
    ]);
    expect(screen.queryByRole('option', { name: /Показать все стройки/ })).toBeNull();
  });

  it('раскрытие действует на форму: соседний список раскрыт тоже', () => {
    render(<Under><SiteRevealScope>
      <Field initial={at('a')} label="Первый" />
      <Field initial={NO_PLACE} label="Второй" />
    </SiteRevealScope></Under>);
    const second = field('Второй');
    open(field('Первый'));
    fireEvent.click(screen.getByRole('option', { name: /Показать все стройки/ }));
    close();
    open(second);
    expect(groups().map(g => g[0])).toEqual(['Избранные стройки', 'Остальные стройки']);
  });

  it('стоящая неизбранная стройка — группой «Не в избранном», обычным именем, и остаётся значением', () => {
    render(<Under><Field initial={at('c')} label="Объект" /></Under>);
    const trigger = field('Объект');
    // Значение поля — эта стройка: форма сохранит её, а не «— выберите —».
    expect(trigger.textContent).toContain('ТЦ «Меридиан»');
    open(trigger);
    expect(groups()).toEqual([
      ['Избранные стройки', ['ЖК «Север»', 'Школа №7']],
      ['Не в избранном', ['ТЦ «Меридиан»']],
    ]);
    expect(screen.queryByRole('option', { name: LOST.construction })).toBeNull();
    expect(screen.getByRole('option', { name: 'Показать все стройки (ещё 1)…' })).toBeTruthy();
  });

  it('удалённая стройка по-прежнему потеря, а не «не в избранном»', () => {
    render(<Under><Field initial={at('gone')} label="Объект" /></Under>);
    const trigger = field('Объект');
    expect(trigger.textContent).toContain(LOST.construction);
    open(trigger);
    expect(screen.getByRole('option', { name: LOST.construction })).toBeTruthy();
    expect(groups().map(g => g[0])).toEqual(['Избранные стройки']);
  });
});
