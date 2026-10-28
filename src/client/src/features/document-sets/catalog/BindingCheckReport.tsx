import type { BindingCheckItem } from '@/shared/api/commonData';

// Отчёт «Проверить связки» (issue #99): статус каждого @@ref-поля.
const CHECK_STATUS: Record<string, { label: string; cls: string }> = {
  matched: { label: 'связано', cls: 'bg-success-container text-success border-success/30' },
  'not-found': { label: 'не найдено', cls: 'bg-warning-container text-warning border-warning-border' },
  dangling: { label: 'запись удалена', cls: 'bg-error-container text-error border-error/30' },
  drift: { label: 'устарело', cls: 'bg-warning-container text-warning border-warning-border' },
  stale: { label: 'пересохранить', cls: 'bg-warning-container text-warning border-warning-border' },
  // Цель в архиве (issue #1185) — не потеря и не «не найдено», поэтому не красным и не жёлтым:
  // стоящая связка работает, а чинить нечего, пока запись не понадобится выбрать заново.
  archived: { label: 'в архиве', cls: 'bg-surface-container text-fg3 border-outline-variant' },
  // А это уже есть что чинить (ревью PR #1228): источник называет архивную запись, которой в поле
  // не было, — ссылка не подставлена, и поле не заполняется. Серым оно выглядело бы исправным.
  'archived-skipped': { label: 'в архиве — не подставлено', cls: 'bg-warning-container text-warning border-warning-border' },
  // Резолв не состоялся вовсе (issue #715): поле не заполняется, и молчать об этом на экране
  // проверки нельзя — раньше такие случаи отсеивались вместе со всем уровнем Error.
  error: { label: 'не разрешилось', cls: 'bg-error-container text-error border-error/30' },
};

export function BindingCheckReport({ items }: { items: BindingCheckItem[] }) {
  if (items.length === 0)
    return <p className="text-xs text-fg4 px-1">Ссылочных связок нет — проверять нечего.</p>;
  return (
    <div className="rounded-lg border border-outline-variant divide-y divide-surface-container">
      {items.map(it => {
        const s = CHECK_STATUS[it.status] ?? { label: it.status, cls: 'bg-surface-container text-fg3 border-outline-variant' };
        return (
          <div key={it.fieldKey} className="flex items-start gap-2 px-3 py-2 text-sm">
            <span className="flex-1 min-w-0">
              <span className="font-medium text-on-surface">{it.fieldTitle}</span>
              {it.linkedName && <span className="text-fg4"> → {it.linkedName}</span>}
              {it.detail && <span className="block text-caption text-fg4">{it.detail}</span>}
            </span>
            <span className={`shrink-0 text-caption px-1.5 py-0.5 rounded-full border font-medium ${s.cls}`}>{s.label}</span>
          </div>
        );
      })}
    </div>
  );
}
