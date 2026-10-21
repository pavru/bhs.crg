import { useMemo, useState } from 'react';
import { AlertTriangle, XCircle } from 'lucide-react';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { Button } from '@/shared/ui/Button';
import { apiError } from '@/shared/utils/apiError';
import { useSaveBackupSchedule } from '@/shared/api/backup';
import { useCompanyTimeZone } from '@/shared/api/companyTimeZone';
import type { BackupScheduleSettings, BackupScheduleStatus } from '@/shared/api/types';
import { formatDateTime, formatDayTime } from '@/shared/format/format';
import { backupZoneHint } from './companySettings';

/** Значение как есть — когда серверный кусок и есть форма. */
const asIs = <T,>(x: T): T => x;

/**
 * Расписание планового копирования (issue #832).
 *
 * Включено по умолчанию, и это главное решение здесь: копия, которую забыли настроить, — самый
 * частый способ потерять данные, поэтому установка, где администратор не сделал ничего, всё равно
 * защищена. Выключатель на месте — но выключать приходится осознанно, а не включать.
 *
 * «Последняя плановая» показывается не потому, что данные есть, а потому, что без неё галка
 * «включено» неопровержима: она выглядит одинаково и когда копии снимаются, и когда они полгода
 * падают на нехватке места.
 */

export function ScheduleForm({ status }: { status: BackupScheduleStatus }) {
  const save = useSaveBackupSchedule();
  /**
   * Ответ сервера — источник истины: расписание могли поменять из другой вкладки, и форма,
   * оставшаяся на своём, показывала бы не то, чем система живёт. Правка живёт поверх ответа и
   * отбрасывается сама, когда придёт другой (issue #858).
   *
   * <p>Поверх ТРЁХ ПОЛЕЙ расписания, а не всего `status`: в нём же едут `running`, `lastRunAt` и
   * прочий след службы, а запрос опрашивается каждые 15 секунд и каждые 3 во время снятия копии.
   * Ключом по всему объекту любой опрос выбрасывал бы недописанную правку — человек меняет время
   * и число копий, нажав до этого «Создать копию», и поля молча возвращаются к сохранённым
   * (поймано ревью PR #864).</p>
   */
  const schedule = useMemo<BackupScheduleSettings>(
    () => ({ enabled: status.enabled, timeOfDay: status.timeOfDay, keepCount: status.keepCount }),
    [status.enabled, status.timeOfDay, status.keepCount],
  );
  const [form, setForm] = useServerForm(schedule, asIs);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const zone = useCompanyTimeZone().data;

  const dirty = form.enabled !== status.enabled
    || form.timeOfDay !== status.timeOfDay
    || form.keepCount !== status.keepCount;

  async function submit() {
    setError('');
    try {
      await save.mutateAsync(form);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(apiError(e, 'Не удалось сохранить расписание.'));
    }
  }

  return (
    <div className="rounded-md border border-muted p-3 space-y-2">
      <label className="flex items-start gap-2 text-sm text-fg2">
        <input type="checkbox" className="mt-0.5" checked={form.enabled}
          onChange={e => setForm({ ...form, enabled: e.target.checked })} />
        <span>
          Снимать копию автоматически
          <span className="block text-xs text-fg4">
            Раз в сутки. Пропущенный запуск (сервер был выключен) выполняется при следующем старте;
            за неделю простоя снимается одна копия, а не семь.
          </span>
        </span>
      </label>

      <div className="flex flex-wrap items-end gap-3">
        <label className="text-xs text-fg3">
          <span className="block mb-1">Время копии</span>
          <input type="time" value={form.timeOfDay} disabled={!form.enabled}
            onChange={e => setForm({ ...form, timeOfDay: e.target.value })}
            className="h-8 px-2 rounded-md border border-stroke bg-surface text-sm text-fg1 disabled:opacity-50" />
        </label>
        <label className="text-xs text-fg3">
          <span className="block mb-1">Хранить плановых</span>
          <input type="number" min={1} max={1000} value={form.keepCount} disabled={!form.enabled}
            onChange={e => setForm({ ...form, keepCount: Number(e.target.value) })}
            className="h-8 w-20 px-2 rounded-md border border-stroke bg-surface text-sm text-fg1 disabled:opacity-50" />
        </label>
        <Button variant="outlined" onClick={submit} disabled={!dirty || save.isPending}>
          {save.isPending ? 'Сохранение…' : 'Сохранить'}
        </Button>
        {saved && <span className="text-xs text-success">Сохранено</span>}
      </div>
      {zone && (
        <p className="text-xs text-fg4">
          {backupZoneHint(zone)}. Пояс меняется в разделе «Компания».
        </p>
      )}

      <p className="text-xs text-fg4">
        Уборка старых копий касается <b>только плановых</b>: снятые вручную и принесённые с другого
        сервера остаются, пока их не удалят руками.
      </p>

      {error && (
        <p className="text-xs text-danger flex items-start gap-1">
          <XCircle size={13} className="shrink-0 mt-px" /> <span>{error}</span>
        </p>
      )}

      {status.lastError ? (
        <p className="text-xs text-danger flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Последняя плановая копия не удалась
            {status.lastErrorAt && <> ({formatDayTime(status.lastErrorAt)})</>}: {status.lastError}
          </span>
        </p>
      ) : status.lastSuccessAt ? (
        <p className="text-xs text-fg4">
          Последняя плановая копия: {formatDateTime(status.lastSuccessAt)}
          {status.lastFileName && <> · {status.lastFileName}</>}
        </p>
      ) : (
        <p className="text-xs text-fg4">Плановых копий ещё не было.</p>
      )}
    </div>
  );
}
