import { useEffect, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { useUpdateStatus, useCheckUpdatesNow, useSaveUpdateSettings } from '@/shared/api/updates';

function whenText(iso: string | null): string {
  if (!iso) return 'ещё не выполнялась';
  const d = new Date(iso);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  const time = d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return sameDay ? `сегодня в ${time}` : d.toLocaleString('ru-RU');
}

/**
 * Проверка новых версий (issue #813).
 *
 * Своя секция, а не строка внутри «Интеграций»: там внешние сервисы с ключами — распознавание,
 * поиск, почта, — а это про само приложение.
 *
 * «Последняя проверка» показывается не потому, что данные есть, а потому, что без неё выключатель в
 * положении «включено» неопровержим: он выглядит одинаково и когда проверка ходит, и когда она
 * полгода падает на прокси.
 */
export function UpdateSettingsSection() {
  // Здесь заметки нужны — это единственное место, где их показывают.
  const { data, isLoading } = useUpdateStatus(true, true);
  const check = useCheckUpdatesNow();
  const save = useSaveUpdateSettings();
  const [enabled, setEnabled] = useState(true);

  // Исход ПОСЛЕДНЕГО нажатия: сеть могла отработать (200), а проверка не состояться.
  const result = check.data;
  const failed = check.isError || result?.justChecked === false;
  const lastError = check.isError ? 'сервер не ответил' : result?.lastError ?? null;

  useEffect(() => { if (data) setEnabled(data.enabled); }, [data]);

  async function toggle(next: boolean) {
    const prev = enabled;
    setEnabled(next);
    try {
      await save.mutateAsync({ enabled: next });
    } catch {
      // Возвращаем галку на место: иначе она показывает «выключено», служба продолжает ходить в
      // GitHub, и расхождение не исправится до перезагрузки страницы — переключатель врёт о том,
      // чем управляет.
      setEnabled(prev);
    }
  }

  return (
    <CollapsibleSection title="Обновления" storageKey="updates" defaultOpen={false}>
      <label className="flex items-start gap-2 text-sm text-fg2">
        <input type="checkbox" className="mt-0.5" checked={enabled}
          disabled={isLoading || save.isPending}
          onChange={e => void toggle(e.target.checked)} />
        <span>
          Проверять новые версии на GitHub
          <span className="block text-xs text-fg4">
            Раз в шесть часов. Выключено — система не обращается к сети вовсе: для установок без
            интернета это единственный способ не копить в журнале неудачные попытки.
          </span>
        </span>
      </label>

      <div className="flex items-center gap-3 flex-wrap">
        <Button type="button" variant="outlined" size="sm" icon={<RefreshCw size={14} />}
          loading={check.isPending} disabled={!enabled}
          onClick={() => check.mutate()}>
          Проверить сейчас
        </Button>
        {data && (
          <span className="text-xs text-fg3">
            Установлена <b>{data.installed}</b>
            {data.updateAvailable && data.latest
              ? <> · доступна <b className="text-brand">{data.latest}</b></>
              : data.latest ? ' · это последняя версия' : ''}
          </span>
        )}
      </div>

      {save.isError && (
        <p className="text-xs text-danger">Настройку сохранить не удалось — значение осталось прежним.</p>
      )}

      {data && (
        // Молчать о неудачной проверке нельзя: «обновлений нет» и «мы не знаем» — разные вещи, а
        // выглядят одинаково.
        <p className={`text-xs ${data.lastCheckedAt ? 'text-fg4' : 'text-warning'}`}>
          Последняя удачная проверка: {whenText(data.lastCheckedAt)}
        </p>
      )}

      {/* Неудача приходит УСПЕШНЫМ ответом (сервер не падает от того, что GitHub недоступен),
          поэтому смотреть надо на признак в теле, а не на ошибку запроса. Иначе нажатие выглядит
          сработавшим: на экране не меняется ничего — ровно тот отказ, переодетый в результат,
          ради которого кнопка и заведена. */}
      {failed && (
        <p className="text-xs text-warning">
          Проверить не удалось{lastError ? `: ${lastError}` : '.'}
        </p>
      )}

      {data?.updateAvailable && data.releaseUrl && (
        <p className="text-xs text-fg3">
          Что изменилось — на{' '}
          <a href={data.releaseUrl} target="_blank" rel="noopener noreferrer"
            className="text-brand hover:underline">странице выпуска</a>.
          {' '}Обновление выполняется вручную — см. инструкцию по развёртыванию.
        </p>
      )}
    </CollapsibleSection>
  );
}
