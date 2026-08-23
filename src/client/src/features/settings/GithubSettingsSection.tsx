import { useState } from 'react';
import { Check, ExternalLink } from 'lucide-react';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { useIntegrationSettings, useSaveGithubSettings } from '@/shared/api/integrationSettings';

/**
 * Передача сообщений об ошибках в GitHub (issue #834, часть 2).
 *
 * Своя секция, а не строка в «Интеграциях»: там внешние службы, которые работают САМИ (распознают,
 * ищут, шлют почту), а здесь — право публиковать наружу текст, и включает его человек нажатием на
 * конкретном сообщении.
 *
 * Выключателя нет намеренно: выключатель — сам токен. Флаг «включено» без токена означал бы кнопку,
 * которая обещает работу и отказывает при нажатии.
 */
export function GithubSettingsSection() {
  const { data, isLoading } = useIntegrationSettings();
  const save = useSaveGithubSettings();
  const toast = useToast();

  const [repository, setRepository] = useState<string | null>(null);
  const [token, setToken] = useState('');

  const saved = data?.github;
  // Значение формы = правка пользователя, иначе сохранённое. Эффектом не синхронизируем: ввод
  // затирался бы ответом сервера ровно в момент набора.
  const repositoryValue = repository ?? saved?.repository ?? '';
  const repositoryChanged = saved != null && repositoryValue.trim() !== saved.repository;

  async function submit() {
    try {
      await save.mutateAsync({ repository: repositoryValue.trim(), token: token.trim() || undefined });
      setToken('');
      setRepository(null);
      toast.success('Настройки GitHub сохранены.');
    } catch (e) {
      toast.apiError(e, 'Не удалось сохранить настройки GitHub.');
    }
  }

  return (
    <CollapsibleSection title="Передача в GitHub" storageKey="github" defaultOpen={false}>
      <p className="text-xs text-fg3">
        Токен нужен только для кнопки «Отправить в GitHub» на экране сообщений об ошибках. Сама
        система в GitHub ничего не отправляет: issue заводит администратор, из текста, который он
        отредактировал.
      </p>

      <div className="grid gap-3 sm:grid-cols-2">
        <TextField
          label="Репозиторий"
          value={repositoryValue}
          disabled={isLoading || save.isPending}
          onChange={e => setRepository(e.target.value)}
          hint="Вид «владелец/репозиторий»"
        />
        <TextField
          label={saved?.hasToken ? 'Новый токен (оставьте пустым — прежний)' : 'Токен'}
          type="password"
          value={token}
          autoComplete="off"
          disabled={isLoading || save.isPending}
          onChange={e => setToken(e.target.value)}
          hint="Fine-grained PAT с правом issues: write на этот репозиторий"
        />
      </div>

      {saved?.hasToken && (
        <p className="flex items-center gap-1.5 text-xs text-success">
          <Check size={13} /> Токен задан
        </p>
      )}

      {/*
        Предупреждение ДО сохранения, а не отказ после: сервер обнуляет сохранённый токен при смене
        репозитория намеренно (иначе форма была бы способом отправить внутренний текст в чужое место
        нашим правом записи), и человек должен узнать об этом заранее, а не по неработающей кнопке.
      */}
      {repositoryChanged && saved?.hasToken && !token.trim() && (
        <p className="text-xs text-warning">
          Репозиторий меняется, а поле токена пусто — сохранённый токен будет удалён: он выдан для
          прежнего репозитория. Введите токен для нового.
        </p>
      )}

      <div className="flex items-center gap-3 flex-wrap">
        <Button type="button" variant="outlined" size="sm" loading={save.isPending}
          disabled={isLoading || !repositoryValue.trim()}
          onClick={() => void submit()}>
          Сохранить
        </Button>
        <a
          href="https://github.com/settings/personal-access-tokens"
          target="_blank" rel="noreferrer"
          className="inline-flex items-center gap-1 text-xs text-brand hover:underline"
        >
          Где выпустить токен <ExternalLink size={12} />
        </a>
      </div>
    </CollapsibleSection>
  );
}
