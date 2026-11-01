import type { BindingCheckItem } from '@/shared/api/commonData';
import { HistoryIcon, Save, SearchOff, type IconDef } from '@/shared/ui/icons';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';

// Отчёт «Проверить связки» (issue #99): статус каждого @@ref-поля — тон пометки по смыслу ([UI-43]). Разные
// предупреждения различаются знаком, а не одним словом ([UI-74]): «не найдено», «устарело», «пересохранить».
const CHECK_STATUS: Record<string, { label: string; tone: StatusTone; icon?: IconDef }> = {
  matched: { label: 'связано', tone: 'success' },
  'not-found': { label: 'не найдено', tone: 'warning', icon: SearchOff },
  dangling: { label: 'запись удалена', tone: 'error' },
  drift: { label: 'устарело', tone: 'warning', icon: HistoryIcon },
  stale: { label: 'пересохранить', tone: 'warning', icon: Save },
  // Цель в архиве (issue #1185) — не потеря и не «не найдено», поэтому не красным и не жёлтым:
  // стоящая связка работает, а чинить нечего, пока запись не понадобится выбрать заново.
  archived: { label: 'в архиве', tone: 'neutral' },
  // А это уже есть что чинить (ревью PR #1228): источник называет архивную запись, которой в поле
  // не было, — ссылка не подставлена, и поле не заполняется. Серым оно выглядело бы исправным.
  'archived-skipped': { label: 'в архиве — не подставлено', tone: 'warning' },
  // Резолв не состоялся вовсе (issue #715): поле не заполняется, и молчать об этом на экране
  // проверки нельзя — раньше такие случаи отсеивались вместе со всем уровнем Error.
  error: { label: 'не разрешилось', tone: 'error' },
};

export function BindingCheckReport({ items }: { items: BindingCheckItem[] }) {
  if (items.length === 0)
    return <p className="text-body-small text-on-surface-variant px-1">Ссылочных связок нет — проверять нечего.</p>;
  return (
    <div className="rounded-md border border-outline-variant divide-y divide-surface-container">
      {items.map(it => {
        const s = CHECK_STATUS[it.status] ?? { label: it.status, tone: 'neutral' as const };
        return (
          <div key={it.fieldKey} className="flex items-start gap-2 px-3 py-2 text-body-medium">
            <span className="flex-1 min-w-0">
              <span className="font-medium text-on-surface">{it.fieldTitle}</span>
              {it.linkedName && <span className="text-on-surface-variant"> → {it.linkedName}</span>}
              {it.detail && <span className="block text-label-small text-on-surface-variant">{it.detail}</span>}
            </span>
            <StatusText tone={s.tone} icon={s.icon}>{s.label}</StatusText>
          </div>
        );
      })}
    </div>
  );
}
