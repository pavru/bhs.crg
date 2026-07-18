import { useState, useEffect, useRef } from 'react';
import { useAccount, useUpdateAccount, useResendConfirmation, useChangeEmail, useUpdateAvatar } from '@/shared/api/account';
import { TextField } from '@/shared/ui/TextField';
import { Button } from '@/shared/ui/Button';
import { Avatar, downscaleToDataUri } from '@/shared/ui/Avatar';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { apiError } from '@/shared/utils/apiError';
import { KeyRound, CheckCircle2, AlertCircle, Mail, Upload, Trash2 } from 'lucide-react';

/** Профиль текущего пользователя (issue #148): просмотр учётных данных,
 *  редактирование отображаемого имени, смена пароля. Доступно любой роли. */
export function ProfilePage() {
  const { data: account, isLoading } = useAccount();
  const update = useUpdateAccount();
  const updateAvatar = useUpdateAvatar();
  const resend = useResendConfirmation();
  const changeEmail = useChangeEmail();
  const [displayName, setDisplayName] = useState('');
  const [pwOpen, setPwOpen] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState('');
  const [avatarError, setAvatarError] = useState('');
  const avatarInput = useRef<HTMLInputElement>(null);

  const [resendMsg, setResendMsg] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [emailPassword, setEmailPassword] = useState('');
  const [emailMsg, setEmailMsg] = useState('');
  const [emailError, setEmailError] = useState('');

  useEffect(() => { if (account) setDisplayName(account.displayName); }, [account]);

  async function resendConfirmation() {
    setResendMsg('');
    try { await resend.mutateAsync(); setResendMsg('Письмо отправлено — проверьте почту.'); }
    catch (err) { setResendMsg(apiError(err, 'Не удалось отправить письмо.')); }
  }

  async function submitEmail(e: React.FormEvent) {
    e.preventDefault();
    setEmailError(''); setEmailMsg('');
    try {
      await changeEmail.mutateAsync({ newEmail: newEmail.trim(), currentPassword: emailPassword });
      setEmailMsg(`Письмо для подтверждения отправлено на ${newEmail.trim()}. Адрес сменится после перехода по ссылке.`);
      setNewEmail(''); setEmailPassword('');
    } catch (err) {
      setEmailError(apiError(err, 'Не удалось запустить смену email'));
    }
  }

  async function pickAvatar(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setAvatarError('');
    try {
      const dataUri = await downscaleToDataUri(file);
      await updateAvatar.mutateAsync(dataUri);
    } catch (err) {
      setAvatarError(apiError(err, 'Не удалось загрузить изображение'));
    }
  }

  async function removeAvatar() {
    setAvatarError('');
    try { await updateAvatar.mutateAsync(null); }
    catch (err) { setAvatarError(apiError(err, 'Не удалось удалить изображение')); }
  }

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setError(''); setSaved(false);
    try {
      await update.mutateAsync({ displayName: displayName.trim() });
      setSaved(true);
    } catch (err) {
      setError(apiError(err, 'Не удалось сохранить профиль'));
    }
  }

  if (isLoading || !account) {
    return <div className="px-6 py-4 text-sm text-fg4">Загрузка…</div>;
  }

  const dirty = displayName.trim() !== account.displayName;

  return (
    <div className="px-6 py-4 max-w-lg">
      <h1 className="text-xl font-semibold text-fg1 mb-1">Профиль</h1>
      <p className="text-xs text-fg4 mb-5">Ваши учётные данные.</p>

      {/* Аватар (issue #245) */}
      <div className="flex items-center gap-4 mb-6">
        <Avatar src={account.avatar} name={account.displayName} email={account.email}
          className="w-16 h-16 text-xl" />
        <div className="space-y-1.5">
          <div className="flex items-center gap-2">
            <Button type="button" variant="outlined" size="sm" icon={<Upload size={14} />}
              loading={updateAvatar.isPending} onClick={() => avatarInput.current?.click()}>
              {account.avatar ? 'Заменить фото' : 'Загрузить фото'}
            </Button>
            {account.avatar && (
              <Button type="button" variant="text" size="sm" icon={<Trash2 size={14} />}
                loading={updateAvatar.isPending} onClick={removeAvatar}>
                Удалить
              </Button>
            )}
          </div>
          <p className="text-xs text-fg4">PNG, JPG. Изображение уменьшится автоматически.</p>
          {avatarError && <p className="text-xs text-danger">{avatarError}</p>}
        </div>
        <input ref={avatarInput} type="file" accept="image/*" className="hidden" onChange={pickAvatar} />
      </div>

      <form onSubmit={save} className="space-y-5">
        <div>
          <div className="text-xs text-fg4 mb-1">Email</div>
          <div className="flex flex-wrap items-center gap-2 text-sm text-fg1">
            {account.email}
            {account.emailConfirmed
              ? <span className="inline-flex items-center gap-1 text-xs text-success"><CheckCircle2 size={13} /> подтверждён</span>
              : <>
                  <span className="inline-flex items-center gap-1 text-xs text-warning"><AlertCircle size={13} /> не подтверждён</span>
                  <Button type="button" variant="text" size="sm" loading={resend.isPending} onClick={resendConfirmation}>
                    Отправить письмо
                  </Button>
                </>}
          </div>
          {resendMsg && <p className="mt-1 text-xs text-fg3">{resendMsg}</p>}
        </div>

        <div>
          <div className="text-xs text-fg4 mb-1">Роль</div>
          <div className="text-sm text-fg1">{account.role === 'Admin' ? 'Администратор' : 'Пользователь'}</div>
        </div>

        <TextField label="Отображаемое имя" value={displayName}
          onChange={e => { setDisplayName(e.target.value); setSaved(false); }} />

        {error && <p className="text-sm text-danger">{error}</p>}
        {saved && <p className="text-sm text-success">Сохранено</p>}

        <div className="flex items-center gap-2">
          <Button type="submit" variant="filled" loading={update.isPending} disabled={!dirty}>
            Сохранить
          </Button>
          <Button type="button" variant="text" icon={<KeyRound size={14} />} onClick={() => setPwOpen(true)}>
            Сменить пароль
          </Button>
        </div>
      </form>

      <div className="mt-8 pt-6 border-t border-stroke">
        <h2 className="flex items-center gap-2 text-sm font-semibold text-fg1 mb-1">
          <Mail size={15} className="text-fg3" /> Сменить email
        </h2>
        <p className="text-xs text-fg4 mb-4">
          Отправим письмо на новый адрес — он станет адресом входа после перехода по ссылке.
        </p>
        <form onSubmit={submitEmail} className="space-y-4">
          <TextField label="Новый email" type="email" autoComplete="email" required
            value={newEmail} onChange={e => { setNewEmail(e.target.value); setEmailMsg(''); }} />
          <TextField label="Текущий пароль" type="password" autoComplete="current-password" required
            value={emailPassword} onChange={e => setEmailPassword(e.target.value)} />
          {emailError && <p className="text-sm text-danger">{emailError}</p>}
          {emailMsg && <p className="text-sm text-success">{emailMsg}</p>}
          <Button type="submit" variant="outlined" loading={changeEmail.isPending}
            disabled={!newEmail.trim() || !emailPassword}>
            Отправить подтверждение
          </Button>
        </form>
      </div>

      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />
    </div>
  );
}
