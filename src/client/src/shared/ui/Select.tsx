import * as RS from '@radix-ui/react-select';
import { Check, ChevronDown } from 'lucide-react';
import { forwardRef, useId, useRef, useState, type ReactNode } from 'react';
import { OutlinedField } from './OutlinedField';

/**
 * MD3-Select (issue #110, фаза 2) поверх Radix Select — замена нативному `<select>`
 * (дефект «смесь нативных и кастомных контролов» из хендоффа). Radix даёт полный
 * APG-listbox с клавиатуры (стрелки/Home/End/typeahead/Esc) — закрывает #107 F5.
 * Тема light/dark через токены.
 *
 * Два слоя (issue #565, шаг 4 — #574), и выбирает между ними наличие `label`, как у
 * `TypePickerField`:
 *
 * - **слой формы** (`label` задан) — общая MD3-оболочка `OutlinedField` высотой `h-14`, подпись
 *   всегда в вырезе рамки. Свой `<label>` в вызывающем коде НЕ пишем: пока подпись жила снаружи,
 *   у семи площадок набралось четыре разные типографики (`text-xs/fg2`, `text-sm/fg1`,
 *   `text-sm/fg2`, `text-xs/fg3`) — при том, что соседние `TextField` в тех же формах уже носили
 *   подпись в вырезе. Связываем через `aria-labelledby`, а не `<label for>`: у кнопки-триггера
 *   щелчок по подписи открывал бы список;
 * - **слой обвязки** (`label` нет) — компактный триггер `h-9` со своей рамкой для шапок, фильтров
 *   и ячеек таблиц, там и живёт плотность.
 *
 * Метка в слое формы поднята ПОСТОЯННО (`raise="always"`): у кнопки-триггера нет состояния «пусто»
 * в смысле CSS — `:placeholder-shown` есть только у `input`, поэтому всплывание по peer-селекторам,
 * как в `TextField`, здесь неосуществимо. Фокус по той же причине приходит пропом.
 *
 * ВНИМАНИЕ: Radix запрещает пустую строку как value у Item. Для «пусто/все» используйте
 * placeholder и `value={undefined}` либо отдельный Item с непустым sentinel-значением.
 *
 * Список ВСЕГДА управляемый: «значения нет» уходит в Radix пустой строкой, а не `undefined`
 * (ревью PR #1319). С `undefined` Radix считает список неуправляемым и помнит выбор сам — и стоило
 * вызывающему коду сбросить своё значение («Отменить», очистка после «Добавить»), как список
 * продолжал показывать сброшенное, а выбрать тот же пункт заново было нельзя: значение «то же»,
 * событие не приходит. Пустую строку Radix читает как «значения нет» и показывает placeholder.
 */
export interface SelectProps {
  value: string | undefined;
  onValueChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  /** В слое обвязки — участие в нативной валидации формы (скрытый `<select required>` у Radix).
   *  В слое формы (задан `label`) — только звёздочка при подписи и `aria-required`, см. ниже. */
  required?: boolean;
  name?: string;
  /** Класс триггера (например ширина). Своя ширина (`w-…`) заменяет умолчание `w-full`, а не
   *  спорит с ним: какой из двух классов победит, решал бы порядок правил в CSS, а не в строке. */
  className?: string;
  /** Высота слоя обвязки: `md` — 36 px (шапки, фильтры), `sm` — 28 px и мелкий текст (строки
   *  плотных таблиц и списков, где рядом стоят поля `text-xs`). В слое формы не действует. */
  size?: 'md' | 'sm';
  /**
   * Пункт «значения нет» первым в списке: «— не выбрано —», «без итога», «наследуется». Его выбор
   * отдаёт пустую строку. Нужен потому, что Radix не принимает пустую строку значением пункта, а
   * сбросить выбор placeholder не умеет — он лишь показывает, что выбора нет.
   */
  emptyItem?: ReactNode;
  /** Доступная подпись, если рядом нет <label>. */
  'aria-label'?: string;
  /** Подпись поля. Задана — контрол переходит в слой формы (outlined-рамка с вырезом). */
  label?: string;
  /** Красная рамка без сообщения (сообщение рисует вызывающий код). Только в слое формы. */
  invalid?: boolean;
  /** Сообщение об ошибке под полем; заодно красит рамку. Только в слое формы. */
  error?: string;
  /** Вспомогательный текст под полем. Только в слое формы. */
  hint?: string;
  /** Классы внешней обёртки поля (ширина, `col-span`). Только в слое формы. */
  containerClassName?: string;
  /**
   * Значение пункта, после выбора которого список НЕ закрывается: пункт-выход «Показать все стройки»
   * (issue #1219). Его выбор раскрывает список на месте — закрой его Radix, раскрытое пришлось бы
   * открывать вторым щелчком, и выглядело бы это как «пункт не сработал» (ревью PR #1332).
   */
  stayOpenOn?: string;
  children: ReactNode;
}

const TRIGGER =
  'inline-flex items-center justify-between gap-2 rounded-md border border-stroke-strong ' +
  'bg-surface text-fg1 transition-colors data-[placeholder]:text-fg4 ' +
  'focus:outline-none focus-visible:ring-2 focus-visible:ring-brand data-[state=open]:ring-2 ' +
  'data-[state=open]:ring-brand disabled:opacity-50 disabled:pointer-events-none';

// В слое формы рамку рисует оболочка — триггер внутри неё прозрачный и без своей границы, иначе
// вышла бы рамка в рамке, а непрозрачный фон затёр бы вырез на не-surface подложке (та же ловушка,
// что у пикера типа). Кольцо фокуса там тоже лишнее: фокус показывает сама оболочка.
const FRAMED_TRIGGER =
  'inline-flex items-center justify-between gap-2 w-full h-14 px-4 rounded-md bg-transparent ' +
  'text-sm text-fg1 data-[placeholder]:text-fg4 outline-none ' +
  'disabled:opacity-50 disabled:pointer-events-none';

const SIZE = { md: 'h-9 px-3 text-sm', sm: 'h-7 px-2 gap-1 text-xs' } as const;

/** Пустая строка — «значения нет» и для Radix (placeholder), и для вызывающего кода; пункту
 *  «значения нет» нужно своё непустое значение, которое наружу не выходит. */
const EMPTY = '∅';

export function Select({
  value, onValueChange, placeholder, disabled, required, name, className = '', size = 'md', emptyItem,
  label, invalid, error, hint, containerClassName = '', stayOpenOn, children, ...aria
}: SelectProps) {
  const [open, setOpen] = useState(false);
  // Radix сообщает выбор раньше закрытия — так что закрытие после пункта-выхода узнаётся по метке.
  const staying = useRef(false);
  const [focused, setFocused] = useState(false);
  const labelId = useId();
  const framed = label !== undefined;

  // В слое формы `required` — ТОЛЬКО звёздочка при подписи: в Radix он включает скрытый
  // `<select required>` (1×1 px, `aria-hidden`), и внутри `<form>` браузер молча отказывается
  // отправлять форму, пока значение пусто. Обработчик submit при этом не вызывается — приложение
  // не показывает ни своей ошибки, ни подсказки браузера (её некуда прицепить: контрол невидим).
  // Обязательность в слое формы проверяет само приложение (`isFieldMissing` + «Обязательное поле»),
  // поэтому в Radix не передаём, а доступность закрываем `aria-required` на триггере.
  const control = (
    <RS.Root value={!value && emptyItem !== undefined ? EMPTY : value ?? ''} disabled={disabled}
      required={framed ? undefined : required} name={name}
      open={open}
      onValueChange={next => { staying.current = next === stayOpenOn; onValueChange(next === EMPTY ? '' : next); }}
      onOpenChange={next => {
        if (!next && staying.current) { staying.current = false; return; }
        setOpen(next);
      }}>
      <RS.Trigger className={framed ? `${FRAMED_TRIGGER} ${className}`
        : `${TRIGGER} ${SIZE[size]} ${/(^|\s)w-/.test(className) ? '' : 'w-full '}${className}`}
        aria-label={framed ? undefined : aria['aria-label']}
        aria-labelledby={framed ? labelId : undefined}
        aria-required={framed && required ? true : undefined}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        {/* min-w-0 + truncate: длинное выбранное значение обрезается многоточием, а не переносится
            на вторую строку, распирая триггер фиксированной высоты. */}
        <span className="min-w-0 flex-1 truncate text-left"><RS.Value placeholder={placeholder} /></span>
        <RS.Icon className="text-fg4 shrink-0"><ChevronDown size={15} /></RS.Icon>
      </RS.Trigger>
      <RS.Portal>
        <RS.Content position="popper" sideOffset={4}
          className="z-50 min-w-[var(--radix-select-trigger-width)] max-h-[var(--radix-select-content-available-height)] overflow-hidden rounded-md border border-stroke bg-surface shadow-[var(--f-shadow16)]">
          <RS.Viewport className="p-1">
            {emptyItem !== undefined && <SelectItem value={EMPTY}>{emptyItem}</SelectItem>}
            {children}
          </RS.Viewport>
        </RS.Content>
      </RS.Portal>
    </RS.Root>
  );

  if (!framed) return control;

  // Пока список открыт, фокус живёт в нём, а не на триггере — без `open` рамка гасла бы ровно на то
  // время, что человек выбирает значение.
  return (
    <OutlinedField label={label} required={required} invalid={invalid} error={error} hint={hint}
      raise="always" focused={focused || open} labelId={labelId} containerClassName={containerClassName}>
      {control}
    </OutlinedField>
  );
}

const ITEM =
  'relative flex items-center gap-2 pl-3 pr-8 py-1.5 rounded text-sm text-fg1 select-none cursor-pointer ' +
  'outline-none data-[highlighted]:bg-brand-subtle data-[highlighted]:text-brand-hover ' +
  'data-[state=checked]:font-medium data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

export const SelectItem = forwardRef<HTMLDivElement, { value: string; disabled?: boolean; children: ReactNode; className?: string }>(
  function SelectItem({ value, disabled, children, className = '' }, ref) {
    return (
      <RS.Item ref={ref} value={value} disabled={disabled} className={`${ITEM} ${className}`}>
        <RS.ItemText>{children}</RS.ItemText>
        <RS.ItemIndicator className="absolute right-2 inline-flex text-brand">
          <Check size={14} />
        </RS.ItemIndicator>
      </RS.Item>
    );
  },
);

/** Группа опций (аналог <optgroup>). */
export function SelectGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <RS.Group>
      <RS.Label className="px-3 pt-2 pb-1 text-caption font-semibold uppercase tracking-wide text-fg4">{label}</RS.Label>
      {children}
    </RS.Group>
  );
}
