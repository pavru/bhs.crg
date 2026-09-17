import { useIntegrationSettings } from '@/shared/api/integrationSettings';

/**
 * Галка «через прокси» у внешнего сервиса (issue #936).
 *
 * Одна на все секции, чтобы подпись и предупреждение не разъехались. Предупреждение — когда галка
 * стоит, а прокси не задан: сервер в этом случае ходит напрямую, и без подсказки галка обещала бы
 * то, чего не происходит.
 */
export function ProxyToggle({ checked, onChange, disabled }: {
  checked: boolean;
  onChange: (next: boolean) => void;
  disabled?: boolean;
}) {
  const { data } = useIntegrationSettings();
  const proxyUrl = data?.proxy.url?.trim();
  return (
    <label className="flex items-start gap-2 text-sm text-fg2">
      <input type="checkbox" className="mt-0.5 accent-brand" checked={checked} disabled={disabled}
        onChange={e => onChange(e.target.checked)} />
      <span>
        Через прокси
        <span className={`block text-xs ${checked && !proxyUrl ? 'text-warning' : 'text-fg4'}`}>
          {proxyUrl
            ? proxyUrl
            : checked
              ? 'Прокси не задан — запросы идут напрямую. Задайте его в разделе «Прокси».'
              : 'Прокси не задан'}
        </span>
      </span>
    </label>
  );
}
