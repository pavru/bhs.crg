import { useEffect, useMemo, useRef, useState } from 'react';
import { useFieldSuggestions, type FieldSuggestion, type SuggestSource } from '../api/fieldSuggestions';
import { matchOptions, type SuggestItem } from './suggestMatch';

/**
 * Откуда список подсказки берёт пункты (UI-55, решение «Архитектора» по D3 change
 * `costs-filter-place-hints`): адрес подсказок по ранее введённому или готовый перечень. Остальное —
 * слой, клавиатура, доступность — одно на оба и живёт в `SuggestBox`: два комбобокса с одним поведением
 * расходились бы при каждой правке UI-1…UI-8.
 */
export interface SuggestFeed {
  /** Что показать и о каком набранном: подсветка считается по `q`. `null` — показывать нечего. */
  view: { q: string; items: SuggestItem[] } | null;
  /** Имя списка для читалки. */
  listLabel: string;
  /** Человек набрал `next` (поле уже получило `onChange`). */
  typed: (next: string) => void;
  /** Открыть сразу, без задержки: ↓ в форме или Alt+↓. */
  openNow: () => void;
  close: () => void;
}

/** Задержка после последней клавиши: три быстрые клавиши — один запрос (UI-5). */
const SUGGEST_DEBOUNCE_MS = 200;

/** Подпись чужих пунктов адреса — над первым из них, когда в ответе есть и свои. */
export const OTHER_SITES_GROUP = 'С других строек';

/** Показанный ответ адреса и о чём он: подсветка считается по его `q`, а не по набранному сейчас. */
interface Shown { q: string; items: FieldSuggestion[]; of: string }

/**
 * Пункты ответа адреса. Признак `here` становится группой «С других строек» — только когда в ответе есть
 * обе: у записи без стройки все «чужие», и подпись там врала бы.
 */
export function addressItems(items: readonly FieldSuggestion[]): SuggestItem[] {
  const split = items.some(i => i.here) && items.some(i => !i.here) ? items.findIndex(i => !i.here) : -1;
  return items.map((item, i) => (split >= 0 && i >= split ? { value: item.value, group: OTHER_SITES_GROUP } : { value: item.value }));
}

/** Подсказка по ранее введённому — адрес `/field-suggestions` (эпик #1527, UI-1…UI-7). */
export function useAddressFeed(source: SuggestSource, value: string, label: string): SuggestFeed {
  /** Человек хочет список: набрал или позвал стрелкой. Виден он, только когда есть что показать. */
  const [open, setOpen] = useState(false);
  /** Отстоявшееся набранное этого открытия — о нём спрашиваем адрес; `null` — ещё не отстоялось. */
  const [asked, setAsked] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const of = [source.type, source.field, source.scope ?? '', source.scopeId ?? ''].join('|');
  // Клетку отдали другому полю — открытие прежнего кончилось: его набранное о новом поле не спрашивают.
  const [openOf, setOpenOf] = useState(of);
  if (openOf !== of) {
    setOpenOf(of);
    setOpen(false);
    setAsked(null);
  }

  const query = useFieldSuggestions(open ? source : null, open ? asked : null);

  // Пока едет ответ на новое набранное, держится прошлый ответ ЭТОГО ЖЕ открытия и этого же поля —
  // иначе список мигал бы на каждую клавишу. Закрытие, отказ и смена поля его сбрасывают: прошлое
  // открытие и чужое поле — уже другие значения.
  const fresh: Shown | null = open && asked !== null && query.data ? { q: asked, items: query.data, of } : null;
  const [held, setHeld] = useState<Shown | null>(null);
  if (fresh && (held?.items !== fresh.items || held.q !== fresh.q || held.of !== of)) setHeld(fresh);
  if (!fresh && held && (!open || query.isError)) setHeld(null);
  const shown = !open || query.isError ? null : fresh ?? (held?.of === of ? held : null);
  // Ссылка на пункты новая на каждой отрисовке — выбор стрелкой `SuggestBox` сбрасывает по содержимому.
  const items = shown ? addressItems(shown.items) : [];

  useEffect(() => {
    const pending = timer;
    return () => clearTimeout(pending.current);
  }, []);

  function close() {
    clearTimeout(timer.current);
    setOpen(false);
    setAsked(null);
  }

  return {
    view: shown && { q: shown.q, items },
    listLabel: `Введённое ранее: ${label}`,
    close,
    openNow() {
      clearTimeout(timer.current);
      setAsked(value.trim());
      setOpen(true);
    },
    typed(next) {
      clearTimeout(timer.current);
      if (next.trim() === '') { close(); return; }
      setOpen(true);
      timer.current = setTimeout(() => setAsked(next.trim()), SUGGEST_DEBOUNCE_MS);
    },
  };
}

/**
 * Подсказка из готового перечня (UI-55): ни запроса, ни задержки — ждать нечего. Совпадение и предел
 * списка — `matchOptions`.
 */
export function useOptionsFeed(options: readonly SuggestItem[], value: string, label: string): SuggestFeed {
  const [open, setOpen] = useState(false);
  const items = useMemo(() => (open ? matchOptions(options, value) : []), [open, options, value]);
  return {
    view: open ? { q: value.trim(), items } : null,
    listLabel: `Подсказки: ${label}`,
    close: () => setOpen(false),
    openNow: () => setOpen(true),
    typed: next => setOpen(next.trim() !== ''),
  };
}
