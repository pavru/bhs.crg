import { useState, useRef } from 'react';
import { useServerForm } from '@/shared/hooks/useServerForm';
import { useAccount, useUpdateAccount, useResendConfirmation, useChangeEmail, useUpdateAvatar } from '@/shared/api/account';
import { TextField } from '@/shared/ui/TextField';
import { Button } from '@/shared/ui/Button';
import { Avatar } from '@/shared/ui/Avatar';
import { downscaleToDataUri } from '@/shared/ui/avatarHelpers';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { apiError } from '@/shared/utils/apiError';
import { AppearanceSection } from './AppearanceSection';
import { MyPermissionsSection } from './MyPermissionsSection';
import { DefaultWorkspaceSection } from './DefaultWorkspaceSection';
import { FavoriteSitesSection } from './FavoriteSitesSection';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, Delete, ErrorIcon, Key, Mail, Palette, Upload, VerifiedUser } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';

/** Ответ сервера → имя в форме. Пока учётка не загружена — пусто, как и раньше. */
const accountName = (a: { displayName: string } | undefined) => a?.displayName ?? '';

/** Профиль текущего пользователя (issue #148): просмотр учётных данных,
 *  редактирование отображаемого имени, смена пароля. Доступно любой роли. */
export function ProfilePage() {
  const { data: account, isLoading } = useAccount();
  const update = useUpdateAccount();
  const updateAvatar = useUpdateAvatar();
  const resend = useResendConfirmation();
  const changeEmail = useChangeEmail();
  // Имя правится поверх ответа сервера (issue #858): своя правка показывается, пока отвечает
  // тому ответу, при котором начата; пришёл другой — показывается он.
  const [displayName, setDisplayName] = useServerForm(account, accountName);
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
    return <div className="px-6 py-4 text-body-medium text-on-surface-variant">Загрузка…</div>;
  }

  const dirty = displayName.trim() !== account.displayName;

  return (
    <div className="px-6 py-4 max-w-lg">
      <PageHeader title="Профиль" subtitle="Ваши учётные данные." className="mb-5" />

      {/* Аватар (issue #245) */}
      <div className="flex items-center gap-4 mb-6">
        <Avatar src={account.avatar} name={account.displayName} email={account.email}
          className="w-16 h-16 text-xl" />
        <div className="space-y-1.5">
          <div className="flex items-center gap-2">
            <Button type="button" variant="outlined" size="sm" icon={<Icon icon={Upload} size={18} />}
              loading={updateAvatar.isPending} onClick={() => avatarInput.current?.click()}>
              {account.avatar ? 'Заменить фото' : 'Загрузить фото'}
            </Button>
            {account.avatar && (
              <Button type="button" variant="text" size="sm" icon={<Icon icon={Delete} size={18} />}
                loading={updateAvatar.isPending} onClick={removeAvatar}>
                Удалить
              </Button>
            )}
          </div>
          <p className="text-body-small text-on-surface-variant">PNG, JPG. Изображение уменьшится автоматически.</p>
          {avatarError && <p className="text-body-small text-error">{avatarError}</p>}
        </div>
        <input ref={avatarInput} type="file" accept="image/*" className="hidden" onChange={pickAvatar} />
      </div>

      <form onSubmit={save} className="space-y-5">
        <div>
          <div className="text-body-small text-on-surface-variant mb-1">Email</div>
          <div className="flex flex-wrap items-center gap-2 text-body-medium text-on-surface">
            {account.email}
            {account.emailConfirmed
              ? <span className="inline-flex items-center gap-1 text-body-small text-success"><Icon icon={CheckCircle} size={18} /> подтверждён</span>
              : <>
                  <span className="inline-flex items-center gap-1 text-body-small text-warning"><Icon icon={ErrorIcon} size={18} /> не подтверждён</span>
                  <Button type="button" variant="text" size="sm" loading={resend.isPending} onClick={resendConfirmation}>
                    Отправить письмо
                  </Button>
                </>}
          </div>
          {resendMsg && <p className="mt-1 text-body-small text-on-surface-variant">{resendMsg}</p>}
        </div>

        <div>
          <div className="text-body-small text-on-surface-variant mb-1">{account.roles.length === 1 ? 'Роль' : 'Роли'}</div>
          {/* Все роли, а не первая (issue #984): действующие права — объединение прав всех.
              Пусто — так и сказано словами: прежде здесь подставлялся «Инженер ИД», и профиль
              называл роль, которой нет, ровно тому, у кого нет никакого доступа. */}
          <div className="text-body-medium text-on-surface">
            {account.roles.length > 0
              ? account.roles.map(r => r.title).join(', ')
              : <span className="text-on-surface-variant">Ролей нет — доступ не выдан</span>}
          </div>
        </div>

        <TextField label="Отображаемое имя" value={displayName}
          onChange={e => { setDisplayName(e.target.value); setSaved(false); }} />

        {error && <p className="text-body-medium text-error">{error}</p>}
        {saved && <p className="text-body-medium text-success">Сохранено</p>}

        <div className="flex items-center gap-2">
          <Button type="submit" variant="filled" loading={update.isPending} disabled={!dirty}>
            Сохранить
          </Button>
          <Button type="button" variant="text" icon={<Icon icon={Key} size={18} />} onClick={() => setPwOpen(true)}>
            Сменить пароль
          </Button>
        </div>
      </form>

      {/* Оформление и язык (issue #954, ТЗ AUTH-16.6): личные настройки — в профиле, а не в
          «Настройке системы», где язык лежал за административным правом. */}
      <div className="mt-8 pt-6 border-t border-outline-variant">
        <h2 className="flex items-center gap-2 text-body-medium font-semibold text-on-surface mb-4">
          <Icon icon={Palette} size={18} className="text-on-surface-variant" /> Оформление и язык
        </h2>
        <AppearanceSection />
      </div>

      {/* Пространство по умолчанию (ТЗ AUTH-16.5, issue #1001): секция сама решает, видна ли она. */}
      <DefaultWorkspaceSection />

      {/* Избранные стройки (#1218) — между личным оформлением и правами; секция сама решает, видна ли. */}
      <FavoriteSitesSection />

      {/* Мои роли и права — только для чтения (ТЗ AUTH-16.6): ответ на вопрос «почему у меня нет
          этого раздела», который иначе задают администратору. */}
      <div className="mt-8 pt-6 border-t border-outline-variant">
        <h2 className="flex items-center gap-2 text-body-medium font-semibold text-on-surface mb-1">
          <Icon icon={VerifiedUser} size={18} className="text-on-surface-variant" /> Мои права
        </h2>
        <p className="text-body-small text-on-surface-variant mb-4">
          Что открывают выданные вам роли. Изменить их может только администратор.
        </p>
        <MyPermissionsSection />
      </div>

      <div className="mt-8 pt-6 border-t border-outline-variant">
        <h2 className="flex items-center gap-2 text-body-medium font-semibold text-on-surface mb-1">
          <Icon icon={Mail} size={18} className="text-on-surface-variant" /> Сменить email
        </h2>
        <p className="text-body-small text-on-surface-variant mb-4">
          Отправим письмо на новый адрес — он станет адресом входа после перехода по ссылке.
        </p>
        <form onSubmit={submitEmail} className="space-y-4">
          <TextField label="Новый email" type="email" autoComplete="email" required
            value={newEmail} onChange={e => { setNewEmail(e.target.value); setEmailMsg(''); }} />
          <TextField label="Текущий пароль" type="password" autoComplete="current-password" required
            value={emailPassword} onChange={e => setEmailPassword(e.target.value)} />
          {emailError && <p className="text-body-medium text-error">{emailError}</p>}
          {emailMsg && <p className="text-body-medium text-success">{emailMsg}</p>}
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
