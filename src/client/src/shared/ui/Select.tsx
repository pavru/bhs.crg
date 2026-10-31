import * as RS from '@radix-ui/react-select';
import { Chip } from './Chip';
import { Icon } from './Icon';
import { Check, KeyboardArrowDown } from './icons';
import { forwardRef, useId, useRef, useState, type ReactNode } from 'react';
import { OutlinedField } from './OutlinedField';
import { fieldBox, fieldDescribedBy, type FieldDensity } from './fieldBox';

/**
 * MD3-Select (issue #110, фаза 2) поверх Radix Select — замена нативному `<select>`
 * (дефект «смесь нативных и кастомных контролов» из хендоффа). Radix даёт полный
 * APG-listbox с клавиатуры (стрелки/Home/End/typeahead/Esc) — закрывает #107 F5.
 * Тема light/dark через токены.
 *
 * Два слоя (issue #565, шаг 4 — #574), и выбирает между ними наличие `label`, как у
 * `TypePickerField`:
 *
 * - **слой формы** (`label` задан) — общая MD3-оболочка `OutlinedField`, поле формы 56 или плотное 40
 *   ([UI-69]), подпись в вырезе рамки. Свой `<label>` в вызывающем коде НЕ пишем: пока подпись жила снаружи,
 *   у семи площадок набралось четыре разные типографики (12 px подписью, 14 px основным цветом и подписью,
 *   12 px другим серым) — при том, что соседние `TextField` в тех же формах уже носили
 *   подпись в вырезе. Связываем через `aria-labelledby`, а не `<label for>`: у кнопки-триггера
 *   щелчок по подписи открывал бы список;
 * - **без подписи** — вид по месту (`SelectProps`): чип отбора, плотное поле на той же оболочке (подпись —
 *   из `aria-label`, только для диктора) или `inline` внутри фразы. Компактных триггеров 36 и 28 больше нет.
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
interface SelectBase {
  value: string | undefined;
  onValueChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  /** У чипа и `inline` — участие в нативной валидации формы (скрытый `<select required>` у Radix). У поля
   *  (с подписью или плотного) — только звёздочка при подписи и `aria-required`, см. ниже. */
  required?: boolean;
  name?: string;
  /** Класс триггера (например ширина). Своя ширина (`w-…`) заменяет умолчание `w-full`, а не
   *  спорит с ним: какой из двух классов победит, решал бы порядок правил в CSS, а не в строке.
   *  У плотного поля без подписи — класс обёртки поля: рамку рисует она, и ширина — её. */
  className?: string;
  /**
   * Пункт «значения нет» первым в списке: «— не выбрано —», «без итога», «наследуется». Его выбор
   * отдаёт пустую строку. Нужен потому, что Radix не принимает пустую строку значением пункта, а
   * сбросить выбор placeholder не умеет — он лишь показывает, что выбора нет.
   */
  emptyItem?: ReactNode;
  /** Доступная подпись, если рядом нет <label>. У плотного поля без подписи — его имя. */
  'aria-label'?: string;
  /** Красная рамка без сообщения (сообщение рисует вызывающий код). У поля — с подписью или плотного. */
  invalid?: boolean;
  /**
   * Значение пункта, после выбора которого список НЕ закрывается: пункт-выход «Показать все стройки»
   * (issue #1219). Его выбор раскрывает список на месте — закрой его Radix, раскрытое пришлось бы
   * открывать вторым щелчком, и выглядело бы это как «пункт не сработал» (ревью PR #1332).
   */
  stayOpenOn?: string;
  /** Список раскрыли или закрыли — у поля отбора закрытие фиксирует выбор, как уход из нативного
   *  `<select>` (#1469): фокус, пока список открыт, живёт в нём, и `onBlur` триггера пришёл бы раньше выбора. */
  onOpenChange?: (open: boolean) => void;
  children: ReactNode;
}

/** Только у поля с подписью: сообщение и пояснение под рамкой, обёртка. */
type Below = { error?: string; hint?: string; containerClassName?: string };
type NoBelow = { error?: never; hint?: never; containerClassName?: never };

/**
 * Вид выбора ([UI-69]). С подписью — поле формы (или плотное, `density="dense"`). Без подписи — один из
 * трёх по месту, и выбрать его обязан экран: размера «по умолчанию» нет, иначе 36 и 28 px вернулись бы
 * молча.
 * - `variant="chip"` — отбор в шапке страницы или панели: чип с меню. Требует `emptyItem` («все»): чип
 *   выбран, когда значение не пусто. Выбор без нейтрального значения — не отбор и не чип.
 * - `density="dense"` — строка настроек в окне или панели, шапка колонки с вводом: плотное поле 40 с рамкой.
 * - `variant="inline"` — внутри фразы, названное отступление: без рамки и фона, высоту и цвет даёт место.
 */
export type SelectProps = SelectBase & (
  | (Below & { label: string; variant?: never; density?: FieldDensity })
  | (NoBelow & { label?: never; variant: 'chip'; emptyItem: ReactNode; density?: never; invalid?: never })
  | (NoBelow & { label?: never; variant: 'inline'; density?: never; invalid?: never })
  | (NoBelow & { label?: never; variant?: never; density: 'dense'; 'aria-label': string })
);

// В слое формы рамку рисует оболочка — триггер внутри неё прозрачный и без своей границы, иначе
// вышла бы рамка в рамке, а непрозрачный фон затёр бы вырез на не-surface подложке (та же ловушка,
// что у пикера типа). Кольцо фокуса там тоже лишнее: фокус показывает сама оболочка.
const FRAMED_TRIGGER =
  'inline-flex items-center w-full rounded-xs bg-transparent ' +
  'text-on-surface data-[placeholder]:text-on-surface-variant outline-none ' +
  'disabled-content disabled:pointer-events-none';

/**
 * Стрелка поля формы — в слоте хвоста оболочки, а не в триггере ([UI-69]): знак ошибки оболочка ставит
 * перед хвостом, и стрелка должна быть хвостом. Щелчки она пропускает к триггеру под собой. Открытый
 * список поворачивает её — exposed dropdown menu MD3.
 */
function FieldArrow({ open, disabled }: { open: boolean; disabled?: boolean }) {
  return (
    <span aria-hidden data-field-arrow data-state={open ? 'open' : 'closed'} data-disabled={disabled ? '' : undefined}
      className={'pointer-events-none flex size-8 pointer-coarse:size-10 items-center justify-center text-on-surface-variant ' +
        'transition-transform ease-standard data-[state=open]:rotate-180 disabled-content'}>
      <Icon icon={KeyboardArrowDown} size={24} />
    </span>
  );
}

// Внутри фразы — без рамки и фона ([UI-69], названное отступление): высоту, поля и цвет текста даёт место.
// Слой состояния и общее кольцо фокуса — свои, рамки, которая их показала бы, нет.
const INLINE =
  'inline-flex items-center justify-between gap-0.5 rounded-sm text-inherit state-layer focus-ring ' +
  'data-[placeholder]:text-on-surface-variant disabled-content disabled:pointer-events-none';

/** Пустая строка — «значения нет» и для Radix (placeholder), и для вызывающего кода; пункту
 *  «значения нет» нужно своё непустое значение, которое наружу не выходит. С управляющим символом:
 *  значения пунктов приходят и с данными (перечень колонки), и «∅» среди них возможен (#1469). */
const EMPTY = '\u0000∅';

export function Select({
  value, onValueChange, placeholder, disabled, required, name, className = '', variant, emptyItem,
  label, invalid, error, hint, containerClassName = '', density = 'form', stayOpenOn, onOpenChange, children, ...aria
}: SelectProps) {
  const [open, setOpen] = useState(false);
  // Radix сообщает выбор раньше закрытия — так что закрытие после пункта-выхода узнаётся по метке.
  const staying = useRef(false);
  const [focused, setFocused] = useState(false);
  const labelId = useId();
  const hintId = useId();
  // Плотное поле без подписи — то же поле на оболочке: имя ему даёт `aria-label`, а оболочка у плотного
  // поля подпись и так прячет от глаз. Ширину тогда задаёт обёртка — рамку рисует она; без своей `w-…` она
  // во всю ширину, как прежний триггер: иначе во flex-строке поле сжималось бы до выбранного текста.
  const framed = variant === undefined;
  const bare = label === undefined;

  // У поля (с подписью или плотного) `required` — ТОЛЬКО звёздочка при подписи: в Radix он включает скрытый
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
        onOpenChange?.(next);
      }}>
      {variant === 'chip' ? (
        // Чип отбора ([UI-69]): триггер Radix — сам чип, и роль `combobox`, клавиатура и typeahead остаются.
        // Выбран, когда значение не пусто: «все» возвращает чип в покой.
        <RS.Trigger asChild aria-label={aria['aria-label']}>
          <Chip selected={!!value} menu className={className}><RS.Value placeholder={placeholder} /></Chip>
        </RS.Trigger>
      ) : <RS.Trigger className={framed ? `${FRAMED_TRIGGER} ${fieldBox(density, { trailing: true })} ${bare ? '' : className}`
        : `${INLINE} ${className}`}
        aria-label={framed ? undefined : aria['aria-label']}
        aria-labelledby={framed ? labelId : undefined}
        aria-required={framed && required ? true : undefined}
        aria-describedby={framed ? fieldDescribedBy(hintId, error || hint) : undefined}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        {/* min-w-0 + truncate: длинное выбранное значение обрезается многоточием, а не переносится
            на вторую строку, распирая триггер фиксированной высоты. */}
        <span className="min-w-0 flex-1 truncate text-left"><RS.Value placeholder={placeholder} /></span>
        {!framed && <RS.Icon className="text-on-surface-variant shrink-0"><Icon icon={KeyboardArrowDown} size={18} /></RS.Icon>}
      </RS.Trigger>}
      <RS.Portal>
        <RS.Content position="popper" sideOffset={4}
          className="z-50 min-w-[var(--radix-select-trigger-width)] max-h-[var(--radix-select-content-available-height)] overflow-hidden rounded-sm border border-outline-variant bg-surface shadow-elevation-2">
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
    <OutlinedField label={label ?? aria['aria-label'] ?? ''} required={required} invalid={invalid} error={error} hint={hint} hintId={hintId}
      raise="always" focused={focused || open} labelId={labelId} density={bare ? 'dense' : density} containerClassName={bare ? `${/(^|\s)w-/.test(className) ? '' : 'w-full '}${className}` : containerClassName}
      trailing={<FieldArrow open={open} disabled={disabled} />}>
      {control}
    </OutlinedField>
  );
}

const ITEM =
  'relative flex items-center gap-2 pl-3 pr-8 py-1.5 rounded-xs text-body-medium text-on-surface select-none cursor-pointer ' +
  'outline-none state-layer data-[state=checked]:font-medium disabled-content data-[disabled]:pointer-events-none';

export const SelectItem = forwardRef<HTMLDivElement, { value: string; disabled?: boolean; children: ReactNode; className?: string }>(
  function SelectItem({ value, disabled, children, className = '' }, ref) {
    return (
      <RS.Item ref={ref} value={value} disabled={disabled} className={`${ITEM} ${className}`}>
        <RS.ItemText>{children}</RS.ItemText>
        <RS.ItemIndicator className="absolute right-2 inline-flex text-primary">
          <Icon icon={Check} size={18} />
        </RS.ItemIndicator>
      </RS.Item>
    );
  },
);

/** Группа опций (аналог <optgroup>). */
export function SelectGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <RS.Group>
      <RS.Label className="px-3 pt-2 pb-1 text-title-small text-on-surface-variant">{label}</RS.Label>
      {children}
    </RS.Group>
  );
}
