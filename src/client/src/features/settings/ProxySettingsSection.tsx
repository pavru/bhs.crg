import { useState } from 'react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import {
  useIntegrationSettings, useSaveProxy, useTestProxy,
  type IntegrationSettingsDto, type ProxyTestResult,
} from '@/shared/api/integrationSettings';
import { useUpdateStatus } from '@/shared/api/updates';

/** Тот же вид, что у сервера (ProxySettings.TryParseUrl): схема и хост без регистра, без косой в конце. */
function normalizeUrl(value: string): string {
  return value.trim().replace(/\/+$/, '').toLowerCase();
}

/** Кто сейчас ходит через прокси — по СОХРАНЁННЫМ галкам, как и сервер. */
function servicesViaProxy(d: IntegrationSettingsDto, updatesViaProxy: boolean): string[] {
  const names: string[] = [];
  const rec: Record<string, string> = { Gemini: 'Gemini', Anthropic: 'Anthropic', Ollama: 'Ollama' };
  const web: Record<string, string> = { Serper: 'Serper', Yandex: 'Яндекс' };
  for (const [k, label] of Object.entries(rec)) if (d.recognition[k]?.useProxy) names.push(label);
  for (const [k, label] of Object.entries(web)) if (d.webSearch[k]?.useProxy) names.push(label);
  if (d.externalLinksUseProxy) names.push('загрузка по внешним ссылкам');
  if (d.smtp.useProxy) names.push('почта');
  if (updatesViaProxy) names.push('проверка обновлений');
  if (d.github.useProxy) names.push('передача в GitHub');
  return names;
}

/**
 * Прокси для внешних сервисов (issue #936).
 *
 * Прокси один, а пользуется им только сервис с галкой «через прокси» в своей секции. Не «весь трафик
 * через прокси» намеренно: рядом с системой работают хранилище, Ollama и плагины, и прокси, не знающий
 * их имён, сделал бы их «недоступными».
 */
export function ProxySettingsSection() {
  const { data, isLoading } = useIntegrationSettings();
  const { data: updates } = useUpdateStatus(true, true);
  const save = useSaveProxy();
  const test = useTestProxy();
  const toast = useToast();

  const saved = data?.proxy;
  const [url, setUrl] = useState<string | null>(null);
  const [user, setUser] = useState<string | null>(null);
  const [password, setPassword] = useState('');
  const [service, setService] = useState<string | null>(null);
  const [result, setResult] = useState<ProxyTestResult | null>(null);

  // Значение формы = правка пользователя, иначе сохранённое (как в секции GitHub): эффектом не
  // синхронизируем, иначе ответ сервера затирал бы ввод на полуслове.
  const urlValue = url ?? saved?.url ?? '';
  const userValue = user ?? saved?.user ?? '';

  // Сервер наследует пароль только тем же прокси и логином — предупреждаем ДО сохранения, а не
  // неработающим прокси после.
  const passwordWillDrop = !!saved?.hasPassword && !password.trim()
    && (normalizeUrl(urlValue) !== normalizeUrl(saved?.url ?? '') || userValue.trim() !== (saved?.user ?? '').trim());

  const via = data ? servicesViaProxy(data, !!updates?.useProxy) : [];
  const checkable = saved?.checkable ?? [];

  async function submit() {
    try {
      await save.mutateAsync({
        url: urlValue.trim() || null,
        user: userValue.trim() || null,
        password: password.trim() || undefined,
      });
      setUrl(null); setUser(null); setPassword('');
      setResult(null);
      toast.success(urlValue.trim() ? 'Прокси сохранён.' : 'Прокси убран: все сервисы ходят напрямую.');
    } catch (e) {
      toast.apiError(e, 'Не удалось сохранить прокси.');
    }
  }

  // Проверка идёт по значениям ФОРМЫ, а не по сохранённым: проверять до сохранения — обычный порядок,
  // и иначе кнопка молча проверяла бы прежний прокси (как и «Проверить подключение» у почты).
  async function check() {
    setResult(null);
    try {
      setResult(await test.mutateAsync({
        url: urlValue.trim() || null,
        user: userValue.trim() || null,
        password: password.trim() || undefined,
        service: service ?? checkable[0]?.service ?? null,
      }));
    } catch (e) {
      toast.apiError(e, 'Не удалось выполнить проверку.');
    }
  }

  return (
    <CollapsibleSection title="Прокси" storageKey="proxy" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Прокси для внешних сервисов: распознавания, веб-поиска, почты, проверки обновлений и GitHub.
        Через него ходят только сервисы с галкой «Через прокси» в своих настройках — остальные, в том
        числе хранилище и плагины, подключаются напрямую.
      </p>

      <div className="grid gap-3 sm:grid-cols-2">
        <TextField containerClassName="sm:col-span-2" label="Адрес прокси" value={urlValue}
          disabled={isLoading || save.isPending} onChange={e => setUrl(e.target.value)}
          hint="http://proxy.example:3128, socks5://proxy.example:1080 (также socks4, socks4a). Пусто — прокси нет." />
        <TextField label="Пользователь" value={userValue} autoComplete="off"
          disabled={isLoading || save.isPending} onChange={e => setUser(e.target.value)}
          hint="Если прокси требует входа" />
        <TextField label={saved?.hasPassword ? 'Новый пароль (оставьте пустым — прежний)' : 'Пароль'}
          type="password" value={password} autoComplete="off"
          disabled={isLoading || save.isPending} onChange={e => setPassword(e.target.value)} />
      </div>

      {passwordWillDrop && (
        <p className="text-xs text-warning">
          Адрес прокси или пользователь меняется, а поле пароля пусто — сохранённый пароль будет удалён:
          на другой прокси он не отправляется. Введите пароль для нового.
        </p>
      )}

      {data && saved?.url && (
        <p className="text-xs text-fg3">
          {via.length > 0
            ? <>Через прокси: {via.join(', ')}.</>
            : <span className="text-warning">Прокси задан, но ни у одного сервиса не отмечена галка «Через прокси» — все ходят напрямую.</span>}
        </p>
      )}

      <p className="text-xs text-fg4">
        Загрузка по внешним ссылкам через прокси: адрес по-прежнему проверяется системой, но прокси
        разрешает имя сам и видит свою сеть, поэтому запрет на внутренние адреса должен быть настроен
        и на самом прокси.
      </p>

      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="outlined" size="sm" loading={save.isPending}
          disabled={isLoading} onClick={() => void submit()}>
          Сохранить
        </Button>
        {checkable.length > 1 && (
          <select className="border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
            value={service ?? checkable[0].service} disabled={test.isPending}
            onChange={e => setService(e.target.value)}>
            {checkable.map(c => <option key={c.service} value={c.service}>{c.name}</option>)}
          </select>
        )}
        <Button type="button" variant="outlined" size="sm" loading={test.isPending}
          disabled={isLoading || !urlValue.trim()} onClick={() => void check()}>
          Проверить через прокси
        </Button>
      </div>

      {result && (
        <p className={`text-xs ${result.ok ? 'text-success' : 'text-danger'}`}>
          {result.message}
          {result.target && <span className="block text-fg4">Проверяли на {result.target}, {result.ms} мс.</span>}
        </p>
      )}
    </CollapsibleSection>
  );
}
