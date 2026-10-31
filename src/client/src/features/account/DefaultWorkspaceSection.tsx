import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { GridView } from '@/shared/ui/icons';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { resolveWorkspace } from '@/shared/ui/workspaces';

const LAST = 'last';

/**
 * Пространство по умолчанию (ТЗ AUTH-16.5, AUTH-16.6; issue #1001) — с него начинается работа после
 * входа. То же умолчание, что галка «Открывать при входе» в окне пространств: два места, один смысл.
 * Третий вариант — «Открывать последнее выбранное»: иначе заданное умолчание нечем было бы снять.
 *
 * <p>Секции нет — вместе с заголовком, — когда нет переключателя (один доступный модуль): выбирать не
 * из чего. Решает это одно место, здесь (ревью PR #1330): решай его ещё и профиль, заголовок мог бы
 * остаться над пустотой.</p>
 */
export function DefaultWorkspaceSection() {
  const ws = useWorkspace();
  const { data: access = NO_ACCESS } = useAccess();
  const toast = useSnackbar();
  const [saving, setSaving] = useState(false);

  if (!ws.shown) return null;

  // Откроется ли умолчание при входе — ТЕМ ЖЕ расчётом, что и сам вход (ревью PR #1330). Удалённое
  // своё и своё, у которого все модули стали недоступны, одинаково откатятся на «все»; по одному
  // наличию в списке второе выглядело бы исправным, и профиль показывал бы не то, что произойдёт.
  const known = ws.list.some(w => w.key === ws.defaultChoice);
  const opens = ws.defaultChoice === null
    || resolveWorkspace(access, ws.own, ws.defaultChoice).workspace.key === ws.defaultChoice;

  async function change(value: string) {
    setSaving(true);
    try {
      await ws.setDefault(value === LAST ? null : value);
      toast.success('Пространство по умолчанию сохранено');
    } catch (e) {
      toast.apiError(e, 'Пространство по умолчанию не сохранено');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="mt-8 pt-6 border-t border-outline-variant">
      <h2 className="flex items-center gap-2 text-body-medium font-semibold text-on-surface mb-1">
        <Icon icon={GridView} size={18} className="text-on-surface-variant" /> Рабочее пространство
      </h2>
      <p className="text-body-small text-on-surface-variant mb-4">
        С какого пространства начинать работу. Свои пространства заводятся в переключателе в шапке.
      </p>
      <div className="max-w-md" data-testid="default-workspace">
        <Select label="Открывать при входе" value={ws.defaultChoice ?? LAST} disabled={saving}
          hint={opens ? undefined : known
            ? 'В выбранном пространстве нет доступных вам модулей — при входе откроются все разделы'
            // Удалённое своё — не вопрос доступа (ревью PR #1330): иначе человек шёл бы к администратору.
            : 'Выбранного пространства больше нет — при входе откроются все разделы'}
          onValueChange={v => void change(v)}>
          <SelectItem value={LAST}>Последнее выбранное</SelectItem>
          {ws.list.map(w => <SelectItem key={w.key} value={w.key}>{w.name}</SelectItem>)}
          {!known && ws.defaultChoice && <SelectItem value={ws.defaultChoice}>Недоступное пространство</SelectItem>}
        </Select>
      </div>
    </div>
  );
}
