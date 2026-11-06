import { useId, type ReactNode } from 'react';
import { Card } from '@/shared/ui/Card';
import { IconButton } from '@/shared/ui/Button';
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
 * строк держит сама таблица, — и свёртка «Строк» иначе молча выбрасывала бы несохранённые правки. Кнопка свёртки называет действие («Свернуть «Строки»») и
 * держит `aria-expanded` — читалка слышит и что будет, и что сейчас.</p>
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
  return (
    <Card variant="outlined" flush className="overflow-visible!" data-testid={testId}>
      {/* Узкая часть (рядом со списком и файлом) переносит действия строкой ниже, а «свернуть» остаётся
          у заголовка: иначе кнопка свёртки уходила за край карточки. */}
      <section aria-label={title} className="@container">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 min-h-14 py-2 pr-2 pl-4">
          {step !== undefined && (
            <span className="shrink-0 text-title-medium text-on-surface-variant tabular-nums" aria-hidden>{step}</span>
          )}
          <h3 className="min-w-0 truncate text-title-medium text-on-surface">{title}</h3>
          {/* Строка-итог — одной строкой: лишнее режется многоточием, полный текст — подсказкой. */}
          <span className="flex-1 min-w-0 truncate text-body-medium text-on-surface-variant"
            onPointerEnter={line ? fullTextIfCut(line) : undefined}>
            {!open && line}
          </span>
          {actions && (
            <div className="order-last w-full @md:order-none @md:w-auto shrink-0 flex flex-wrap items-center gap-2">
              {actions}
            </div>
          )}
          <IconButton label={`${open ? 'Свернуть' : 'Развернуть'} «${title}»`}
            icon={open ? KeyboardArrowUp : KeyboardArrowDown}
            aria-expanded={open} aria-controls={bodyId} onClick={onToggle} />
        </div>
        <div id={bodyId} hidden={!open} className="flex flex-col gap-3 px-4 pb-4">{children}</div>
      </section>
    </Card>
  );
}
