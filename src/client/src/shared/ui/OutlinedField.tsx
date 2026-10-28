import { type ReactNode } from 'react';

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
   * Значение показано, но не правится (issue #958). Отмечается приглушённой подложкой — той же,
   * что у полей без оболочки (`fieldInputClass`), чтобы read-only выглядел одинаково во всей форме.
   *
   * ⚠️ Это НЕ `disabled`: рамка, метка и фокус остаются обычными. Поле доступно — в него встают
   * клавиатурой, значение выделяют и копируют; приглушена только подложка, и она говорит «сюда не
   * пишут», а не «сейчас недоступно».
   */
  readOnly?: boolean;
  /** Правый адорнмент (например «глаз» пароля). */
  trailing?: ReactNode;
  /** Классы внешней обёртки (поле + подпись под ним). */
  containerClassName?: string;
  children: ReactNode;
}

export function OutlinedField({
  label, required, invalid, error, hint, hintId, raise = 'auto', focused, htmlFor, labelId,
  readOnly, trailing, containerClassName = '', children,
}: OutlinedFieldProps) {
  const bad = !!error || invalid;
  const auto = raise === 'auto';

  // peer-* реагируют только на СОСЕДЕЙ input'а, поэтому реакцию на фокус/заполнение вешаем на
  // fieldset (он сосед) и целим вложенный legend через [&>legend].
  const borderColor = bad
    ? 'border-error'
    : auto ? 'border-outline peer-focus:border-primary'
      : focused ? 'border-primary' : 'border-outline';
  const borderWidth = auto ? 'peer-focus:border-2' : focused ? 'border-2' : '';
  // Вырез раскрывает legend, а он ВНУТРИ fieldset — соседом input'у он не приходится, поэтому
  // peer-варианты вешаем на fieldset и целим вложенный legend через [&>legend].
  const legendWidth = auto ? 'max-w-[0.01px]' : 'max-w-full';
  const legendOpener = auto
    ? 'peer-focus:[&>legend]:max-w-full peer-[:not(:placeholder-shown)]:[&>legend]:max-w-full'
    : '';
  const labelColor = bad
    ? 'text-error'
    : auto ? 'text-fg4 peer-focus:text-primary'
      : focused ? 'text-primary' : 'text-fg4';
  // В `auto` метка стоит по центру и уезжает наверх; в `always` она уже наверху.
  const labelPosition = auto
    ? 'top-1/2 -translate-y-1/2 text-sm peer-focus:top-0 peer-focus:text-xs '
      + 'peer-[:not(:placeholder-shown)]:top-0 peer-[:not(:placeholder-shown)]:text-xs'
    : 'top-0 -translate-y-1/2 text-xs';

  // Звёздочка — знак для глаз, а не часть имени поля (issue #1291): обязательность читалке называет сам
  // контрол (`required` / `aria-required`), а текст подписи «Номер*» ломал бы поиск поля по его имени.
  // Поэтому она рисуется псевдоэлементом: в тексте подписи её нет.
  // ⚠️ Отсюда долг каждого поля на этой оболочке: донести `required` до своего контрола. У `DateField`
  // и `TypePickerField` он не доходил, и обязательность для чтения с экрана пропадала (ревью PR #1301).
  const labelContent = label;
  const star = required ? " after:ml-0.5 after:text-error after:content-['*']" : '';
  const labelClass = `absolute left-3 px-1 pointer-events-none transition-all block max-w-[calc(100%-1.5rem)] truncate ${labelColor} ${labelPosition}${star}`;

  return (
    <div className={containerClassName}>
      <div className={readOnly ? 'relative isolate' : 'relative'}>
        {/* Подложка ПОД детьми: контролы внутри прозрачные и рисуются поверх неё. Своим фоном на
            контейнере не обойтись — оболочка намеренно прозрачна, чтобы поле жило на любом фоне.
            ⚠️ «Под» даёт отрицательный слой в своём контексте наложения, а не место в разметке:
            позиционированная подложка рисуется ПОВЕРХ обычного поля, где бы она ни стояла, — и поле
            для чтения выглядело пустым при заполненном значении (нашлось на issue #1282). */}
        {readOnly && <div aria-hidden className="absolute inset-0 -z-10 rounded-md bg-surface-container" />}
        {children}
        {/* Рамка с вырезом: fieldset даёт границу, legend прорезает верх под меткой. Ширина legend
            и есть вырез — она же анимируется, когда метка всплывает. */}
        <fieldset aria-hidden
          className={`pointer-events-none absolute inset-x-0 bottom-0 top-[-5px] m-0 rounded-md border px-3 transition-colors ${borderColor} ${borderWidth} ${legendOpener}`}>
          <legend className={`h-2.5 w-auto whitespace-nowrap p-0 text-xs invisible transition-[max-width] duration-100 ${legendWidth}`}>
            <span className="inline-block px-1 opacity-0">{label}{required ? ' *' : ''}</span>
          </legend>
        </fieldset>
        {htmlFor
          ? <label id={labelId} htmlFor={htmlFor} className={labelClass}>{labelContent}</label>
          : <span id={labelId} className={labelClass}>{labelContent}</span>}
        {trailing && <div className="absolute right-2 top-1/2 -translate-y-1/2 z-10">{trailing}</div>}
      </div>
      {error
        ? <p id={hintId} className="mt-1 px-1 text-xs text-error">{error}</p>
        : hint ? <p id={hintId} className="mt-1 px-1 text-xs text-fg4">{hint}</p> : null}
    </div>
  );
}
