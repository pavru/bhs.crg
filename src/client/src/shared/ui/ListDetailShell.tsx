import { useContext, type ReactNode } from 'react';
import { ListDetailViewContext as ViewContext, useListDetailView, type ListDetailView } from './listDetailView';
import { ArrowLeft, ChevronRight } from 'lucide-react';
import { Button } from './Button';
import { PageHeader, type PageHeaderProps } from './PageHeader';
import { SearchInput } from './SearchInput';


/** Свой порядок сплита начинает с карточки — страницы уровня (см. ниже). Постоянная «запись»:
 *  она названа с первого кадра и не меняется, поэтому карточку показывает только `NavItem`. */
const OWN_START = 'own';

/** Ширины колонки на просторе. Классом целиком: Tailwind не соберёт имя, склеенное из частей. */
const NAV_WIDE: Record<string, string> = { 'w-80': '@2xl/ld:w-80', 'w-64': '@2xl/ld:w-64' };

/**
 * Тонкий layout-примитив «list-detail со вторичной навигацией» (issue #210, разбор с Архитектором+
 * Дизайнером). Владеет ТОЛЬКО рамками (шапка страницы, колонка навигации фикс. ширины 320px, область
 * detail) и Save-кластером в шапке detail. Модель навигации (табы/группы/список) и содержимое detail —
 * per-page слоты; реестр агрегации dirty — отдельный модуль (features/settings/typeEditorRegistry).
 * НЕ клиент этого shell: редактор документа (#192) — там навигация ВНУТРИ сущности + preview-панель.
 *
 * Тесно — уже 672 px САМОГО сплита (container query, как у `PageHeader`), а не окна: на ноутбуке с
 * развёрнутой панелью сплит тоже бывает тесным. Тогда колонка и карточка идут по очереди, у карточки
 * — кнопка «назад к списку» (issue #1355). Без `view` от страницы сплит ведёт очередь сам: начинает
 * с карточки, а к ней возвращает пункт `NavItem` — так устроены страницы уровня (стройка, раздел,
 * комплект), где колонка — разделы уровня, а не список записей.
 */
export function ListDetailShell({ overlay, nav, navWidth = 'w-80', detail, view, backLabel, ...header }:
  Omit<PageHeaderProps, 'title'> & {
  /** Строкой, а не узлом: им же названа колонка навигации для диктора. Остальные слоты шапки —
   *  те же, что у `PageHeader` (значок уровня, готовность по плану в `badge` — #796, крошки). */
  title: string;
  /** Полноэкранное состояние вместо сплита (загрузка / пустая коллекция). */
  overlay?: ReactNode;
  /** Содержимое левой колонки (табы/поиск/список) — per-page. Рамку/ширину задаёт shell. */
  nav: ReactNode;
  /** Ширина левой колонки (Tailwind-класс): по умолчанию w-80 (320px); w-64 (260) для вырожденных. */
  navWidth?: 'w-80' | 'w-64';
  /** Detail-панель или EmptyState «выберите …». */
  detail: ReactNode;
  /** Очередь «список / карточка» на узком месте — см. `useListDetailView`. */
  view?: ListDetailView;
  /** Подпись кнопки возврата к колонке на узком месте; по умолчанию — «К списку». */
  backLabel?: string;
}) {
  const own = useListDetailView(OWN_START);
  const current = view ?? own;
  const onDetail = current.view === 'detail';
  return (
    <div className="h-full flex flex-col @container/ld">
      <PageHeader bar {...header} />
      {overlay ?? (
        <ViewContext.Provider value={current}>
          <div className="flex-1 min-h-0 flex">
            <nav aria-label={header.title} data-testid="ld-nav"
              className={`w-full ${NAV_WIDE[navWidth]} shrink-0 @2xl/ld:border-r border-stroke flex flex-col bg-base ${
                onDetail ? '@max-2xl/ld:hidden' : ''}`}>
              {nav}
            </nav>
            <div data-testid="ld-detail" className={`flex-1 min-w-0 min-h-0 flex flex-col ${onDetail ? '' : '@max-2xl/ld:hidden'}`}>
              <button type="button" onClick={current.showList}
                className="@2xl/ld:hidden shrink-0 flex items-center gap-2 h-11 px-4 border-b border-stroke text-sm font-medium text-brand hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand">
                <ArrowLeft size={16} className="shrink-0" />
                {backLabel ?? 'К списку'}
              </button>
              <div className="flex-1 min-h-0 flex">{detail}</div>
            </div>
          </div>
        </ViewContext.Provider>
      )}
    </div>
  );
}

/** Поле поиска в шапке левой колонки — единый вид (issue #210, инвариант Дизайнера). */
export function NavSearchInput({ value, onChange, placeholder = 'Поиск…' }: {
  value: string; onChange: (v: string) => void; placeholder?: string;
}) {
  return (
    <div className="p-3 shrink-0">
      <SearchInput value={value} onChange={onChange} placeholder={placeholder} rounded="full" />
    </div>
  );
}

/** Микрозаголовок секции левой навигации scope-страницы («КОМПЛЕКТЫ», «ЭТОТ РАЗДЕЛ», …). */
export function NavSection({ label }: { label: string }) {
  return <div className="px-3 pt-3 pb-1 text-caption font-semibold uppercase tracking-wide text-fg4">{label}</div>;
}

/**
 * Пилюля левой навигации scope-страницы (issue #210, ось видимости). Два рода (аффорданс кодирует scope):
 * `chevron` — «ребёнок» (уводит вглубь/роут): счётчик перед chevron, НИКОГДА не подсвечивается;
 * без `chevron` — ресурс/контент уровня (меняет detail на месте): тональный бейдж-счётчик, подсветка при active.
 */
export function NavItem({ icon, label, count, active, chevron, filter, onClick, alert, alertDanger, progress, progressTitle }: {
  icon: ReactNode; label: string; count?: number; active?: boolean; chevron?: boolean; onClick: () => void;
  /** Пункт сужает СПИСОК (отбор по состоянию), а не меняет карточку: на узком месте он оставляет
   *  список на экране — показать карточку значило бы спрятать только что отобранное (issue #1355). */
  filter?: boolean;
  /** Готовность по плану, % (#796). null/пусто — плана нет, и рисовать нечего: «0 %» соврал бы.
   *  Тон НЕЙТРАЛЬНЫЙ: красный на этих экранах занят арифметикой сверки, и второй красный читался
   *  бы как ошибка там, где просто не всё готово. */
  progress?: number | null;
  /** Расшифровка процента для подсказки: сколько закрыто и сколько комплектов в счёт не вошли.
   *  В пилюле помещается только цифра, а цифра без оговорки «без плана: N» противоречит шапке
   *  уровня, где оговорка есть. */
  progressTitle?: string;
  /** Сколько проблем ждёт разбора ниже по иерархии. Ноль/пусто — маркер не рисуется: он существует,
   *  чтобы отличать «есть что разобрать» от «пусто», а нарисованный ноль этой задачи не решает. */
  alert?: number;
  /** Расхождение, посчитанное СИСТЕМОЙ. Красный зарезервирован за арифметикой: двести утверждений
   *  агента — не то же самое, что одно расхождение в числах. */
  alertDanger?: boolean;
}) {
  const highlight = active && !chevron;
  // Пункт уровня меняет карточку на месте — на узком месте её и надо показать (issue #1355).
  // Ребёнок уводит на другой адрес, отбор меняет список: им карточка ни при чём.
  const view = useContext(ViewContext);
  return (
    <button type="button" onClick={() => { if (!chevron && !filter) view?.showDetail(); onClick(); }} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
        highlight ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
      <span className={`shrink-0 ${highlight ? 'text-brand-hover' : 'text-fg4'}`}>{icon}</span>
      <span className="flex-1 truncate text-sm">{label}</span>
      {progress != null && (
        <span title={progressTitle ?? `Готовность по плану: ${progress}%`}
          className={`text-caption px-1.5 py-0.5 rounded-full shrink-0 tabular-nums ${
            progress === 100 ? 'bg-brand-subtle text-brand' : 'bg-muted text-fg3'}`}>
          {progress}%
        </span>
      )}
      {alert != null && alert > 0 && (
        <span title={`Требует разбора: ${alert}`}
          className={`text-caption px-1.5 py-0.5 rounded-full shrink-0 ${
            alertDanger ? 'bg-danger-subtle text-danger' : 'bg-warning-subtle text-warning'}`}>
          {alert > 99 ? '99+' : alert}
        </span>
      )}
      {chevron ? (
        <>
          {count != null && <span className="text-xs text-fg4 shrink-0">{count}</span>}
          <ChevronRight size={14} className="shrink-0 text-fg4" />
        </>
      ) : (
        count != null && count > 0 && (
          <span className={`text-caption px-1.5 py-0.5 rounded-full shrink-0 ${highlight ? 'bg-white/50 text-brand-hover' : 'bg-brand-subtle text-brand'}`}>{count}</span>
        )
      )}
    </button>
  );
}

/** Имя выбранного объекта в `DetailHeader` (issue #1352): `h2` — на ступень ниже заголовка страницы,
 *  крупно, но тише его (`font-normal`). Длинное обрезается, полностью — в подсказке. */
export function DetailTitle({ children }: { children: string }) {
  return <h2 className="text-xl font-normal text-fg1 truncate min-w-0" title={children}>{children}</h2>;
}

/** Sticky-шапка detail: слева доменный `heading` (имя+бейджи+код), справа dirty-бейдж + «Сохранить»
 *  (единственный общий концерн) + доменные `actions` (group-picker/delete). Инвариант: «Сохранить»
 *  ТОЛЬКО здесь. */
export function DetailHeader({ heading, dirty, saving, onSaveAll, onRevert, actions }: {
  heading: ReactNode;
  dirty: boolean; saving: boolean; onSaveAll: () => Promise<void>;
  /** Откат несохранённых правок; если задан — показывается кнопка «Отмена» (активна при dirty). */
  onRevert?: () => void;
  actions?: ReactNode;
}) {
  return (
    <div className="shrink-0 px-6 py-4 border-b border-stroke bg-surface">
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">{heading}</div>
        <div className="flex items-center gap-1.5 shrink-0">
          {dirty && <span className="text-xs px-2 py-0.5 rounded-full font-medium bg-warning-subtle text-warning">есть изменения</span>}
          {onRevert && (
            <Button variant="text" size="sm" disabled={!dirty || saving} onClick={onRevert}>Отмена</Button>
          )}
          <Button variant="filled" size="sm" disabled={!dirty} loading={saving}
            onClick={() => { onSaveAll().catch(() => { /* ошибки показаны в формах */ }); }}>
            Сохранить
          </Button>
          {actions}
        </div>
      </div>
    </div>
  );
}
