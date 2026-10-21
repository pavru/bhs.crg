import { useMemo, useState } from 'react';
import { AlertTriangle, XCircle } from 'lucide-react';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { TextField } from '@/shared/ui/TextField';
import { TimeField } from '@/shared/ui/TimeField';
import { apiError } from '@/shared/utils/apiError';
import { useSaveBackupSchedule } from '@/shared/api/backup';
import { useCompanyTimeZone } from '@/shared/api/companyTimeZone';
import type { BackupScheduleSettings, BackupScheduleStatus } from '@/shared/api/types';
import { formatDateTime, formatDayTime } from '@/shared/format/format';
import { backupZoneHint, nextBackupText } from './companySettings';

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
 *
 * Поля — слоем формы по макету #1468 (вариант А): время сегментами ЧЧ:ММ с поясом в подписи,
 * «Хранить плановых» той же рамкой с единицей «копий», флажок с описанием.
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
  /**
   * Набранное время — не время (недописано, 25:00), и наружу оно не ушло. Кнопка при нём доступна и
   * называет причину отказа: иначе «Сохранить» записала бы прежнее время молча, а недоступная кнопка
   * не сказала бы почему.
   */
  const [timeProblem, setTimeProblem] = useState<string | null>(null);
  const zone = useCompanyTimeZone().data;

  const dirty = form.enabled !== status.enabled
    || form.timeOfDay !== status.timeOfDay
    || form.keepCount !== status.keepCount;
  // Ближайшая — у сохранённого расписания: при неотправленной правке она говорила бы о том, чего
  // служба делать не будет.
  const nextRun = !dirty && status.nextRunAt ? nextBackupText(status.nextRunAt) : null;
  // У выключенного расписания время не действует — и сохранению не мешает.
  const blocked = form.enabled ? timeProblem : null;

  async function submit() {
    setError('');
    if (blocked) {
      setError(`${blocked} — расписание не сохранено.`);
      return;
    }
    try {
      await save.mutateAsync(form);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(apiError(e, 'Не удалось сохранить расписание.'));
    }
  }

  return (
    <div className="rounded-md border border-muted p-3 space-y-3">
      <Checkbox checked={form.enabled} onChange={enabled => setForm({ ...form, enabled })}
        label="Снимать копию автоматически"
        description={'Раз в сутки. Пропущенный запуск (сервер был выключен) выполняется при следующем старте; '
          + 'за неделю простоя снимается одна копия, а не семь.'} />

      {/* Выключенное расписание — disabled, а не readOnly: значение не действует, а не «кладёт не
          человек» (макет #1468, 0a). Подпись пояса при этом не гаснет. */}
      <div className="flex flex-col sm:flex-row sm:flex-wrap sm:items-start gap-3 pt-1">
        <TimeField label="Время копии" required value={form.timeOfDay} disabled={!form.enabled}
          onChange={timeOfDay => setForm({ ...form, timeOfDay })} onIncomplete={setTimeProblem}
          hint={zone ? backupZoneHint(zone) : undefined} className="w-full sm:w-64" />
        <TextField label="Хранить плановых" type="number" inputMode="numeric" min={1} max={1000}
          value={form.keepCount} disabled={!form.enabled}
          onChange={e => setForm({ ...form, keepCount: Number(e.target.value) })}
          trailing={<span className="text-sm text-fg4">копий</span>}
          containerClassName="w-full sm:w-44" className="tabular-nums" />
        <div className="flex items-center gap-2 sm:h-14">
          <Button variant="outlined" onClick={submit} disabled={!(dirty || blocked) || save.isPending}>
            {save.isPending ? 'Сохранение…' : 'Сохранить'}
          </Button>
          {/* Живая область стоит всегда: появившись вместе с текстом, она осталась бы немой. */}
          <span role="status" className="text-xs text-success">{saved ? 'Сохранено' : ''}</span>
        </div>
      </div>

      {nextRun && <p className="text-xs text-fg2">Ближайшая копия: <b>{nextRun}</b></p>}
      <p className="text-xs text-fg4">
        Пояс меняется в разделе «Компания». Уборка старых копий касается <b>только плановых</b>: снятые
        вручную и принесённые с другого сервера остаются, пока их не удалят руками.
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
