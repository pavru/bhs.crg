import { useState } from 'react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Switch } from '@/shared/ui/Switch';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import {
  useModuleSettings, useModuleSettingsImpact, useSaveModuleSettings, type ModuleSettings,
} from '@/shared/api/moduleSettings';
import { ModuleMappingSignal } from './ModuleMappingSignal';
import {
  boundsHint, changedValues, changeWarnings, fieldErrors, fieldText, flagOn, impactLine, localRefusals, mappingNeedsAttention,
  sentence, staleStored,
  unitOf,
  type SettingsDraft,
} from './moduleSettings';

/**
 * Настройки модулей на странице «Настройки» (issue #1070): по секции на включённый модуль, у
 * которого есть настройки или потребности в справочниках ядра (issue #1101). Модуль без того и
 * другого секции не получает — пустая секция была бы вопросом «а где?»; сервер такие и не присылает.
 */
export function ModuleSettingsSections() {
  const { data, isError, refetch } = useModuleSettings();

  // Пока ответа нет, секций нет вовсе: нарисовать поля с умолчаниями вместо неполученных значений
  // значило бы показать «1,00 ₽» там, где сохранено другое.
  if (isError)
    return (
      <div className="border border-outline-variant rounded-lg p-4 flex items-center gap-3">
        <span className="text-body-medium text-error">Настройки модулей не загрузились.</span>
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
  // Числа к предупреждению спрашиваются при открытии окна подтверждения: считать их на каждую набранную
  // цифру незачем, а на момент вопроса они должны быть свежими.
  const impact = useModuleSettingsImpact();

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

  /**
   * Открыть подтверждение и спросить числа у модулей, которые умеют их назвать. Отказ сервера по полю
   * (знаков после запятой больше, чем примет запись, — их считает только сервер) закрывает окно и встаёт
   * под полем, как местный: подтверждать значение, которое запись всё равно отвергнет, незачем, а причина
   * уже в руках (ревью PR #1381).
   */
  function askConfirm(proceed: (() => void) | null) {
    setAfterConfirm(() => proceed);
    setConfirming(true);
    if (!warnings.some(w => w.impact)) return;
    impact.mutate({ code: module.code, values }, {
      onError: e => {
        const refused = fieldErrors(e);
        if (Object.keys(refused).length > 0) { setErrors(refused); closeConfirm(); }
      },
    });
  }

  /** Окно закрыто — ответ о числах забыт: следующее открытие спросит заново, а не покажет прошлое. */
  function closeConfirm() {
    setConfirming(false);
    setAfterConfirm(null);
    impact.reset();
  }

  /** Настройка, действующая на записанные данные, сохраняется через подтверждение. */
  function requestSave(): boolean {
    const refused = localRefusals(module.settings, values);
    if (Object.keys(refused).length > 0) { setErrors(refused); return false; }
    if (warnings.length > 0) askConfirm(null);
    else void submit();
    return true;
  }

  return (
    <CollapsibleSection title={`Модуль «${module.title}»`} storageKey={`module.${module.code}`}
      defaultOpen={mappingNeedsAttention(module.mapping)}
      right={dirty ? <span className="text-body-small text-warning normal-case">не сохранено</span>
        : mappingNeedsAttention(module.mapping)
          ? <span className="text-body-small text-warning normal-case">требует внимания</span> : undefined}>
      {module.mapping && <ModuleMappingSignal signal={module.mapping} />}
      {module.settings.map(setting => {
        const stale = staleStored(setting);
        const reset = draft[setting.key] === null;
        return (
          <div key={setting.key}>
            {/* Флаг — переключателем с подписью (issue #1080): поле ввода для «true» звало бы набирать
                слово, которое сервер примет только одно из двух. */}
            {setting.kind === 'flag' ? (
              <div className="max-w-xl">
                <label className="flex items-center gap-3 text-body-medium text-on-surface">
                  <Switch checked={flagOn(setting, draft)} label={setting.title}
                    onChange={on => edit(setting.key, on ? 'true' : 'false')} />
                  {setting.title}
                </label>
                <p className={`text-body-small mt-1 ${errors[setting.key] ? 'text-error' : 'text-on-surface-variant'}`}>
                  {errors[setting.key] ? sentence(errors[setting.key]) : boundsHint(setting)}
                </p>
              </div>
            ) : (
              <TextField
                containerClassName="max-w-xs"
                label={setting.title}
                inputMode="decimal"
                value={fieldText(setting, draft)}
                onChange={e => edit(setting.key, e.target.value)}
                trailing={unitOf(setting) ? <span className="text-body-medium text-on-surface-variant">{unitOf(setting)}</span> : undefined}
                error={errors[setting.key] ? sentence(errors[setting.key]) : undefined}
                hint={boundsHint(setting)}
              />
            )}
            <p className="text-body-small text-on-surface-variant mt-1.5 max-w-xl">{setting.effect}</p>
            {stale && <p className="text-body-small text-warning mt-1 max-w-xl">{stale}</p>}
            {/* «Вернуть» — только когда есть что возвращать: у несохранённой настройки умолчание и так действует. */}
            {(setting.stored !== null || setting.stale !== null) && !reset && (
              <Button variant="text" size="sm" className="mt-1 -ml-2" onClick={() => edit(setting.key, null)}>
                Вернуть значение по умолчанию
              </Button>
            )}
          </div>
        );
      })}

      {/* У модуля без настроек сохранять нечего: секция пришла ради строки о справочниках. */}
      {module.settings.length > 0 && (
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
      )}

      <ConfirmDialog
        open={confirming}
        onOpenChange={o => { if (!o) closeConfirm(); }}
        title="Изменить настройку?"
        description={
          <div className="space-y-3">
            {warnings.map(w => {
              const line = impactLine(w, impact);
              return (
                <div key={w.key}>
                  <p className="font-medium text-on-surface">{w.title}: {w.from} → {w.to}</p>
                  <p className="mt-1">{w.warning}</p>
                  {line && (
                    <p className={`mt-2 ${line.tone === 'told' ? 'text-on-surface' : line.tone === 'refused' ? 'text-error' : 'text-on-surface-variant'}`}>
                      {line.text}
                    </p>
                  )}
                </div>
              );
            })}
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
          closeConfirm();
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
          else if (warnings.length > 0) askConfirm(proceed);
          else if (await submit()) proceed?.();
        }} />
    </CollapsibleSection>
  );
}
