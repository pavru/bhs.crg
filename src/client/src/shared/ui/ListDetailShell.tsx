import { useState, type ReactNode } from 'react';
import { Search, ChevronRight } from 'lucide-react';
import { Button } from './Button';

/**
 * Тонкий layout-примитив «list-detail со вторичной навигацией» (issue #210, разбор с Архитектором+
 * Дизайнером). Владеет ТОЛЬКО рамками (шапка страницы, колонка навигации фикс. ширины 320px, область
 * detail) и Save-кластером в шапке detail. Модель навигации (табы/группы/список) и содержимое detail —
 * per-page слоты; реестр агрегации dirty — отдельный модуль (features/settings/typeEditorShell).
 * НЕ клиент этого shell: редактор документа (#192) — там навигация ВНУТРИ сущности + preview-панель.
 */
export function ListDetailShell({ title, subtitle, titleIcon, breadcrumb, headerAction, overlay, nav, navWidth = 'w-80', detail }: {
  title: string;
  subtitle?: string;
  /** Иконка уровня слева от заголовка (scope-страницы: стройка/раздел/комплект). */
  titleIcon?: ReactNode;
  /** Хлебные крошки над заголовком (scope-страницы). */
  breadcrumb?: ReactNode;
  headerAction?: ReactNode;
  /** Полноэкранное состояние вместо сплита (загрузка / пустая коллекция). */
  overlay?: ReactNode;
  /** Содержимое левой колонки (табы/поиск/список) — per-page. Рамку/ширину задаёт shell. */
  nav: ReactNode;
  /** Ширина левой колонки (Tailwind-класс): по умолчанию w-80 (320px); w-64 (260) для вырожденных. */
  navWidth?: string;
  /** Detail-панель или EmptyState «выберите …». */
  detail: ReactNode;
}) {
  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center justify-between gap-3 px-6 py-3 shrink-0 border-b border-stroke">
        <div className="min-w-0">
          {breadcrumb && <div className="mb-0.5">{breadcrumb}</div>}
          <div className="flex items-center gap-2 min-w-0">
            {titleIcon && <span className="text-fg3 shrink-0">{titleIcon}</span>}
            <h1 className="text-xl font-semibold text-fg1 truncate">{title}</h1>
          </div>
          {subtitle && <p className="text-xs text-fg3 mt-0.5">{subtitle}</p>}
        </div>
        {headerAction}
      </div>
      {overlay ?? (
        <div className="flex-1 min-h-0 flex">
          <nav aria-label={title} className={`${navWidth} shrink-0 border-r border-stroke flex flex-col bg-base`}>
            {nav}
          </nav>
          {detail}
        </div>
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
      <div className="relative">
        <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
        <input value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} aria-label="Поиск"
          className="w-full h-10 pl-9 pr-3 rounded-full text-sm bg-surface border border-stroke-strong text-fg1 outline-none focus-visible:ring-2 focus-visible:ring-brand placeholder:text-fg4" />
      </div>
    </div>
  );
}

/** Микрозаголовок секции левой навигации scope-страницы («КОМПЛЕКТЫ», «ЭТОТ РАЗДЕЛ», …). */
export function NavSection({ label }: { label: string }) {
  return <div className="px-3 pt-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-fg4">{label}</div>;
}

/**
 * Пилюля левой навигации scope-страницы (issue #210, ось видимости). Два рода (аффорданс кодирует scope):
 * `chevron` — «ребёнок» (уводит вглубь/роут): счётчик перед chevron, НИКОГДА не подсвечивается;
 * без `chevron` — ресурс/контент уровня (меняет detail на месте): тональный бейдж-счётчик, подсветка при active.
 */
export function NavItem({ icon, label, count, active, chevron, onClick }: {
  icon: ReactNode; label: string; count?: number; active?: boolean; chevron?: boolean; onClick: () => void;
}) {
  const highlight = active && !chevron;
  return (
    <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2.5 px-3 h-11 rounded-full text-left transition-colors ${
        highlight ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
      <span className={`shrink-0 ${highlight ? 'text-brand-hover' : 'text-fg4'}`}>{icon}</span>
      <span className="flex-1 truncate text-sm">{label}</span>
      {chevron ? (
        <>
          {count != null && <span className="text-xs text-fg4 shrink-0">{count}</span>}
          <ChevronRight size={14} className="shrink-0 text-fg4" />
        </>
      ) : (
        count != null && count > 0 && (
          <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 ${highlight ? 'bg-white/50 text-brand-hover' : 'bg-brand-subtle text-brand'}`}>{count}</span>
        )
      )}
    </button>
  );
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

/**
 * Гард несохранённых изменений при смене выбранного элемента. Generic по ключу выбора (`string` у типов
 * документов, `{mode,id}` у типов полей). Возвращает `request(next)` для перехвата выбора и `dialogProps`
 * для `LeaveGuardDialog`. `onCommit` применяет переход (страница владеет своим selectedKey).
 */
export function useDirtyGuard<TKey>({ isDirty, saving, saveAll, onCommit }: {
  isDirty: boolean; saving: boolean; saveAll: () => Promise<void>; onCommit: (next: TKey) => void;
}) {
  const [pending, setPending] = useState<{ next: TKey } | null>(null);
  const request = (next: TKey) => { if (isDirty) setPending({ next }); else onCommit(next); };
  const dialogProps = {
    open: pending !== null,
    saving,
    onCancel: () => setPending(null),
    onDiscard: () => { if (pending) onCommit(pending.next); setPending(null); },
    onSave: async () => {
      try { await saveAll(); if (pending) onCommit(pending.next); setPending(null); }
      catch { /* ошибка валидации показана в форме — остаёмся */ }
    },
  };
  return { request, dialogProps };
}
