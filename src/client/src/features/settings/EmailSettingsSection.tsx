import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Cancel, CheckCircle, Mail, Power, Send, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Checkbox } from '@/shared/ui/Checkbox';
import { useServerForm } from '@/shared/hooks/useServerForm';
import {
  useIntegrationSettings, useSaveSmtp, useTestEmail, useTestSmtpConnection, useEmailUserStatus,
  type SmtpUpdate, type IntegrationSettingsDto,
} from '@/shared/api/integrationSettings';
import { CollapsibleSection } from './CollapsibleSection';
import { apiError } from '@/shared/utils/apiError';
import { ProxyToggle } from './ProxyToggle';

const EMPTY: SmtpUpdate = { enabled: false, host: '', port: 587, user: '', password: '', from: '', fromName: '', useSsl: true, useProxy: false };

/** Настройки SMTP → форма. Пароль всегда пуст: сервер его не отдаёт, а пустой означает «не менять». */
function smtpForm(settings: IntegrationSettingsDto | undefined): SmtpUpdate {
  if (!settings) return EMPTY;
  const s = settings.smtp;
  return {
    enabled: s.enabled, host: s.host ?? '', port: s.port, user: s.user ?? '',
    password: '', from: s.from ?? '', fromName: s.fromName ?? '', useSsl: s.useSsl, useProxy: s.useProxy,
  };
}

export function EmailSettingsSection() {
  const { data: settings } = useIntegrationSettings();
  const saveSmtp = useSaveSmtp();
  const testEmail = useTestEmail();
  const testConn = useTestSmtpConnection();
  const { data: userStatus = [] } = useEmailUserStatus();
  const [connResult, setConnResult] = useState<{ ok: boolean; error?: string } | null>(null);

  // Форма поверх загруженных настроек (issue #858); пароль с сервера не приходит — только признак
  // hasPassword, поэтому он и остаётся производным от ответа, а не отдельным состоянием.
  const [form, setForm] = useServerForm(settings, smtpForm);
  const hasPassword = !!settings?.smtp.hasPassword;
  const [saved, setSaved] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [testTo, setTestTo] = useState('');
  const [testResult, setTestResult] = useState<{ ok: boolean; error?: string } | null>(null);

  function set<K extends keyof SmtpUpdate>(key: K, value: SmtpUpdate[K]) {
    setForm(f => ({ ...f, [key]: value }));
    setSaved(false);
    setConnResult(null);
  }

  async function handleTestConnection() {
    setConnResult(null);
    try { setConnResult(await testConn.mutateAsync({ ...form, password: form.password || undefined })); }
    catch (e) { setConnResult({ ok: false, error: apiError(e, 'Не удалось проверить подключение.') }); }
  }

  async function handleSave() {
    setSaveError('');
    try {
      await saveSmtp.mutateAsync({ ...form, password: form.password || undefined });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) { setSaveError(apiError(e, 'Не удалось сохранить.')); }
  }

  async function handleTest() {
    setTestResult(null);
    try { setTestResult(await testEmail.mutateAsync(testTo.trim())); }
    catch (e) { setTestResult({ ok: false, error: apiError(e, 'Не удалось отправить.') }); }
  }

  const fieldCls = "w-full border border-outline rounded-sm px-3 py-2 text-body-medium focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface";

  return (
    <CollapsibleSection title="Почта (SMTP)" storageKey="email" defaultOpen={false}>
      <p className="text-body-small text-on-surface-variant">
        Настройки исходящей почты для отправки уведомлений и документов подписчикам.
      </p>

      <Checkbox checked={form.enabled} onChange={v => set('enabled', v)} label="Включить отправку почты" />

      <div className="grid grid-cols-2 gap-3">
        <TextField containerClassName="col-span-2 sm:col-span-1" label="SMTP-сервер (host)"
          value={form.host ?? ''} onChange={e => set('host', e.target.value)} hint="smtp.example.com" />
        <TextField label="Порт" type="number" value={form.port} onChange={e => set('port', Number(e.target.value))} />
        <TextField label="Пользователь" value={form.user ?? ''} onChange={e => set('user', e.target.value)}
          hint="user@example.com" />
        <TextField label="Пароль" type="password" value={form.password ?? ''} onChange={e => set('password', e.target.value)}
          hint={hasPassword ? '•••••• (не менять)' : undefined} />
        <TextField label="Адрес отправителя (From)" value={form.from ?? ''} onChange={e => set('from', e.target.value)}
          hint="docs@example.com" />
        <TextField containerClassName="col-span-2 sm:col-span-1" label="Имя отправителя"
          value={form.fromName ?? ''} onChange={e => set('fromName', e.target.value)} hint="BHS.CRG" />
      </div>

      <Checkbox checked={form.useSsl} onChange={v => set('useSsl', v)} label="Шифрование (STARTTLS/SSL — порт 587/465)" />

      <ProxyToggle checked={form.useProxy} onChange={v => set('useProxy', v)} />

      <div className="flex items-center gap-3 flex-wrap">
        <Button variant="filled" onClick={handleSave} loading={saveSmtp.isPending}>
          {saveSmtp.isPending ? 'Сохранение...' : 'Сохранить'}
        </Button>
        <Button variant="outlined" onClick={handleTestConnection} disabled={testConn.isPending || !form.host}
          title="Проверить соединение и аутентификацию по введённым значениям (письмо не отправляется)"
          icon={<Icon icon={Power} size={18} />}>
          {testConn.isPending ? 'Проверка...' : 'Проверить подключение'}
        </Button>
        {saved && <span className="text-body-medium text-success">Сохранено</span>}
        {saveError && <span className="text-body-medium text-error flex items-center gap-1"><Icon icon={Cancel} size={18} /> {saveError}</span>}
        {connResult && (connResult.ok
          ? <span className="text-body-medium text-success flex items-center gap-1"><Icon icon={CheckCircle} size={18} /> Подключение успешно</span>
          : <span className="text-body-medium text-error flex items-center gap-1"><Icon icon={Cancel} size={18} /> {connResult.error}</span>
        )}
      </div>

      {/* Тест-отправка */}
      <div className="border-t border-surface-container pt-3 space-y-2">
        <p className="text-body-medium font-medium text-on-surface-variant flex items-center gap-1.5"><Icon icon={Send} size={18} className="text-on-surface-variant" /> Тест-отправка</p>
        <div className="flex items-center gap-2">
          <input className={fieldCls + ' flex-1'} value={testTo} onChange={e => setTestTo(e.target.value)}
            placeholder="Адрес для проверки" />
          <button type="button" onClick={handleTest} disabled={testEmail.isPending || !testTo.trim()}
            className="px-3 py-2 text-body-medium border border-outline state-layer rounded-sm shrink-0 disabled-outlined">
            {testEmail.isPending ? 'Отправка...' : 'Отправить'}
          </button>
        </div>
        {testResult && (testResult.ok
          ? <p className="text-body-small text-success flex items-center gap-1"><Icon icon={CheckCircle} size={18} /> Письмо отправлено</p>
          : <p className="text-body-small text-error flex items-start gap-1"><Icon icon={Cancel} size={18} className="shrink-0 mt-0.5" /> {testResult.error}</p>
        )}
      </div>

      {/* Проверка email пользователей */}
      <div className="border-t border-surface-container pt-3 space-y-1.5">
        <p className="text-body-medium font-medium text-on-surface-variant flex items-center gap-1.5"><Icon icon={Mail} size={18} className="text-on-surface-variant" /> Email пользователей</p>
        {userStatus.length === 0 ? (
          <p className="text-body-small text-on-surface-variant">Нет пользователей</p>
        ) : (
          <div className="rounded-sm border border-outline-variant divide-y divide-surface-container">
            {userStatus.map((u, i) => (
              <div key={i} className="flex items-center gap-2 px-2.5 py-1.5 text-body-medium">
                <span className="text-on-surface flex-1 min-w-0 truncate">{u.displayName}</span>
                <span className="text-body-small text-on-surface-variant min-w-0 truncate">{u.email || '—'}</span>
                {u.valid
                  ? <Icon icon={CheckCircle} size={18} className="text-success shrink-0" />
                  : <Icon icon={Warning} size={18} className="text-warning shrink-0" />}
              </div>
            ))}
          </div>
        )}
        <p className="text-label-small text-on-surface-variant">⚠ — email не задан или невалиден: такой пользователь не получит писем.</p>
      </div>
    </CollapsibleSection>
  );
}
