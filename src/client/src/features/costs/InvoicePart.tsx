import { useId, useRef, type ReactNode } from 'react';
import { Card } from '@/shared/ui/Card';
import { Icon } from '@/shared/ui/Icon';
import { KeyboardArrowDown, KeyboardArrowUp } from '@/shared/ui/icons';
import { fullTextIfCut } from '@/shared/ui/fullTextIfCut';

/**
 * Часть формы счёта (costs-invoice-screen-10-layout, D-10.1, ТЗ COST-6.2): шаг работы — карточкой
 * outlined, которую можно свернуть в строку-итог.
 *
 * <p>Шапку собирает экран внутри `Card` (решение владельца Э6, 06.11.2026): у `Card` в шапке только
 * заголовок, знак и меню, а части нужны номер шага, строка-итог, свои действия справа и свёртка. Общего
 * свойства у `Card` не заводится — до второго потребителя. Поэтому карточка — `flush`, без своей шапки,
 * и обе полосы рисует часть.</p>
 *
 * <p>Свёрнутая часть тело ПРЯЧЕТ, а не размонтирует (ревью PR #1787): в теле живёт состояние — черновик
 * строк держит сама таблица, — и свёртка «Строк» иначе молча выбрасывала бы несохранённые правки.</p>
 *
 * <p>Шапка целиком — цель раскрытия (costs-invoice-part-header-toggle, #1869, решения владельца
 * 11.11.2026): аккордеон WAI-ARIA — кнопка в `<h3>`, а её `::after` растянут на всю шапку. Так заголовок
 * остаётся заголовком для навигации диктора, а раскладка шапки не меняется. Действия части стоят над
 * растянутой целью и не вложены в неё (нажимаемое в нажимаемом запрещено); пустое место их строки
 * пропускает щелчок к цели. Имя цели называет действие («Свернуть «Строки»») и держит `aria-expanded`,
 * строка-итог — её описание. Слой состояния — `state-layer-row`, как у `ListGroupHeader`: нажатие и
 * фокус — по цели, под курсором на действии горит действие, а не шапка.</p>
 */
export function InvoicePart({ title, step, line, open, onToggle, actions, children, testId }: {
  title: string;
  /** Номер шага: у четырёх частей первого прохода он есть, у «Ещё» и «Истории» — нет. */
  step?: number;
  /** Строка-итог: главное из части. Видна у свёрнутой. */
  line?: string;
  open: boolean;
  onToggle: () => void;
  /** Действия части справа в шапке — видны и у свёрнутой («Всё верно (5)»). */
  actions?: ReactNode;
  children?: ReactNode;
  testId?: string;
}) {
  const bodyId = useId();
  const lineId = useId();
  const lineRef = useRef<HTMLSpanElement>(null);
  const shownLine = !open && line ? line : undefined;
  return (
    <Card variant="outlined" flush className="overflow-visible!" data-testid={testId}>
      {/* Узкая часть (рядом со списком и файлом) переносит действия строкой ниже, а шеврон остаётся у
          заголовка: иначе он уходил за край карточки. */}
      <section aria-label={title} className="@container">
        <div className={`relative flex flex-wrap items-center gap-x-3 gap-y-1 min-h-14 py-2 pr-2 pl-4 state-layer state-layer-row text-on-surface ${open ? 'rounded-t-md' : 'rounded-md'}`}>
          <h3 className="flex min-w-0">
            {/* Подсказку обрезанной строки-итога держит цель: строка лежит под её `::after` и наведения не
                получает (D4). */}
            <button type="button" data-li-target="" aria-label={`${open ? 'Свернуть' : 'Развернуть'} «${title}»`}
              aria-expanded={open} aria-controls={bodyId} aria-describedby={shownLine ? lineId : undefined}
              onClick={onToggle} onPointerEnter={shownLine ? e => fullTextIfCut(shownLine, lineRef.current)(e) : undefined}
              className="flex min-w-0 items-center gap-3 text-left cursor-pointer focus:outline-none after:absolute after:inset-0">
              {step !== undefined && (
                <span className="shrink-0 text-title-medium text-on-surface-variant tabular-nums" aria-hidden>{step}</span>
              )}
              <span className="min-w-0 truncate text-title-medium text-on-surface">{title}</span>
            </button>
          </h3>
          {/* Строка-итог — одной строкой: лишнее режется многоточием, полный текст — подсказкой цели. */}
          <span ref={lineRef} id={lineId} className="flex-1 min-w-0 truncate text-body-medium text-on-surface-variant">
            {shownLine}
          </span>
          {/* Пустое место строки действий (узкая часть) пропускает щелчок к цели; сами действия ловят свой —
              и неактивное тоже: иначе щелчок по нему проваливался бы к цели и переключал часть. */}
          {actions && (
            <div data-li-side=""
              className="relative z-10 pointer-events-none *:pointer-events-auto [&_:disabled]:pointer-events-auto! order-last w-full @md:order-none @md:w-auto shrink-0 flex flex-wrap items-center gap-2">
              {actions}
            </div>
          )}
          {/* Шеврон только показывает состояние: цель — вся шапка. Место прежней кнопки-иконки — 40. */}
          <span className="shrink-0 size-10 grid place-items-center text-on-surface-variant">
            <Icon icon={open ? KeyboardArrowUp : KeyboardArrowDown} size={24} />
          </span>
        </div>
        <div id={bodyId} hidden={!open} className="flex flex-col gap-3 px-4 pb-4">{children}</div>
      </section>
    </Card>
  );
}
