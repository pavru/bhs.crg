import type { ReactNode } from 'react';

/**
 * Заголовок страницы (issue #1352). До него на 26 страниц приходилось восемь вариантов `h1`, и
 * действия, подзаголовок и счётчик каждый экран раскладывал сам.
 *
 * - `title` — строка или узел. Узел нужен, когда заголовок сам по себе контрол (выбор представления
 *   таблицы): он всё равно остаётся внутри `h1`.
 * - `icon` — ведущий узел слева: значок раздела или кнопка «назад».
 * - `badge` — показатель состояния рядом с заголовком (готовность по плану): не в `actions`, иначе
 *   цифра среди кнопок читается как ещё одна кнопка.
 * - `subtitle` — одна-две строки пояснения. Длинный текст о том, как устроен раздел, — содержимое
 *   страницы, а не её шапка.
 * - `actions` — справа; filled-кнопка среди них не больше одной (правило `Button`). На узком экране
 *   уходят под заголовок, а не сжимают его.
 * - `bar` — шапка полосой над разделённой страницей (list-detail, редакторы): поля и черта снизу. Без
 *   него шапка стоит в потоке содержимого, и отступ вокруг задаёт страница.
 *
 * Шапку выбранного в list-detail объекта рисует `DetailHeader` (заголовок `h2`), не этот компонент.
 */
export function PageHeader({ title, subtitle, icon, badge, breadcrumb, actions, bar = false, className = '' }: {
  title: ReactNode;
  subtitle?: ReactNode;
  icon?: ReactNode;
  badge?: ReactNode;
  /** Хлебные крошки над заголовком (страницы уровней: стройка, раздел, комплект). */
  breadcrumb?: ReactNode;
  actions?: ReactNode;
  bar?: boolean;
  className?: string;
}) {
  return (
    <header className={`flex flex-wrap items-center justify-between gap-x-4 gap-y-2 ` +
      `${bar ? 'shrink-0 px-6 py-3 border-b border-stroke ' : ''}${className}`}>
      <div className="min-w-0">
        {breadcrumb && <div className="mb-0.5">{breadcrumb}</div>}
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0">
          {icon && <span className="flex text-fg3 shrink-0">{icon}</span>}
          <h1 className={`text-xl font-semibold text-fg1 min-w-0 max-w-full ${typeof title === 'string' ? 'truncate' : ''}`}>
            {title}
          </h1>
          {badge}
        </div>
        {subtitle && <div className="text-xs text-fg3 mt-0.5">{subtitle}</div>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
