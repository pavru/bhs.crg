import { useState, type ReactNode } from 'react';
import { Search } from 'lucide-react';
import { Button } from './Button';

/**
 * Тонкий layout-примитив «list-detail со вторичной навигацией» (issue #210, разбор с Архитектором+
 * Дизайнером). Владеет ТОЛЬКО рамками (шапка страницы, колонка навигации фикс. ширины 320px, область
 * detail) и Save-кластером в шапке detail. Модель навигации (табы/группы/список) и содержимое detail —
 * per-page слоты; реестр агрегации dirty — отдельный модуль (features/settings/typeEditorShell).
 * НЕ клиент этого shell: редактор документа (#192) — там навигация ВНУТРИ сущности + preview-панель.
 */
export function ListDetailShell({ title, subtitle, headerAction, overlay, nav, detail }: {
  title: string;
  subtitle?: string;
  headerAction?: ReactNode;
  /** Полноэкранное состояние вместо сплита (загрузка / пустая коллекция). */
  overlay?: ReactNode;
  /** Содержимое левой колонки (табы/поиск/список) — per-page. Рамку/ширину задаёт shell. */
  nav: ReactNode;
  /** Detail-панель или EmptyState «выберите …». */
  detail: ReactNode;
}) {
  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center justify-between gap-3 px-6 py-3 shrink-0 border-b border-stroke">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold text-fg1">{title}</h1>
          {subtitle && <p className="text-xs text-fg3 mt-0.5">{subtitle}</p>}
        </div>
        {headerAction}
      </div>
      {overlay ?? (
        <div className="flex-1 min-h-0 flex">
          <nav aria-label={title} className="w-80 shrink-0 border-r border-stroke flex flex-col bg-base">
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
