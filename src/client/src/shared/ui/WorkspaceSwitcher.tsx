import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Check, ChevronDown, LayoutGrid, Settings2 } from 'lucide-react';
import { useState } from 'react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { WorkspacesDialog } from '@/features/account/WorkspacesDialog';
import { useWorkspace } from './workspaceContext';
import { compositionLabel, type Workspace } from './workspaces';

const itemCls =
  'flex items-start gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-base data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

/**
 * Переключатель рабочего пространства — в шапке слева (ТЗ AUTH-16.1; issue #1001): это контекст
 * экрана, а не действие над учётной записью. Рядом с именем — состав: имя не отвечает на вопрос
 * «почему я не вижу сметы», а состав отвечает.
 *
 * <p>Виден, только когда доступно больше одного модуля, — и тогда виден ВСЕГДА, не бледнея и в «Все
 * доступные модули»: иначе вход находится, а выход нет.</p>
 */
export function WorkspaceSwitcher() {
  const ws = useWorkspace();
  const { data: access = NO_ACCESS } = useAccess();
  const [setupOpen, setSetupOpen] = useState(false);

  if (!ws.shown) return null;

  // У системного пространства одного модуля состав повторил бы имя — «Счета и накладные · 1 модуль:
  // Счета и накладные». Строка состава нужна там, где имя её не говорит.
  const composition = ws.current.kind === 'module' ? null : compositionLabel(ws.current, access);

  const system = ws.list.filter(w => w.kind !== 'own');
  const own = ws.list.filter(w => w.kind === 'own');
  const item = (w: Workspace) => (
    <DropdownMenu.Item key={w.key} className={itemCls} onSelect={() => ws.choose(w.key)}
      data-testid="workspace-option">
      <Check size={14} className={`mt-0.5 shrink-0 ${w.key === ws.current.key ? 'text-brand' : 'invisible'}`} />
      <span className="min-w-0">
        <span className="block truncate text-fg1">{w.name}</span>
        {w.kind !== 'module' && <span className="block truncate text-xs text-fg3">{compositionLabel(w, access)}</span>}
      </span>
    </DropdownMenu.Item>
  );

  return (
    <>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <button type="button" data-testid="workspace-switcher" aria-label={`Рабочее пространство: ${ws.current.name}`}
            className="flex items-center gap-2 min-w-0 max-w-[28rem] h-9 px-3 rounded-full text-left hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <LayoutGrid size={16} className="shrink-0 text-fg3" />
            {/* Имя не режется — режется состав, целиком он в подсказке. */}
            <span className="min-w-0 flex items-baseline gap-2" title={composition ?? undefined}>
              <span className="text-sm font-medium text-fg1 whitespace-nowrap">{ws.current.name}</span>
              {composition && (
                <span className="text-xs text-fg3 truncate" data-testid="workspace-composition">{composition}</span>
              )}
            </span>
            <ChevronDown size={14} className="shrink-0 text-fg3" />
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content align="start" sideOffset={4}
            className="z-50 min-w-[16rem] max-w-[24rem] max-h-[70vh] overflow-auto rounded-md border border-stroke bg-surface py-1"
            style={{ boxShadow: 'var(--f-shadow28)' }}>
            <DropdownMenu.Label className="px-3 pt-1 pb-0.5 text-caption font-semibold uppercase tracking-widest text-fg4">
              Системные
            </DropdownMenu.Label>
            {system.map(item)}
            {own.length > 0 && <>
              <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
              <DropdownMenu.Label className="px-3 pt-1 pb-0.5 text-caption font-semibold uppercase tracking-widest text-fg4">
                Мои
              </DropdownMenu.Label>
              {own.map(item)}
            </>}
            <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
            {/* Создание — отсюда, последним пунктом (AUTH-16.5): нужда осознаётся при переключении. */}
            <DropdownMenu.Item className={itemCls} onSelect={() => setSetupOpen(true)}>
              <Settings2 size={14} className="mt-0.5 shrink-0 text-fg3" />
              <span className="text-fg1">Настроить пространства…</span>
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
      {setupOpen && <WorkspacesDialog onClose={() => setSetupOpen(false)} />}
    </>
  );
}
