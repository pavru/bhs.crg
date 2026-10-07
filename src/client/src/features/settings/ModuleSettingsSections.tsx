import { useState } from 'react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import {
  useModuleSettings, useSaveModuleSettings, type ModuleSettings,
} from '@/shared/api/moduleSettings';
import {
  boundsHint, changedValues, changeWarnings, fieldErrors, fieldText, localRefusals, sentence, staleStored,
  type SettingsDraft,
} from './moduleSettings';

/**
 * Настройки модулей на странице «Настройки» (issue #1070): по секции на включённый модуль, у
 * которого настройки есть. Модуль без настроек секции не получает — пустая секция была бы вопросом
 * «а где?»; сервер такие модули и не присылает.
 */
export function ModuleSettingsSections() {
  const { data, isError, refetch } = useModuleSettings();

  // Пока ответа нет, секций нет вовсе: нарисовать поля с умолчаниями вместо неполученных значений
  // значило бы показать «1,00 ₽» там, где сохранено другое.
  if (isError)
    return (
      <div className="border border-stroke rounded-xl p-4 flex items-center gap-3">
        <span className="text-sm text-danger">Настройки модулей не загрузились.</span>
        <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
      </div>
    );

  return <>{data?.map(module => <ModuleSection key={module.code} module={module} />)}</>;
}

function ModuleSection({ module }: { module: ModuleSettings }) {
  const save = useSaveModuleSettings();
  const toast = useToast();
  const [draft, setDraft] = useState<SettingsDraft>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState(false);
  const [leave, setLeave] = useState<(() => void) | null>(null);
  // Переход, отложенный до ответа на предупреждение: «сохранить и уйти» вопрос о следствиях не обходит.
  const [afterConfirm, setAfterConfirm] = useState<(() => void) | null>(null);

  const values = changedValues(module.settings, draft);
  const dirty = Object.keys(values).length > 0;
  const warnings = changeWarnings(module.settings, values);

  useLeaveGuard(dirty, proceed => setLeave(() => proceed));

  function edit(key: string, typed: string | null) {
    setDraft(d => ({ ...d, [key]: typed }));
    setErrors(({ [key]: _, ...rest }) => rest);
  }

  /** Записать. Отказ у поля встаёт под своим полем; набранное остаётся — исправить, а не набирать заново. */
  async function submit(): Promise<boolean> {
    try {
      await save.mutateAsync({ code: module.code, values });
      setDraft({});
      setErrors({});
      toast.success('Настройки сохранены.');
      return true;
    } catch (e) {
      setErrors(fieldErrors(e));
      toast.apiError(e, 'Не удалось сохранить настройки.');
      return false;
    }
  }

  /** Настройка, действующая на записанные данные, сохраняется через подтверждение. */
  function requestSave(): boolean {
    const refused = localRefusals(module.settings, values);
    if (Object.keys(refused).length > 0) { setErrors(refused); return false; }
    if (warnings.length > 0) setConfirming(true);
    else void submit();
    return true;
  }

  return (
    <CollapsibleSection title={`Модуль «${module.title}»`} storageKey={`module.${module.code}`} defaultOpen={false}
      right={dirty ? <span className="text-xs text-warning normal-case">не сохранено</span> : undefined}>
      {module.settings.map(setting => {
        const stale = staleStored(setting);
        const reset = draft[setting.key] === null;
        return (
          <div key={setting.key}>
            <TextField
              containerClassName="max-w-xs"
              label={setting.title}
              inputMode="decimal"
              value={fieldText(setting, draft)}
              onChange={e => edit(setting.key, e.target.value)}
              trailing={setting.unit ? <span className="text-sm text-fg3">{setting.unit}</span> : undefined}
              error={errors[setting.key] ? sentence(errors[setting.key]) : undefined}
              hint={boundsHint(setting)}
            />
            <p className="text-xs text-fg3 mt-1.5 max-w-xl">{setting.effect}</p>
            {stale && <p className="text-xs text-warning mt-1 max-w-xl">{stale}</p>}
            {/* «Вернуть» — только когда есть что возвращать: у несохранённой настройки умолчание и так действует. */}
            {(setting.stored !== null || setting.stale !== null) && !reset && (
              <Button variant="text" size="sm" className="mt-1 -ml-2" onClick={() => edit(setting.key, null)}>
                Вернуть значение по умолчанию
              </Button>
            )}
          </div>
        );
      })}

      <div className="flex items-center gap-3">
        <Button variant="filled" onClick={requestSave} disabled={!dirty || save.isPending}>
          Сохранить
        </Button>
        {dirty && (
          <Button variant="text" onClick={() => { setDraft({}); setErrors({}); }} disabled={save.isPending}>
            Отменить
          </Button>
        )}
      </div>

      <ConfirmDialog
        open={confirming}
        onOpenChange={o => { if (!o) { setConfirming(false); setAfterConfirm(null); } }}
        title="Изменить настройку?"
        description={
          <div className="space-y-3">
            {warnings.map(w => (
              <div key={w.key}>
                <p className="font-medium text-fg1">{w.title}: {w.from} → {w.to}</p>
                <p className="mt-1">{w.warning}</p>
              </div>
            ))}
            <p>Смена будет записана в журнал действий.</p>
          </div>
        }
        confirmLabel="Изменить"
        errorTitle="Настройка не сохранена"
        onConfirm={async () => {
          // Отказ показан под полем и тостом — диалог закрываем в обоих исходах; уходим только
          // если записалось.
          const proceed = afterConfirm;
          const saved = await submit();
          setConfirming(false);
          setAfterConfirm(null);
          if (saved) proceed?.();
        }}
      />

      <LeaveGuardDialog
        open={leave !== null} saving={save.isPending}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const proceed = leave; setLeave(null); setDraft({}); proceed?.(); }}
        onSave={async () => {
          // Не сохранилось — остаёмся, причина под полем. Настройка с предупреждением и здесь
          // идёт через него: уход со страницы — не повод записать следствия молча.
          const proceed = leave;
          setLeave(null);
          const refused = localRefusals(module.settings, values);
          if (Object.keys(refused).length > 0) setErrors(refused);
          else if (warnings.length > 0) { setAfterConfirm(() => proceed); setConfirming(true); }
          else if (await submit()) proceed?.();
        }} />
    </CollapsibleSection>
  );
}
