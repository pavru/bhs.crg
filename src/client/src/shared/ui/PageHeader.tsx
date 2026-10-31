import { useId, type ReactNode } from 'react';
import { Icon } from './Icon';
import { KeyboardArrowDown, ViewList } from './icons';
import { IconButton } from './Button';
import { SectionMenu } from './SectionMenu';
import { useSectionMenu } from './sectionMenuContext';
import { Tooltip } from './Tooltip';

/**
 * Заголовок страницы (issue #1352). До него на 26 страниц приходилось восемь вариантов `h1`, и
 * действия, подзаголовок и счётчик каждый экран раскладывал сам.
 *
 * - `title` — строка или узел. Узел нужен, когда заголовок сам по себе контрол (выбор представления
 *   таблицы): он всё равно остаётся внутри `h1`. Длинная строка обрезается, полностью — в подсказке.
 * - `icon` — ведущий узел слева: значок раздела или кнопка «назад».
 * - `badge` — показатель состояния рядом с заголовком (готовность по плану): не в `actions`, иначе
 *   цифра среди кнопок читается как ещё одна кнопка.
 * - `subtitle` — одна-две строки пояснения. Длинный текст о том, как устроен раздел, — содержимое
 *   страницы, а не её шапка.
 * - `actions` — справа; filled-кнопка среди них не больше одной (правило `Button`).
 * - `bar` — шапка полосой над разделённой страницей (list-detail, редакторы): поля и черта снизу. Без
 *   него шапка стоит в потоке содержимого, и отступ вокруг задаёт страница.
 *
 * Узкое место — по ширине САМОЙ шапки (container query), а не окна: рядом бывает боковое меню, и
 * окно в 1280 px оставляет странице меньше тысячи. Пока места хватает, заголовок и действия стоят в
 * ряд: длинный заголовок обрезается, лишние действия переносятся внутри своей группы справа. Уже
 * `@2xl` (672 px) — действия уходят строкой под заголовок.
 *
 * Шапку выбранного в list-detail объекта рисует `DetailHeader` с `DetailTitle` (`h2`), не этот компонент.
 *
 * На телефоне (issue #1434) заголовок — дверь к соседним страницам раздела: подпанели там нет, и
 * «Счета на оплату ▾» раскрывает меню раздела, а над заголовком стоит имя раздела. Заголовок-контрол
 * (выбор представления) своё нажатие уже занял — меню раздела тогда открывает кнопка рядом.
 */
export interface PageHeaderProps {
  title: ReactNode;
  subtitle?: ReactNode;
  icon?: ReactNode;
  badge?: ReactNode;
  /** Хлебные крошки над заголовком (страницы уровней: стройка, раздел, комплект). */
  breadcrumb?: ReactNode;
  actions?: ReactNode;
}

export function PageHeader({ title, subtitle, icon, badge, breadcrumb, actions, bar = false, className = '' }: PageHeaderProps & {
  bar?: boolean;
  className?: string;
}) {
  const plain = typeof title === 'string';
  const section = useSectionMenu();
  // Подсказка о меню — описанием кнопки, а не частью имени: заголовок в списке заголовков диктора
  // звучит названием страницы.
  const hint = useId();
  const heading = section && plain ? (<>
    <h1 className="text-xl font-semibold text-on-surface min-w-0">
      <SectionMenu value={section}>
        <button type="button" data-testid="section-menu-trigger" aria-describedby={hint}
          className="flex items-center gap-1 max-w-[calc(100%+0.5rem)] min-h-11 touch-target [--_touch-inset:-2px] -ml-2 px-2 rounded-lg text-left state-layer focus-ring data-[state=open]:bg-secondary-container data-[state=open]:text-on-secondary-container">
          <span className="truncate">{title}</span>
          <Icon icon={KeyboardArrowDown} size={20} className="shrink-0 text-on-surface-variant" aria-hidden />
        </button>
      </SectionMenu>
    </h1>
    {/* Вне заголовка: текст внутри него стал бы частью имени. */}
    <span id={hint} className="sr-only">Другие страницы раздела «{section.def.title}»</span>
  </>) : (
    <Tooltip content={plain ? title : undefined}>
      <h1 className={`text-xl font-semibold text-on-surface min-w-0 ${plain ? 'truncate' : ''}`}>
        {title}
      </h1>
    </Tooltip>
  );
  return (
    <header className={`@container ${bar ? 'shrink-0 px-6 py-3 border-b border-outline-variant ' : ''}${className}`}>
      <div className="flex flex-col gap-2 @2xl:flex-row @2xl:items-center @2xl:justify-between @2xl:gap-4">
        <div className="min-w-0 @2xl:flex-1 @2xl:min-w-48">
          {breadcrumb && <div className="mb-0.5">{breadcrumb}</div>}
          {section && !breadcrumb && <div data-testid="section-caption" className="text-body-small text-on-surface-variant">{section.def.title}</div>}
          <div className="flex items-center gap-2 min-w-0">
            {icon && <span className="flex text-on-surface-variant shrink-0">{icon}</span>}
            {heading}
            {section && !plain && (
              <SectionMenu value={section}>
                {/* Свой значок: у заголовка-контрола уже есть «▾», и второй такой же рядом читался бы
                    повтором той же стрелки (ревью PR #1488). */}
                <IconButton label={`Другие страницы раздела «${section.def.title}»`} data-testid="section-menu-trigger" icon={ViewList} />
              </SectionMenu>
            )}
            {badge && <div className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0">{badge}</div>}
          </div>
          {subtitle && <div className="text-body-small text-on-surface-variant mt-0.5">{subtitle}</div>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2 min-w-0 @2xl:justify-end">{actions}</div>}
      </div>
    </header>
  );
}
