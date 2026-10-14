import { useState } from 'react';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { useWorkspace } from '@/shared/ui/workspaceContext';

const LAST = 'last';

/**
 * Пространство по умолчанию (ТЗ AUTH-16.5, AUTH-16.6; issue #1001) — с него начинается работа после
 * входа. То же умолчание, что галка «Открывать при входе» в окне пространств: два места, один смысл.
 * Третий вариант — «Открывать последнее выбранное»: иначе заданное умолчание нечем было бы снять.
 *
 * <p>Секции нет, когда нет переключателя (один доступный модуль): выбирать не из чего.</p>
 */
export function DefaultWorkspaceSection() {
  const ws = useWorkspace();
  const toast = useToast();
  const [saving, setSaving] = useState(false);

  if (!ws.shown) return null;

  // Умолчание, которого больше нет в списке (удалённое своё, модуль стал недоступен), названо
  // словами, а не подменено «последним выбранным»: иначе профиль показывал бы не то, что хранится.
  const known = ws.defaultChoice === null || ws.list.some(w => w.key === ws.defaultChoice);

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
    <div className="max-w-md" data-testid="default-workspace">
      <Select label="Открывать при входе" value={ws.defaultChoice ?? LAST} disabled={saving}
        hint={known ? undefined : 'Выбранное пространство недоступно — при входе откроются все разделы'}
        onValueChange={v => void change(v)}>
        <SelectItem value={LAST}>Последнее выбранное</SelectItem>
        {ws.list.map(w => <SelectItem key={w.key} value={w.key}>{w.name}</SelectItem>)}
        {!known && ws.defaultChoice && <SelectItem value={ws.defaultChoice}>Недоступное пространство</SelectItem>}
      </Select>
    </div>
  );
}
