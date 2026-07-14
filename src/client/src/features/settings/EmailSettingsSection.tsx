import { useState, useEffect } from 'react';
import { Mail, Send, CheckCircle, XCircle, AlertTriangle, PlugZap } from 'lucide-react';
import { TextField } from '@/shared/ui/TextField';
import {
  useIntegrationSettings, useSaveSmtp, useTestEmail, useTestSmtpConnection, useEmailUserStatus,
  type SmtpUpdate,
} from '@/shared/api/integrationSettings';
import { CollapsibleSection } from './CollapsibleSection';
import { apiError } from '@/shared/utils/apiError';

const EMPTY: SmtpUpdate = { enabled: false, host: '', port: 587, user: '', password: '', from: '', fromName: '', useSsl: true };

export function EmailSettingsSection() {
  const { data: settings } = useIntegrationSettings();
  const saveSmtp = useSaveSmtp();
  const testEmail = useTestEmail();
  const testConn = useTestSmtpConnection();
  const { data: userStatus = [] } = useEmailUserStatus();
  const [connResult, setConnResult] = useState<{ ok: boolean; error?: string } | null>(null);

  const [form, setForm] = useState<SmtpUpdate>(EMPTY);
  const [hasPassword, setHasPassword] = useState(false);
  const [saved, setSaved] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [testTo, setTestTo] = useState('');
  const [testResult, setTestResult] = useState<{ ok: boolean; error?: string } | null>(null);

  // Инициализируем форму из загруженных настроек (пароль не приходит — только признак hasPassword).
  useEffect(() => {
    if (!settings) return;
    const s = settings.smtp;
    setForm({
      enabled: s.enabled, host: s.host ?? '', port: s.port, user: s.user ?? '',
      password: '', from: s.from ?? '', fromName: s.fromName ?? '', useSsl: s.useSsl,
    });
    setHasPassword(s.hasPassword);
  }, [settings]);

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

  const fieldCls = "w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface";

  return (
    <CollapsibleSection title="Почта (SMTP)" storageKey="email" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Настройки исходящей почты для отправки уведомлений и документов подписчикам.
      </p>

      <label className="flex items-center gap-2 text-sm text-fg2">
        <input type="checkbox" checked={form.enabled} onChange={e => set('enabled', e.target.checked)} />
        Включить отправку почты
      </label>

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

      <label className="flex items-center gap-2 text-sm text-fg2">
        <input type="checkbox" checked={form.useSsl} onChange={e => set('useSsl', e.target.checked)} />
        Шифрование (STARTTLS/SSL — порт 587/465)
      </label>

      <div className="flex items-center gap-3 flex-wrap">
        <button type="button" onClick={handleSave} disabled={saveSmtp.isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
          {saveSmtp.isPending ? 'Сохранение...' : 'Сохранить'}
        </button>
        <button type="button" onClick={handleTestConnection} disabled={testConn.isPending || !form.host}
          title="Проверить соединение и аутентификацию по введённым значениям (письмо не отправляется)"
          className="flex items-center gap-2 px-3 py-2 text-sm border border-stroke-strong hover:bg-base rounded-md transition-colors disabled:opacity-50">
          <PlugZap size={14} className="text-fg3" />
          {testConn.isPending ? 'Проверка...' : 'Проверить подключение'}
        </button>
        {saved && <span className="text-sm text-success">Сохранено</span>}
        {saveError && <span className="text-sm text-danger flex items-center gap-1"><XCircle size={14} /> {saveError}</span>}
        {connResult && (connResult.ok
          ? <span className="text-sm text-success flex items-center gap-1"><CheckCircle size={14} /> Подключение успешно</span>
          : <span className="text-sm text-danger flex items-center gap-1"><XCircle size={14} /> {connResult.error}</span>
        )}
      </div>

      {/* Тест-отправка */}
      <div className="border-t border-muted pt-3 space-y-2">
        <p className="text-sm font-medium text-fg2 flex items-center gap-1.5"><Send size={14} className="text-fg4" /> Тест-отправка</p>
        <div className="flex items-center gap-2">
          <input className={fieldCls + ' flex-1'} value={testTo} onChange={e => setTestTo(e.target.value)}
            placeholder="Адрес для проверки" />
          <button type="button" onClick={handleTest} disabled={testEmail.isPending || !testTo.trim()}
            className="px-3 py-2 text-sm border border-stroke-strong hover:bg-base rounded-md transition-colors shrink-0 disabled:opacity-50">
            {testEmail.isPending ? 'Отправка...' : 'Отправить'}
          </button>
        </div>
        {testResult && (testResult.ok
          ? <p className="text-xs text-success flex items-center gap-1"><CheckCircle size={13} /> Письмо отправлено</p>
          : <p className="text-xs text-danger flex items-start gap-1"><XCircle size={13} className="shrink-0 mt-0.5" /> {testResult.error}</p>
        )}
      </div>

      {/* Проверка email пользователей */}
      <div className="border-t border-muted pt-3 space-y-1.5">
        <p className="text-sm font-medium text-fg2 flex items-center gap-1.5"><Mail size={14} className="text-fg4" /> Email пользователей</p>
        {userStatus.length === 0 ? (
          <p className="text-xs text-fg4">Нет пользователей</p>
        ) : (
          <div className="rounded-md border border-stroke divide-y divide-muted">
            {userStatus.map((u, i) => (
              <div key={i} className="flex items-center gap-2 px-2.5 py-1.5 text-sm">
                <span className="text-fg1 flex-1 min-w-0 truncate">{u.displayName}</span>
                <span className="text-xs text-fg4 min-w-0 truncate">{u.email || '—'}</span>
                {u.valid
                  ? <CheckCircle size={14} className="text-success shrink-0" />
                  : <AlertTriangle size={14} className="text-warning shrink-0" />}
              </div>
            ))}
          </div>
        )}
        <p className="text-[11px] text-fg4">⚠ — email не задан или невалиден: такой пользователь не получит писем.</p>
      </div>
    </CollapsibleSection>
  );
}
