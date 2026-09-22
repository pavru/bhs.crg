import { useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useMyPermissions } from '@/shared/api/account';

/**
 * «Мои роли и права» — только для чтения (issue #954, ТЗ AUTH-16.6).
 *
 * Зачем в профиле: человек должен видеть, чем объясняется отсутствие раздела, не спрашивая
 * администратора. Вопрос возникает уже после отказа — и до сих пор ответить на него было негде:
 * справочник прав открыт лишь тому, кто управляет пользователями.
 *
 * Права показаны СЛОВАМИ из того же справочника, что и в редакторе ролей: код
 * (`core.reconciliation.run`) не объясняет ничего тому, кто справочник не читал. Код всё же
 * показан рядом, мелким — им человека спрашивают в переписке с администратором.
 *
 * Группы свёрнуты: у администратора прав полсотни, и списком они нечитаемы — раскрывается та,
 * которая понадобилась. Счётчик у заголовка виден всегда, иначе свёрнутая группа не отличается от
 * пустой.
 */
export function MyPermissionsSection() {
  const { data: groups, isLoading, isError } = useMyPermissions();
  const [open, setOpen] = useState<string | null>(null);

  if (isLoading) return <p className="text-xs text-fg4">Загрузка прав…</p>;
  if (isError) return <p className="text-xs text-fg4">Права сейчас не показать — повторите позже.</p>;

  // Ноль прав — это ответ, а не пустота: именно он объясняет, почему не открывается ничего.
  if (!groups || groups.length === 0) {
    return <p className="text-sm text-fg4">Прав нет — разделы не открываются. Права выдаёт администратор.</p>;
  }

  return (
    <div className="space-y-1">
      {groups.map(group => {
        const expanded = open === group.module;
        return (
          <div key={group.module} className="rounded-md border border-stroke">
            <button type="button" onClick={() => setOpen(expanded ? null : group.module)}
              aria-expanded={expanded}
              className="w-full flex items-center gap-2 px-3 py-2 text-left text-sm text-fg1 rounded-md
                         transition-colors hover:bg-base focus-visible:outline-none
                         focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand">
              <ChevronRight size={16}
                className={`text-fg3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} />
              <span className="flex-1">{group.title}</span>
              <span className="text-xs text-fg4 tabular-nums">{group.permissions.length}</span>
            </button>

            {expanded && (
              <ul className="px-3 pb-3 pt-1 space-y-2 border-t border-stroke">
                {group.permissions.map(p => (
                  <li key={p.code}>
                    <div className="text-sm text-fg1">{p.gives}</div>
                    <div className="text-xs text-fg3">{p.opens}</div>
                    <code className="text-[11px] text-fg4">{p.code}</code>
                  </li>
                ))}
              </ul>
            )}
          </div>
        );
      })}
    </div>
  );
}
