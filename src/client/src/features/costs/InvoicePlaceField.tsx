import * as Dialog from '@radix-ui/react-dialog';
import * as Popover from '@radix-ui/react-popover';
import { Icon } from '@/shared/ui/Icon';
import { ChevronLeft, ChevronRight, KeyboardArrowDown } from '@/shared/ui/icons';
import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactElement } from 'react';
import { Tooltip } from '@/shared/ui/Tooltip';
import { useHistoryLayer } from '@/shared/ui/historyLayers';
import { WIDE, useMedia } from '@/shared/hooks/useMedia';
import { useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { ruCount } from '@/shared/utils/pluralize';
import { headerPlace, type HeaderObject } from './matrix';
import { fieldText, firstStep, searchStep, sectionStep, type PlaceList, type PlaceRow } from './placeChoices';
import { LOADING_PLACE, UNREAD_PLACE, samePlace, type Place, type Places } from './places';

/**
 * Поле «стройка / раздел» в шапке счёта (вариант Б, COST-12.1, решение владельца 25.10.2026): одно поле,
 * раздел — вторым шагом в том же слое. Щелчок по стройке — вся стройка; «›» (или стрелка вправо) — её
 * разделы, «‹» (стрелка влево) — назад. Набор ищет по обеим частям подписи.
 *
 * <p>Не `Select`: у него нет ни набора с поиском, ни второго шага (D4). Пункты — те же, что у
 * `PlaceSelect` (`placeChoices`), роли — те же, что у списка `Select` (`combobox`, `listbox`, группы с
 * подписью), поэтому живые прогоны выбирают объект прежними помощниками.</p>
 *
 * <p>На телефоне — нижний лист с теми же шагами; системная «Назад» закрывает его, а не страницу.</p>
 */
export function InvoicePlaceField({ current, places, label, disabled, onPick }: {
  current: HeaderObject;
  places: Places;
  label: string;
  disabled?: boolean;
  onPick: (place: Place) => void;
}) {
  const wide = useMedia(WIDE);
  const [open, setOpen] = useState(false);
  useHistoryLayer(open && !wide, () => { setOpen(false); return true; });
  const shown = fieldText(current, places);
  const value = headerPlace(current) ?? (current.kind === 'one' ? current.place : null);

  const pick = (place: Place) => { setOpen(false); onPick(place); };

  // Усечена ли подпись: подсказка повторяет её только тогда, иначе это тот же текст ещё раз.
  const textRef = useRef<HTMLSpanElement>(null);
  const [cut, setCut] = useState(false);
  useLayoutEffect(() => {
    const el = textRef.current;
    if (!el) return;
    const measure = () => setCut(el.scrollWidth > el.clientWidth);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [shown.text]);

  const trigger = (
    <button type="button" role="combobox" aria-label={label} aria-expanded={open} aria-haspopup="listbox"
      disabled={disabled} data-state={open ? 'open' : 'closed'}
      className="inline-flex items-center justify-between gap-1 min-w-56 max-w-full h-7 px-2 rounded-sm border border-outline bg-surface text-body-small text-on-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-primary data-[state=open]:ring-2 data-[state=open]:ring-primary disabled-outlined disabled:pointer-events-none">
      <span ref={textRef} className={`min-w-0 flex-1 truncate text-left ${shown.muted ? 'text-on-surface-variant' : ''}`}>{shown.text}</span>
      <Icon icon={KeyboardArrowDown} size={18} aria-hidden className="text-on-surface-variant shrink-0" />
    </button>
  );
  // Полная подпись — подсказкой, когда в поле она усечена («Комарова 36 / 4 эт.» шире поля на узкой шапке).
  const anchored = (inner: ReactElement) => <Tooltip content={cut ? shown.text : undefined}>{inner}</Tooltip>;
  const body = open && <Choices places={places} value={value} label={label} onPick={pick} />;

  if (wide) return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      {anchored(<Popover.Trigger asChild>{trigger}</Popover.Trigger>)}
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={4} collisionPadding={8}
          className="z-50 w-80 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) flex flex-col overflow-hidden rounded-sm border border-outline-variant bg-surface p-1 shadow-elevation-2">
          {body}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      {anchored(<Dialog.Trigger asChild>{trigger}</Dialog.Trigger>)}
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-scrim/32" />
        <Dialog.Content aria-describedby={undefined} data-modal-state="free"
          className="fixed inset-x-0 bottom-0 z-50 max-h-[80dvh] flex flex-col overflow-hidden rounded-t-lg border-t border-outline-variant bg-surface p-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] focus:outline-none shadow-elevation-2">
          <Dialog.Title className="px-2 pt-1 pb-2 text-body-medium font-semibold text-on-surface">{label}</Dialog.Title>
          {body}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

type Option = { id: string; row: PlaceRow } | { id: string; reveal: true };

/** Набор, шаг и пункты слоя. Фокус живёт в поле набора, пункт выделен `aria-activedescendant`. */
function Choices({ places, value, label, onPick }: {
  places: Places; value: Place | null; label: string; onPick: (place: Place) => void;
}) {
  const ids = useId();
  const input = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState('');
  const [siteId, setSiteId] = useState<string | null>(null);
  const choices = useSiteChoices(places.sites, value?.article ? null : value?.construction);
  const site = siteId === null ? undefined : places.sites?.find(s => s.id === siteId);

  const list: PlaceList = query.trim() ? searchStep(query, places, value)
    : site ? sectionStep(site, places, value)
      : firstStep(places, choices, value);
  const options: Option[] = [
    ...(list.lost ? [{ id: `${ids}-lost`, row: list.lost }] : []),
    ...list.groups.flatMap((g, gi) => g.rows.map((row, ri) => ({ id: `${ids}-${gi}-${ri}`, row }))),
    ...(list.reveal ? [{ id: `${ids}-reveal`, reveal: true as const }] : []),
  ];

  // Выделенный пункт — по ключу, а не по номеру: список меняется под набором, и номер указал бы на другой.
  // Без выбора с клавиатуры — стоящее место, а на шаге строек у счёта на разделе — его стройка: иначе
  // «→» открыл бы разделы первой стройки списка, а не той, на которой счёт.
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const found = options.findIndex(o => key(o) === activeKey);
  const exact = options.findIndex(o => 'row' in o && value !== null && samePlace(o.row.place, value));
  const ofSite = (o: Option) => 'row' in o && !!value?.construction && o.row.place.construction === value.construction;
  const activeIndex = found >= 0 ? found : exact >= 0 ? exact : Math.max(0, options.findIndex(ofSite));
  const active = options[activeIndex];

  // Шаг меняют и кнопки «›» / «‹», а клавиши слушает поле набора: фокус обязан вернуться в него.
  const step = (to: string | null) => { setSiteId(to); setQuery(''); setActiveKey(null); input.current?.focus(); };
  const keepFocus = (e: MouseEvent) => e.preventDefault();

  // Выделенный стрелками пункт — на виду: список прокручивается, и подсветка ушла бы за край.
  const activeId = active?.id;
  useEffect(() => {
    if (activeId) document.getElementById(activeId)?.scrollIntoView?.({ block: 'nearest' });
  }, [activeId]);
  const activate = (option: Option | undefined) => {
    if (!option) return;
    if ('reveal' in option) choices.reveal(); else onPick(option.row.place);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const atEnd = e.currentTarget.selectionStart === query.length;
    // Home / End — по пунктам, пока набора нет; с набором они водят курсор по тексту, как в любом поле.
    if ((e.key === 'Home' || e.key === 'End') && query === '' && options.length > 0) {
      e.preventDefault();
      setActiveKey(key(options[e.key === 'Home' ? 0 : options.length - 1]));
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const next = (activeIndex + (e.key === 'ArrowDown' ? 1 : options.length - 1)) % Math.max(options.length, 1);
      setActiveKey(options[next] ? key(options[next]) : null);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      activate(active);
    } else if (e.key === 'ArrowRight' && atEnd && active && 'row' in active && active.row.sections > 0) {
      e.preventDefault();
      step(active.row.place.construction);
    } else if (e.key === 'ArrowLeft' && site && query === '') {
      e.preventDefault();
      step(null);
    }
  };

  const row = (option: Option, text: string, row?: PlaceRow) => (
    <div key={option.id} className="flex items-center gap-1">
      <div id={option.id} role="option" aria-selected={'row' in option && value !== null && samePlace(option.row.place, value)}
        aria-description={row && row.sections > 0 ? `${ruCount(row.sections, 'раздел', 'раздела', 'разделов')}, стрелка вправо — разделы` : undefined}
        onMouseMove={() => setActiveKey(key(option))}
        onClick={() => activate(option)}
        className={`min-w-0 flex-1 px-3 py-1.5 rounded-xs text-body-medium cursor-pointer select-none break-words ${option === active ? 'bg-primary-container text-on-primary-container' : 'text-on-surface'} aria-selected:font-medium`}>
        {text}
      </div>
      {row && row.sections > 0 && (
        <button type="button" tabIndex={-1} aria-hidden onMouseDown={keepFocus} onClick={() => step(row.place.construction)}
          className="shrink-0 inline-flex items-center gap-0.5 h-7 px-1.5 rounded-xs text-body-small text-on-surface-variant hover:text-on-surface hover:bg-primary-container">
          {row.sections}<Icon icon={ChevronRight} size={18} />
        </button>
      )}
    </div>
  );

  let index = list.lost ? 1 : 0;
  return (
    <>
      <input ref={input} type="text" value={query} autoFocus aria-label={site ? `Найти раздел стройки «${site.name}»` : 'Найти стройку или раздел'}
        role="combobox" aria-expanded aria-controls={`${ids}-list`} aria-activedescendant={active?.id}
        placeholder={site ? 'Найти раздел' : 'Найти стройку или раздел'}
        onChange={e => { setQuery(e.target.value); setActiveKey(null); }} onKeyDown={onKeyDown}
        className="mx-1 mb-1 h-8 px-2 rounded-xs border border-outline bg-surface text-body-medium text-on-surface placeholder:text-on-surface-variant focus:outline-none focus-visible:ring-2 focus-visible:ring-primary" />
      {site && !query.trim() && (
        <button type="button" onMouseDown={keepFocus} onClick={() => step(null)}
          className="mx-1 inline-flex items-center gap-1 h-7 px-2 rounded-xs text-body-small text-on-surface-variant hover:bg-primary-container"
          aria-label={`Назад к стройкам из разделов «${site.name}»`}>
          <Icon icon={ChevronLeft} size={18} aria-hidden />{site.name}
        </button>
      )}
      <div id={`${ids}-list`} role="listbox" className="min-h-0 overflow-y-auto"
        aria-label={site ? `Разделы стройки «${site.name}»` : `${label}: стройки и статьи`}>
        {list.lost && row(options[0], list.lost.label)}
        {list.groups.map((g, gi) => (
          <div key={g.label} role="group" aria-labelledby={`${ids}-g${gi}`}>
            <div id={`${ids}-g${gi}`} className="px-3 pt-2 pb-1 text-title-small text-on-surface-variant">{g.label}</div>
            {g.rows.map(r => { const option = options[index++]; return row(option, r.label, r); })}
          </div>
        ))}
        {list.reveal && row(options[options.length - 1], list.reveal)}
        {options.length === 0 && (
          // Справочник не пришёл — «ещё не знаем», а не «пусто»: «Ничего не найдено» соврало бы, что строек нет.
          <div className="px-3 py-2 text-body-medium text-on-surface-variant">
            {places.sites !== undefined ? 'Ничего не найдено' : places.unread ? UNREAD_PLACE : LOADING_PLACE}
          </div>
        )}
      </div>
    </>
  );
}

const key = (option: Option) => 'reveal' in option ? 'reveal' : option.row.key;
