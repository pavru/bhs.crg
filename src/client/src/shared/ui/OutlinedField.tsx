import { type CSSProperties, type ReactNode } from 'react';
import { ICON_BUTTON_XS } from './buttonClasses';
import { Icon } from './Icon';
import { ErrorIcon } from './icons';
import { MARK_SPACE, type FieldDensity } from './fieldBox';
import { Tooltip } from './Tooltip';

/**
 * Оболочка MD3 outlined-поля (issue #565): рамка с НАСТОЯЩИМ вырезом под меткой (fieldset+legend),
 * сама метка, состояния фокуса/ошибки и подпись под полем. Контрол внутрь передаётся детьми.
 *
 * Появилась, потому что разметка выреза уже была продублирована в `TextField` (#178) и `DateField`
 * (#176), а пикер типа и `Select` дали бы третью и четвёртую копию. Контейнер прозрачный, поэтому
 * поле корректно выглядит на ЛЮБОМ фоне (surface/base/карточка), а не только на surface;
 * тема light/dark — через токены.
 *
 * Метка живёт в двух режимах, и это не украшение, а разные механики:
 *
 * - `raise="auto"` — метка всплывает по CSS: покоится по центру у пустого поля и уезжает в вырез
 *   при фокусе или заполнении. Работает только если ребёнок — сам `<input>` с классом `peer` и
 *   `placeholder=" "` (по `:placeholder-shown` CSS и отличает пустое поле). Так живёт `TextField`.
 * - `raise="always"` — метка ПОСТОЯННО во всплывшем положении, вырез всегда открыт. Нужен там, где
 *   у контрола нет состояния «пусто» в смысле CSS: у даты всегда видны плейсхолдеры сегментов
 *   (штатный MD3-паттерн для полей с форматной подсказкой), у кнопки-триггера пикера — значок и
 *   имя выбранного. Фокус в этом режиме приходит пропом `focused`: он живёт на составном контроле,
 *   а не на единственном input, и поймать его селектором нельзя.
 *
 * Размеров два ([UI-69], sharedui-md3-11): `density="form"` — поле формы 56 с подписью в вырезе, `dense` —
 * плотное поле 40 без выреза: строка настроек, шапка колонки с вводом. Имя плотного поля — та же подпись,
 * только для диктора (`sr-only`): видимое имя ему дают шапка колонки или строка. Высоту, отступы и роль
 * текста контрол берёт из `fieldBox` — у пяти полей на оболочке они одни.
 *
 * КОНВЕНЦИЯ РАЗМЕЩЕНИЯ. Всплывшая метка выходит ЗА верхнюю границу поля примерно на 8px (она
 * `-translate-y-1/2` от `top-0`, а рамка-fieldset поднята на 5px), и в поток эти пиксели не входят:
 * поле «занимает» меньше места, чем видно. Поэтому стек полей обязан задавать внешний зазор —
 * `space-y-3` и больше у контейнера (или `gap-3` у сетки). Без него метка нижнего поля садится на
 * подпись/рамку верхнего: так счётчик «Показано N из M» наехал на метку селекта в диалоге нового
 * источника (issue #727).
 *
 * Внутренний отступ самому компоненту НЕ добавлять: поле живёт и одиночным (в ряду, в ячейке
 * таблицы), где лишний воздух сверху разъедет уже выверенные раскладки — а их десятки.
 */
/**
 * Знак ошибки поля ([UI-69]): ошибка видна не только цветом. У поля формы — 24, текст ошибки стоит под
 * полем; у плотного — 20 с подсказкой, потому что места под полем нет. Для диктора знак скрыт: текст
 * ошибки он читает через `aria-describedby` поля.
 *
 * Ставит его оболочка. Контрол, у которого хвост свой (календарь даты), ставит его сам перед хвостом —
 * этим же компонентом, и оболочке тогда говорят `markInside`: двух знаков у поля не бывает. Им же рисует
 * свой знак ошибка набранного у сегментов без оболочки (`SegmentProblemMark`).
 */
export function FieldErrorMark({ density = 'form', message }: { density?: FieldDensity; message?: string | null }) {
  const mark = (
    <span data-field-error-mark aria-hidden
      className={`flex shrink-0 items-center text-error ${density === 'dense' ? 'pointer-events-auto' : ''}`}>
      <Icon icon={ErrorIcon} size={density === 'dense' ? 20 : 24} />
    </span>
  );
  return density === 'dense' && message ? <Tooltip content={message}>{mark}</Tooltip> : mark;
}

export interface OutlinedFieldProps {
  label: string;
  required?: boolean;
  /** Красная рамка без сообщения (сообщение рисует вызывающий код). */
  invalid?: boolean;
  /** Сообщение об ошибке под полем; заодно красит рамку. */
  error?: string;
  /** Вспомогательный текст под полем (напр. пример ввода) — скрывается при наличии `error`. */
  hint?: string;
  /**
   * id подписи под полем — контрол ссылается на неё `aria-describedby` (issue #1468): иначе пояснение
   * и ошибку под рамкой скринридер с полем не связывает. Стоит на той подписи, что видна.
   */
  hintId?: string;
  raise?: 'auto' | 'always';
  /** Только для `raise="always"`: подсветить рамку и метку как при фокусе. */
  focused?: boolean;
  /** id управляемого контрола — метка станет `<label for>`. Для кнопок-триггеров НЕ задавать:
   *  щелчок по подписи открывал бы модалку. Там доступное имя связывают через `labelId`. */
  htmlFor?: string;
  /** id самой метки — для `aria-labelledby` у контролов, которым `<label for>` не годится. */
  labelId?: string;
  /**
   * Значение показано, но не правится (issue #958). Отмечается обводкой `outline-variant` без подложки
   * ([UI-69], выбор владельца 31.10.2026): у MD3 залитое поле значит «правится», а приглушение —
   * «отключено».
   *
   * ⚠️ Это НЕ `disabled`: метка и фокус остаются обычными. Поле доступно — в него встают клавиатурой,
   * значение выделяют и копируют; другой только обводка, и она говорит «сюда не пишут», а не «сейчас
   * недоступно».
   */
  readOnly?: boolean;
  /** Поле формы (56, подпись в вырезе) или плотное (40, без выреза) — см. `fieldBox`. */
  density?: FieldDensity;
  /**
   * Правый адорнмент (например «глаз» пароля). Хвост — место XS ([UI-68], sharedui-md3-10): кнопка-иконка
   * в нём 32 при мыши и 40 при касании — класс места ставит обёртка, экран размера не пишет. Отступ текста
   * под хвост держит само поле — `fieldBox(density, { trailing: true })`.
   */
  trailing?: ReactNode;
  /** Хвост живёт внутри контрола (календарь даты): знак ошибки контрол ставит сам — `FieldErrorMark`. */
  markInside?: boolean;
  /** Классы внешней обёртки (поле + подпись под ним). */
  containerClassName?: string;
  children: ReactNode;
}

export function OutlinedField({
  label, required, invalid, error, hint, hintId, raise = 'auto', focused, htmlFor, labelId,
  readOnly, density = 'form', trailing, markInside, containerClassName = '', children,
}: OutlinedFieldProps) {
  const bad = !!error || invalid;
  const dense = density === 'dense';
  // У плотного поля метки в вырезе нет вовсе — механики всплывания ему не нужно.
  const auto = raise === 'auto' && !dense;
  const markHere = bad && !markInside;

  // peer-* реагируют только на СОСЕДЕЙ input'а, поэтому реакцию на фокус/заполнение вешаем на
  // fieldset (он сосед) и целим вложенный legend через [&>legend].
  const rest = readOnly ? 'border-outline-variant' : 'border-outline';
  const borderColor = bad
    ? 'border-error'
    : auto ? `${rest} peer-focus:border-primary`
      : focused ? 'border-primary' : rest;
  const borderWidth = auto ? 'peer-focus:border-2' : focused ? 'border-2' : '';
  // Наведение — обводка `on-surface` (MD3). Не у фокуса, ошибки, отключённого и поля для чтения: им своя.
  const hover = bad || readOnly || focused ? ''
    : '[&:hover:not(:focus-within):not(:has(:disabled))>fieldset]:border-on-surface';
  // Вырез раскрывает legend, а он ВНУТРИ fieldset — соседом input'у он не приходится, поэтому
  // peer-варианты вешаем на fieldset и целим вложенный legend через [&>legend].
  const legendWidth = auto ? 'max-w-[0.01px]' : 'max-w-full';
  const legendOpener = auto
    ? 'peer-focus:[&>legend]:max-w-full peer-[:not(:placeholder-shown)]:[&>legend]:max-w-full'
    : '';
  const labelColor = bad
    ? 'text-error'
    : auto ? 'text-on-surface-variant peer-focus:text-primary'
      : focused ? 'text-primary' : 'text-on-surface-variant';
  // В `auto` метка стоит по центру тем же `body-large`, что и текст, и уезжает наверх; в `always` она
  // уже наверху.
  const labelPosition = auto
    ? 'top-1/2 -translate-y-1/2 text-body-large peer-focus:top-0 peer-focus:text-body-small '
      + 'peer-[:not(:placeholder-shown)]:top-0 peer-[:not(:placeholder-shown)]:text-body-small'
    : 'top-0 -translate-y-1/2 text-body-small';

  // Звёздочка — знак для глаз, а не часть имени поля (issue #1291): обязательность читалке называет сам
  // контрол (`required` / `aria-required`), а текст подписи «Номер*» ломал бы поиск поля по его имени.
  // Поэтому она рисуется псевдоэлементом: в тексте подписи её нет.
  // ⚠️ Отсюда долг каждого поля на этой оболочке: донести `required` до своего контрола. У `DateField`
  // и `TypePickerField` он не доходил, и обязательность для чтения с экрана пропадала (ревью PR #1301).
  const star = required ? " after:ml-0.5 after:text-error after:content-['*']" : '';
  const labelClass = dense ? 'sr-only'
    : `absolute left-3 px-1 pointer-events-none transition-all ease-standard block max-w-[calc(100%-1.5rem)] truncate ${labelColor} ${labelPosition}${star}`;
  const markStyle = markHere ? { '--field-mark': MARK_SPACE[density] } as CSSProperties : undefined;

  // У плотного поля текста под ним нет и места нет: ошибку и пояснение диктор читает, глаз видит знак.
  const below = error || hint;
  const belowClass = dense ? 'sr-only'
    : `mt-1 px-1 text-body-small ${error ? 'text-error' : 'text-on-surface-variant'}`;

  return (
    <div className={containerClassName}>
      <div className={`relative ${hover}`} style={markStyle}>
        {children}
        {/* Рамка с вырезом: fieldset даёт границу, legend прорезает верх под меткой. Ширина legend
            и есть вырез — она же анимируется, когда метка всплывает. У плотного поля выреза нет. */}
        <fieldset aria-hidden
          className={`pointer-events-none absolute inset-x-0 bottom-0 ${dense ? 'top-0' : 'top-[-5px]'} m-0 rounded-xs border px-3 transition-colors ease-standard ${borderColor} ${borderWidth} ${legendOpener}`}>
          {!dense && (
            <legend className={`h-2.5 w-auto whitespace-nowrap p-0 text-body-small invisible transition-[max-width] duration-100 ease-standard ${legendWidth}`}>
              <span className="inline-block px-1 opacity-0">{label}{required ? ' *' : ''}</span>
            </legend>
          )}
        </fieldset>
        {htmlFor
          ? <label id={labelId} htmlFor={htmlFor} className={labelClass}>{label}</label>
          : <span id={labelId} className={labelClass}>{label}</span>}
        {/* Слот хвоста: знак ошибки, за ним адорнмент. Сам слот щелчков не ловит — под ним поле, а хвост
            получает их сам (`contents` не даёт обёртке своего прямоугольника). */}
        {(markHere || trailing) && (
          <div className={`pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 z-10 flex items-center gap-1 ${ICON_BUTTON_XS}`}>
            {markHere && <FieldErrorMark density={density} message={error} />}
            {trailing && <span className="contents pointer-events-auto">{trailing}</span>}
          </div>
        )}
      </div>
      {below ? <p id={hintId} className={belowClass}>{below}</p> : null}
    </div>
  );
}
