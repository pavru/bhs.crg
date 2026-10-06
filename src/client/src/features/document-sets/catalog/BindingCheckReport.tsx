import type { BindingCheckItem } from '@/shared/api/commonData';

// Отчёт «Проверить связки» (issue #99): статус каждого @@ref-поля.
const CHECK_STATUS: Record<string, { label: string; cls: string }> = {
  matched: { label: 'связано', cls: 'bg-green-50 text-green-700 border-green-200' },
  'not-found': { label: 'не найдено', cls: 'bg-warning-subtle text-warning border-warning-border' },
  dangling: { label: 'запись удалена', cls: 'bg-red-50 text-danger border-red-200' },
  drift: { label: 'устарело', cls: 'bg-warning-subtle text-warning border-warning-border' },
  stale: { label: 'пересохранить', cls: 'bg-warning-subtle text-warning border-warning-border' },
  // Цель в архиве (issue #1185) — не потеря и не «не найдено», поэтому не красным и не жёлтым:
  // стоящая связка работает, а чинить нечего, пока запись не понадобится выбрать заново.
  archived: { label: 'в архиве', cls: 'bg-muted text-fg3 border-stroke' },
  // Резолв не состоялся вовсе (issue #715): поле не заполняется, и молчать об этом на экране
  // проверки нельзя — раньше такие случаи отсеивались вместе со всем уровнем Error.
  error: { label: 'не разрешилось', cls: 'bg-red-50 text-danger border-red-200' },
};

export function BindingCheckReport({ items }: { items: BindingCheckItem[] }) {
  if (items.length === 0)
    return <p className="text-xs text-fg4 px-1">Ссылочных связок нет — проверять нечего.</p>;
  return (
    <div className="rounded-lg border border-stroke divide-y divide-muted">
      {items.map(it => {
        const s = CHECK_STATUS[it.status] ?? { label: it.status, cls: 'bg-muted text-fg3 border-stroke' };
        return (
          <div key={it.fieldKey} className="flex items-start gap-2 px-3 py-2 text-sm">
            <span className="flex-1 min-w-0">
              <span className="font-medium text-fg1">{it.fieldTitle}</span>
              {it.linkedName && <span className="text-fg4"> → {it.linkedName}</span>}
              {it.detail && <span className="block text-[11px] text-fg4">{it.detail}</span>}
            </span>
            <span className={`shrink-0 text-[11px] px-1.5 py-0.5 rounded-full border font-medium ${s.cls}`}>{s.label}</span>
          </div>
        );
      })}
    </div>
  );
}
