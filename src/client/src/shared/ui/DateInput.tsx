import { useMemo, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Icon } from './Icon';
import { CalendarMonth, Close } from './icons';
import type { DatePrecision } from '@/shared/api/types';
import { IconButton } from './Button';
import { ICON_BUTTON_XS } from './buttonClasses';
import { Calendar } from './Calendar';
import { readSegments } from './dateSegments';
import { dateSegmentModel } from './dateSegmentModel';
import { SegmentProblemMark } from './SegmentProblemMark';
import { useSegmentField } from './useSegmentField';
import { nextTabStop, tabStops } from './tabStops';
import { dateMask } from '@/shared/format/dateMask';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';

interface DateInputProps {
  /**
   * Имя поля для чтения с экрана — у группы сегментов (issue #1463): «Дата платежа». Обязательное:
   * без него скринридер слышал три безымянных «день, месяц, год», а живые прогоны искали поле по
   * подсказке «ДД», которой у en-US нет. Видимую подпись рисует вызывающий, это — её же текст.
   */
  label: string;
  /** Stored value: ISO YYYY-MM-DD or '' (always full ISO, независимо от точности) */
  value: string;
  /**
   * Emits full ISO YYYY-MM-DD when the visible segments make a real date, '' when explicitly cleared,
   * nothing while partially typed or when the date does not exist (31.02, issue #1460). Скрытые
   * точностью части допоняются '01' (issue #60): 'year' → YYYY-01-01, 'month' → YYYY-MM-01.
   */
  onChange: (iso: string) => void;
  /**
   * Ошибка набранного — текстом для подписи под полем, null — ошибки нет (issue #1460): «Такой даты
   * нет», «Дата не дописана» (после ухода из поля), значение не дата. Задан — ошибку рисует
   * вызывающий (`DateField`); не задан — поле само красит цифры и ставит значок с подсказкой.
   */
  onProblem?: (message: string | null) => void;
  /**
   * Набранное — не дата: недописанное или несуществующее, и наружу оно не ушло (ревью PR #1556). Сразу,
   * а не после ухода, как `onProblem`: форма, пишущая по кнопке, читает его в щелчке, а ошибка ухода
   * встаёт таймером позже щелчка — и в Safari, где кнопка фокуса не берёт, ухода к ней нет вовсе.
   */
  onIncomplete?: (message: string | null) => void;
  /**
   * Точность ввода: 'day' (по умолч.), 'month', 'year'. Маска — по языку пользователя (issue #1461):
   * ДД.ММ.ГГГГ у ru-RU, MM/DD/YYYY у en-US; точность убирает лишние сегменты (ММ.ГГГГ, MM/YYYY).
   */
  precision?: DatePrecision;
  /**
   * Классы контейнера: ширина и место в раскладке. Рамку поля обвязки рисует `size`, а не экран —
   * своей рамки здесь не заводят (issue #1464, храповик `date-frame`).
   */
  className?: string;
  /**
   * Слой обвязки (issue #1464) — поле со своей рамкой, как `Select`: 'md' — h-9, крестик по наведению
   * и фокусу, как у поля формы; 'sm' — h-7, без крестика (в отборе у чипа свой ×). Не задан — рамку
   * рисует оболочка (`DateField`) или ячейка (`compact`).
   */
  size?: 'md' | 'sm';
  /** Значение не прошло проверку формы — рамка обвязки красная, сегменты помечены для скринридера. */
  invalid?: boolean;
  /** id подписи под рамкой (`DateField`) — сегменты описываются ею: см. `useSegmentField`. */
  hintId?: string;
  /**
   * Границы допустимых дат — полный ISO (issue #1464). Календарь не даёт выбрать за ними; набранное
   * с клавиатуры поле принимает, ошибку называет форма — откатывать ввод поле не вправе (#1460).
   */
  min?: string;
  max?: string;
  /** Период, частью которого служит поле (`DateRange`): календарь рисует ленту от «с» до «по». */
  range?: [string, string];
  /**
   * Человек закончил с полем: ушёл из него, изменив значение, нажал Enter в сегменте или выбрал дату
   * в календаре. Значок календаря, крестик и сам календарь — ещё поле. Отдаёт значение на этот момент — `DateRange`
   * поправляет по нему другую границу, а не на каждой цифре и стрелке.
   */
  onCommit?: (value: string) => void;
  disabled?: boolean;
  /**
   * Значение показано, но не правится (issue #958) — в отличие от `disabled`, поле остаётся в
   * обходе клавиатурой, а сегменты можно выделить и скопировать. Так рисуются поля, значение
   * которых кладёт не человек: привязка к источнику данных и замок модуля.
   */
  readOnly?: boolean;
  /**
   * Поле обязательное — для чтения с экрана. Звёздочка у подписи рисуется псевдоэлементом и в имя поля
   * не входит (issue #1291), поэтому обязательность обязан назвать сам контрол.
   */
  required?: boolean;
  /** Показывать иконку-триггер календаря (docked date picker, issue #338). По умолчанию — да. */
  calendar?: boolean;
  /**
   * Компактный режим (ячейка таблицы): иконка появляется по hover/focus, не занимает место постоянно.
   * Крестика «Очистить дату» у ячейки нет (issue #1463, вариант В): второй кнопке в колонке места нет,
   * очищают её подвалом календаря или Ctrl+A → Delete.
   */
  compact?: boolean;
}

export function DateInput({ label, value, onChange, onProblem, onIncomplete, precision = 'day', className = '', disabled = false, readOnly = false, required = false, calendar = true, compact = false,
  size, invalid = false, hintId, min, max, range, onCommit }: DateInputProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  /** Календарь закрыт Tab'ом с края — фокус уже перенесён, возвращать его на значок не нужно. */
  const tabbedOut = useRef(false);

  // Порядок сегментов, разделитель и обозначения — языка пользователя (issue #1461). Хранимое значение
  // от языка не зависит. Полный порядок нужен вставке: дату целиком вставляют и в поле месяца.
  const [storedLocale] = useLocale();
  const model = useMemo(() => {
    const locale = resolveLocale(storedLocale);
    return dateSegmentModel(dateMask(locale, precision), dateMask(locale, 'day').order, precision);
  }, [storedLocale, precision]);

  // Иконка календаря у нередактируемого поля — приглашение к действию, которого нет: открыв её,
  // человек выберет дату, и ничего не произойдёт. Убираем её вместе с жестом Alt+↓ (issue #958).
  const showCalendar = calendar && !readOnly;

  // Движок сегментов (issue #1468): набор, стрелки, вставка, Esc, Enter, фиксация при уходе. Здесь —
  // только своё у даты: Alt+↓ из любого сегмента открывает календарь (штатный MD3-жест).
  const {
    foreign, problem, problemId, markInvalid, describedBy, clearable, groupProps, segments: segmentInputs,
    selectedAnnouncement, fromValue, contentRef, fieldFocus, pick, clear, startOver,
  } = useSegmentField(model, {
    label, value, onChange, onProblem, onIncomplete, onCommit, disabled, readOnly, required, invalid, hintId,
    onKey: e => {
      if (!(showCalendar && !disabled && e.altKey && e.key === 'ArrowDown')) return false;
      e.preventDefault(); setPickerOpen(true);
      return true;
    },
  });

  const problemMark = <SegmentProblemMark id={problemId} problem={problem} showIcon={!onProblem} />;

  // Рамка обвязки (issue #1464) — как у `Select`: кольцо, пока фокус внутри или открыт календарь.
  const frame = !size ? '' : [
    FRAME[size], 'rounded-sm border transition-colors ease-standard',
    // Отключённое — по MD3 ([UI-64]): текст on-surface 38 %, рамка on-surface 12 %, а не прозрачность.
    readOnly ? 'bg-surface-container text-on-surface-variant border-outline-variant'
      : disabled ? 'bg-surface text-on-surface/38 border-on-surface/12'
      : `bg-surface text-on-surface ${markInvalid ? 'border-error' : 'border-outline'}`,
    readOnly || disabled ? '' : `focus-within:ring-2 ${markInvalid ? 'focus-within:ring-error' : 'focus-within:ring-primary'}`,
    pickerOpen ? `ring-2 ${markInvalid ? 'ring-error' : 'ring-primary'}` : '',
  ].join(' ');

  // Крестик поля формы — один на обе ветки: у «не даты» и у сегментов (макет #1463, вариант В). Кнопка
  // внутри поля — место XS ([UI-68]): 32, как календарь рядом, при касании 40.
  const clearButton = !compact && size !== 'sm' && clearable && (
    <IconButton label="Очистить дату" onClick={clear} className={`-my-1.5 ${ICON_BUTTON_XS} ${CLEAR_REVEAL}`} icon={Close} />
  );

  const segments = foreign ? (
    <div role="group" aria-label={label} className={`flex items-center group min-w-0 ${frame} ${className}`}>
      {readOnly || disabled
        ? <span className="truncate text-on-surface-variant">{value}</span>
        // Ввод начинается щелчком, Enter или пробелом, а не фокусом: проход Tab'ом по форме не должен
        // стирать показанное и уводить фокус в сегменты (ревью PR #1542).
        : <button type="button" onClick={startOver} aria-describedby={describedBy}
            aria-label={`${value} — не дата, набрать дату заново`}
            className="truncate text-left text-on-surface-variant rounded-xs focus:outline-none focus-visible:bg-primary-container">
            {value}
          </button>}
      {problemMark}
      {/* «Не дата» тоже стирается крестиком: календаря у неё нет, а выход иначе один — набрать заново. */}
      {clearButton && <span className="flex items-center shrink-0 ml-auto pl-0.5">{clearButton}</span>}
    </div>
  ) : (
    <div {...groupProps} className={`flex items-center group ${frame} ${className}`}>
      {segmentInputs}
      {problemMark}
      {selectedAnnouncement}
      {/* Хвост поля формы — у правого края, крестик слева от календаря (макет #1463, вариант В):
          появляясь, крестик не сдвигает ни календарь, ни цифры. */}
      <span className={`flex items-center shrink-0 ${compact ? '' : 'ml-auto pl-0.5'}`}>
        {clearButton}
        {showCalendar && (
          <Popover.Trigger asChild>
            <button ref={triggerRef} type="button" disabled={disabled} aria-label="Открыть календарь"
              className={`shrink-0 flex items-center justify-center rounded-full text-on-surface-variant state-layer focus-ring disabled-content ${
                size === 'sm' ? SM_TRIGGER : '-my-1.5 h-8 w-8'} ${
                compact ? `${COMPACT_TRIGGER} opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 data-[state=open]:opacity-100` : ''}`}>
              <Icon icon={CalendarMonth} size={18} />
            </button>
          </Popover.Trigger>
        )}
      </span>
    </div>
  );

  if (!showCalendar) return segments;

  // Календарю — только существующая дата: с «2026-02-31» он рисовал заголовок «undefined 2026».
  const shownDate = fromValue && readSegments(fromValue, 'day');
  const calendarValue = shownDate && shownDate.kind === 'date' ? shownDate.iso : '';

  /**
   * Tab с края календаря — дальше по форме (ревью PR #1526).
   *
   * <p>Календарь лежит в конце body, вне порядка Tab формы. Без этого Tab с его последней остановки
   * возвращался на первую кнопку календаря (проверено живым прогоном): по кругу внутри, и уйти к
   * следующему полю можно было только через Esc. Внутри календаря Tab ходит как обычно. С последней
   * остановки вперёд фокус встаёт на следующее поле после значка, с первой назад — на сам значок, и
   * календарь закрывается.</p>
   *
   * <p>Фокус переносится СРАЗУ, в обработчике клавиши. Отдать его Radix при закрытии нельзя: тот
   * возвращает фокус таймером, и до таймера фокус лежит на body — живой прогон в CI успевал увидеть
   * именно body.</p>
   */
  function onCalendarTab(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key !== 'Tab') return;
    const stops = tabStops(e.currentTarget);
    if (document.activeElement !== (e.shiftKey ? stops[0] : stops.at(-1))) return;
    e.preventDefault();
    const trigger = triggerRef.current;
    const target = (!e.shiftKey && trigger && nextTabStop(trigger)) || trigger;
    tabbedOut.current = true;
    target?.focus();
    setPickerOpen(false);
  }
  /**
   * «Очистить» календаря: фокус — в первый сегмент, набирать новую дату, а не на значок, как после
   * выбора дня. Возврат Radix гасится тем же признаком, что у ухода Tab'ом.
   */
  function clearFromCalendar() {
    tabbedOut.current = true;
    setPickerOpen(false);
    clear();
  }
  /** Фокус уже перенесён Tab'ом — Radix не возвращает его на значок. */
  function onCalendarClosed(e: Event) {
    if (!tabbedOut.current) return;
    tabbedOut.current = false;
    e.preventDefault();
  }

  // Календарь — слой над окном, как Select и меню: портал в body (issue #1521, docs/UI.md «Слои над
  // окном»). Внутри окна его резали бы `overflow-hidden` и `transform` окна, а разворот вверх не
  // спасал бы: место Radix считает по экрану. Раньше портировали ВНУТРЬ окна (#338) — это обходило
  // дубль слоёв Radix (свои копии focus-scope и dismissable-layer у popover), которого больше нет:
  // единственную копию сторожит `scripts/radix-single-copy.test.mjs`.
  //
  // `hideWhenDetached`: поле прокрутили из видимой части тела окна — календарь прячется. Внутри окна
  // его прятал край окна; в body он висел бы над шапкой, указывая на невидимое поле.
  return (
    <Popover.Root open={pickerOpen} onOpenChange={setPickerOpen}>
      <Popover.Anchor asChild>{segments}</Popover.Anchor>
      <Popover.Portal>
        <Popover.Content ref={contentRef} align="start" sideOffset={6} collisionPadding={8} hideWhenDetached
          onKeyDown={onCalendarTab} onCloseAutoFocus={onCalendarClosed} {...fieldFocus}
          onOpenAutoFocus={() => { tabbedOut.current = false; }}
          className="z-50 rounded-lg bg-surface border border-outline-variant focus:outline-none shadow-elevation-2">
          <Calendar value={calendarValue} precision={precision}
            onSelect={pick} onClose={() => setPickerOpen(false)}
            min={min} max={max} range={range}
            onClear={clearable ? clearFromCalendar : undefined} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Кнопка календаря в ячейке: нажимать — те же 32 px, а места в строке на 6 px меньше (ревью PR #1548):
 * зона нажатия заходит на правое поле ячейки (`px-1.5`), а не на цифры года слева. Ширину колонки под
 * поле — 140 px — держит `DEFAULT_COL_WIDTHS`.
 */
const COMPACT_TRIGGER = '-mr-1.5';
/**
 * Кнопка календаря в рамке sm (h-7): видимый круг 24 px, нажимать — 32 px (docs/UI.md, кнопка-иконка
 * не меньше 32 px; ревью PR #1553). Зона нажатия — псевдоэлемент на 4 px за круг: высоту рамки она
 * не меняет.
 */
const SM_TRIGGER = "relative h-6 w-6 before:absolute before:-inset-1 before:content-['']";
/** Высота и отступы рамки обвязки — те же, что у `Select` md / sm; справа место держит кнопка календаря. */
const FRAME = { md: 'h-9 pl-3 pr-0.5 text-body-medium', sm: 'h-7 pl-2 pr-0.5 text-body-small' } as const;
/**
 * Крестик поля формы — ускоритель (issue #1463, вариант В): виден при наведении на поле и при фокусе в
 * нём, а где наведения нет — всегда. Прозрачный он не ловит нажатий (`pointer-events-none`), иначе
 * касание у края поля стирало бы дату вместо открытия календаря (тот же урок у `TypePickerField`).
 * Фокус Tab'ом его показывает: остановка полноценная, зрячий с клавиатурой видит, куда пришёл.
 */
const CLEAR_REVEAL = 'opacity-0 pointer-events-none transition-opacity ease-standard '
  + 'group-hover:opacity-100 group-hover:pointer-events-auto '
  + 'group-focus-within:opacity-100 group-focus-within:pointer-events-auto '
  + '[@media(pointer:coarse)]:opacity-100 [@media(pointer:coarse)]:pointer-events-auto';
