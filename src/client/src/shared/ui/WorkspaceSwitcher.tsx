import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Icon } from './Icon';
import { Check, GridView, KeyboardArrowDown, Settings } from './icons';
import { useState } from 'react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { WorkspacesDialog } from '@/features/account/WorkspacesDialog';
import { useWorkspace } from './workspaceContext';
import { compositionLabel, type Workspace } from './workspaces';
import { Tooltip } from './Tooltip';

const itemCls =
  'flex items-start gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-surface-container-low data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

/**
 * Переключатель рабочего пространства — в шапке слева (ТЗ AUTH-16.1; issue #1001): это контекст
 * экрана, а не действие над учётной записью. На кнопке — только короткое имя (#1432): шапка одна на
 * все экраны, и строка состава в ней спорила за место с поиском. Состав — в списке, у каждого
 * пространства, и в подсказке кнопки: он отвечает на вопрос «почему я не вижу сметы», когда его задают.
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
  const composition = (w: Workspace) => w.kind === 'module' ? null : compositionLabel(w, access);

  const system = ws.list.filter(w => w.kind !== 'own');
  const own = ws.list.filter(w => w.kind === 'own');
  const item = (w: Workspace) => (
    <DropdownMenu.Item key={w.key} className={itemCls} onSelect={() => ws.choose(w.key)}
      data-testid="workspace-option">
      <Icon icon={Check} size={18} className={`mt-0.5 shrink-0 ${w.key === ws.current.key ? 'text-primary' : 'invisible'}`} />
      <span className="min-w-0">
        <span className="block truncate text-on-surface">{w.name}</span>
        {composition(w) && (
          <span className="block text-xs text-fg3" data-testid="workspace-composition">{composition(w)}</span>
        )}
      </span>
    </DropdownMenu.Item>
  );

  return (
    <>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <button type="button" data-testid="workspace-switcher" aria-label={`Рабочее пространство: ${ws.current.name}`}
            className="flex items-center gap-1.5 min-w-0 max-w-60 h-9 max-md:h-11 pl-3 pr-2 rounded-full hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
            <Icon icon={GridView} size={18} className="shrink-0 text-fg3" />
            <Tooltip content={composition(ws.current) ?? ws.current.name}>
              <span className="min-w-0 truncate text-sm font-medium text-on-surface">{ws.current.name}</span>
            </Tooltip>
            <Icon icon={KeyboardArrowDown} size={18} className="shrink-0 text-fg3" />
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content align="start" sideOffset={4}
            className="z-50 min-w-64 max-w-96 max-h-[70vh] overflow-auto rounded-md border border-outline-variant bg-surface py-1"
            style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
            <DropdownMenu.Label className="px-3 pt-1 pb-0.5 text-caption font-semibold uppercase tracking-widest text-fg4">
              Системные
            </DropdownMenu.Label>
            {system.map(item)}
            {own.length > 0 && <>
              <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
              <DropdownMenu.Label className="px-3 pt-1 pb-0.5 text-caption font-semibold uppercase tracking-widest text-fg4">
                Мои
              </DropdownMenu.Label>
              {own.map(item)}
            </>}
            <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
            {/* Создание — отсюда, последним пунктом (AUTH-16.5): нужда осознаётся при переключении. */}
            <DropdownMenu.Item className={itemCls} onSelect={() => setSetupOpen(true)}>
              <Icon icon={Settings} size={18} className="mt-0.5 shrink-0 text-fg3" />
              <span className="text-on-surface">Настроить пространства…</span>
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
      {setupOpen && <WorkspacesDialog onClose={() => setSetupOpen(false)} />}
    </>
  );
}
