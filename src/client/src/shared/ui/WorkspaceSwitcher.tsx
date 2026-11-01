import { Icon } from './Icon';
import { GridView, KeyboardArrowDown, Settings } from './icons';
import { Menu, MenuItem, MenuLabel, MenuRadioGroup, MenuRadioItem, MenuSeparator } from './Menu';
import { useState } from 'react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { WorkspacesDialog } from '@/features/account/WorkspacesDialog';
import { useWorkspace } from './workspaceContext';
import { compositionLabel, type Workspace } from './workspaces';
import { Tooltip } from './Tooltip';

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
    <MenuRadioItem key={w.key} value={w.key} data-testid="workspace-option"
      description={composition(w) && <span data-testid="workspace-composition">{composition(w)}</span>}>
      {w.name}
    </MenuRadioItem>
  );

  return (
    <>
      <Menu className="min-w-64" trigger={
        <button type="button" data-testid="workspace-switcher" aria-label={`Рабочее пространство: ${ws.current.name}`}
          className="flex items-center gap-1.5 min-w-0 max-w-60 h-9 touch-target [--_touch-inset:-6px] pl-3 pr-2 rounded-full text-on-surface state-layer focus-ring">
          <Icon icon={GridView} size={18} className="shrink-0 text-on-surface-variant" />
          <Tooltip content={composition(ws.current) ?? ws.current.name}>
            <span className="min-w-0 truncate text-body-medium font-medium text-on-surface">{ws.current.name}</span>
          </Tooltip>
          <Icon icon={KeyboardArrowDown} size={18} className="shrink-0 text-on-surface-variant" />
        </button>
      }>
        <MenuRadioGroup value={ws.current.key} onValueChange={ws.choose}>
          <MenuLabel>Системные</MenuLabel>
          {system.map(item)}
          {own.length > 0 && <>
            <MenuSeparator />
            <MenuLabel>Мои</MenuLabel>
            {own.map(item)}
          </>}
        </MenuRadioGroup>
        <MenuSeparator />
        {/* Создание — отсюда, последним пунктом (AUTH-16.5): нужда осознаётся при переключении. */}
        <MenuItem icon={Settings} onSelect={() => setSetupOpen(true)}>Настроить пространства…</MenuItem>
      </Menu>
      {setupOpen && <WorkspacesDialog onClose={() => setSetupOpen(false)} />}
    </>
  );
}
