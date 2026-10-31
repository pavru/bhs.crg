import { useId, useRef, useState } from 'react';
import { Icon } from './Icon';
import { Close, Search, Widgets } from './icons';
import { TypePicker, type PickType } from './TypePicker';
import { typeIcon } from './typeIcons';
import { OutlinedField } from './OutlinedField';
import { fieldBox, type FieldDensity } from './fieldBox';
import { Tooltip } from './Tooltip';

/**
 * Триггер-поле над `TypePicker` (issue #266): закрытый вид читается как поле формы (значок
 * семейства + имя выбранного типа + код в слое формы + лупа), клик/Enter/Space открывают богатую
 * модалку выбора. Единый контрол для ЛЮБОГО выбора типа (документа/поля/родителя), чтобы не плодить
 * свои триггеры на каждом сайте. Триггер — настоящий `<button aria-haspopup>`, фокус возвращается
 * на него по Esc (Radix Dialog). Не-типовые короткие списки (роль/scope) — обычный Select.
 *
 * Замыкающий значок — ЛУПА, а не шеврон (issue #565). Обещание о том, что произойдёт по клику,
 * несёт именно он: шеврон в MD3 означает приклеенное меню, а у нас открывается модалка с поиском,
 * группами и «недавними». Оболочку поля при этом можно носить честно — врал только значок.
 *
 * Два слоя (issue #565), и выбирает между ними наличие `label`:
 *
 * - **слой формы** (`label` задан) — MD3 outlined-оболочка `OutlinedField`, поле формы 56 или плотное 40
 *   ([UI-69]), подпись в вырезе рамки. Свой `<label>` в вызывающем коде НЕ пишем: он разъехался на три разные
 *   типографики, пока жил снаружи. Подпись связана через `aria-labelledby`, а не `<label for>` —
 *   у кнопки щелчок по подписи открывал бы модалку;
 * - **без подписи** — плотное поле 40 на той же оболочке ([UI-69]): имя из `aria-label`, подпись только
 *   для диктора. Компактных триггеров 32 и 36 больше нет.
 */
export function TypePickerField({
  types, value, onChange, recentKey, title = 'Выберите тип', placeholder = 'Выберите тип',
  label, required, clearable, disabled, className = '', 'aria-label': ariaLabel, density = 'form',
}: {
  types: PickType[];
  value: string | undefined;
  /** id выбранного типа, либо null при выборе «нет значения» (только при `clearable`). */
  onChange: (id: string | null) => void;
  recentKey?: string;
  title?: string;
  placeholder?: string;
  /** Подпись поля. Задана — контрол переходит в слой формы (outlined-рамка с вырезом). Без неё — плотное
   *  поле с именем из `aria-label`; кода типа там нет (см. `showCode`, issue #668). */
  label?: string;
  /** Поле формы 56 (по умолчанию) или плотное 40 без выреза — [UI-69]. Без подписи — всегда плотное. */
  density?: FieldDensity;
  required?: boolean;
  /** Показать строку «нет значения» в пикере + крестик-сброс на триггере. */
  clearable?: { label: string };
  disabled?: boolean;
  className?: string;
  'aria-label'?: string;
}) {
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const labelId = useId();
  const selected = value ? types.find(t => t.id === value) : undefined;
  const icon = selected ? typeIcon(selected) : Widgets;
  const code = selected?.code.trim();
  const framed = label !== undefined;
  /**
   * Код показываем только в СЛОЕ ФОРМЫ (issue #668). Раньше условием был размер, и компактный
   * триггер размера `md` код получал — а места под него там нет: имя стоит `flex-1 truncate`, код
   * `shrink-0`, и при нехватке ширины сжималось имя, вплоть до нуля. В поле оставался один код
   * («СетрификатСоответствия» вместо «Сертификат соответствия») — то есть исчезало ровно то, ради
   * чего поле существует. Коды в проекте пишутся слитно, имена с пробелами, так что проверка на
   * несовпадение почти всегда истинна и от этого не спасала.
   */
  const showCode = framed && !!code && code.toLowerCase() !== selected!.name.trim().toLowerCase();
  const showClear = !!clearable && !!selected && !disabled;

  // Рамку и фон рисует оболочка — триггер внутри неё прозрачный и без своей границы, иначе получилась бы
  // рамка в рамке. Кольцо фокуса тоже не нужно: фокус показывает сама оболочка (border-2 + подпись цветом
  // бренда), как у `DateField`.
  const triggerClass = `w-full ${fieldBox(framed ? density : 'dense')} rounded-xs bg-transparent text-left outline-none ` +
    `disabled-content disabled:pointer-events-none`;

  // `overflow-hidden` на триггере — страховка впредь (issue #668): её не было, и несжимаемый
  // элемент (код типа) не обрезался, а выходил за рамку поля и наезжал на замыкающую лупу.
  const trigger = (
    <button
      ref={triggerRef}
      type="button" disabled={disabled} onClick={() => setOpen(true)}
      aria-haspopup="dialog" aria-expanded={open}
      aria-labelledby={required ? `${labelId} ${labelId}-required` : labelId}
      className={`inline-flex items-center gap-2 overflow-hidden ${triggerClass}`}
      data-state={open ? 'open' : 'closed'}
    >
      <Icon icon={icon} size={18} className={`shrink-0 ${selected ? 'text-on-surface-variant' : 'text-on-surface-variant'}`} />
      {/* Подсказка — на самом обрезаемом тексте, а не на кнопке (issue #548): подсказку кнопки
          наследуют и лупа, и код типа, и всплывало бы имя типа поверх них. */}
      <Tooltip content={selected ? (showCode ? `${selected.name} (${code})` : selected.name) : undefined}>
        <span className={`flex-1 truncate ${selected ? 'text-on-surface' : 'text-on-surface-variant'}`}>
          {selected ? selected.name : placeholder}
        </span>
      </Tooltip>
      {showCode && <span className="text-label-small font-mono text-on-surface-variant shrink-0">{code}</span>}
      {/* Место под крестик резервируем В ПОТОКЕ, а не правым паддингом: паддинг сдвинул бы влево и
          саму лупу (она такой же элемент потока), и у края поля осталась бы дыра. */}
      {showClear && <span aria-hidden className="w-[22px] shrink-0" />}
      <Icon icon={Search} size={18} className="shrink-0 text-on-surface-variant" />
    </button>
  );

  // «Очистить» — отдельная кнопка РЯДОМ с триггером, а не `<span role="button">` внутри него
  // (issue #565): вложенный интерактив невалиден, и с клавиатуры до него было не добраться.
  // Лишняя остановка Tab здесь правильная — действие полноценное.
  //
  // Фокус после нажатия возвращаем на триггер (issue #572): кнопка исчезает вместе со значением, а
  // удалённому элементу браузер не шлёт blur — обработчик обёртки не срабатывал, поле навсегда
  // оставалось «в фокусе» (бренд-рамка без фокуса), а сам фокус падал на <body>, и следующий Tab
  // начинался с начала страницы.
  const clear = showClear && (
    <Tooltip content="Очистить">
      <button type="button" aria-label="Очистить"
        onClick={() => { onChange(null); triggerRef.current?.focus(); }}
        className={`absolute right-10 top-1/2 -translate-y-1/2 z-10 text-on-surface-variant ` +
          // Прозрачность НЕ отменяет попадания, а крестик лежит поверх триггера: без
          // pointer-events-none нажатие у правого края на тач-устройстве молча очищало бы значение
          // вместо открытия пикера (в диалоге материализации — вместе со всем маппингом).
          // Где hover'а нет, показываем его сразу, иначе до него было бы не добраться.
          `opacity-0 pointer-events-none transition-opacity ease-standard ` +
          `group-hover:opacity-100 group-hover:pointer-events-auto ` +
          `group-focus-within:opacity-100 group-focus-within:pointer-events-auto ` +
          `[@media(pointer:coarse)]:opacity-100 [@media(pointer:coarse)]:pointer-events-auto ` +
          `focus-visible:opacity-100 focus-ring rounded-xs`}>
        <Icon icon={Close} size={18} />
      </button>
    </Tooltip>
  );

  // Фокус слушаем на обёртке, а не на самом триггере: крестик — его сосед и визуально часть того же
  // поля, поэтому переход Tab'ом на него не должен гасить рамку. Приём тот же, что в `DateField`.
  const body = (
    <div className="relative flex w-full"
      onFocus={() => setFocused(true)}
      onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false); }}>
      {trigger}
      {clear}
      {/* Звёздочка у подписи — псевдоэлемент и в имя поля не входит (issue #1291), а у кнопки нет
          `aria-required`: обязательность называем словом, вторым источником имени. */}
      {required && <span id={`${labelId}-required`} className="sr-only">обязательное</span>}
    </div>
  );

  return (
    <>
      <OutlinedField label={label ?? ariaLabel ?? ''} required={required} raise="always" focused={focused}
        labelId={labelId} density={framed ? density : 'dense'} containerClassName={`group ${className}`}>
        {body}
      </OutlinedField>

      <TypePicker
        open={open} onOpenChange={setOpen} title={title} recentKey={recentKey}
        types={types} onSelect={id => onChange(id)}
        noneOption={clearable} onSelectNone={clearable ? () => onChange(null) : undefined}
      />
    </>
  );
}
