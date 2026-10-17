import type { ReactNode } from 'react';
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
  return (
    <header className={`@container ${bar ? 'shrink-0 px-6 py-3 border-b border-stroke ' : ''}${className}`}>
      <div className="flex flex-col gap-2 @2xl:flex-row @2xl:items-center @2xl:justify-between @2xl:gap-4">
        <div className="min-w-0 @2xl:flex-1 @2xl:min-w-48">
          {breadcrumb && <div className="mb-0.5">{breadcrumb}</div>}
          <div className="flex items-center gap-2 min-w-0">
            {icon && <span className="flex text-fg3 shrink-0">{icon}</span>}
            <Tooltip content={plain ? title : undefined}>
              <h1 className={`text-xl font-semibold text-fg1 min-w-0 ${plain ? 'truncate' : ''}`}>
                {title}
              </h1>
            </Tooltip>
            {badge && <div className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0">{badge}</div>}
          </div>
          {subtitle && <div className="text-xs text-fg3 mt-0.5">{subtitle}</div>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2 min-w-0 @2xl:justify-end">{actions}</div>}
      </div>
    </header>
  );
}
