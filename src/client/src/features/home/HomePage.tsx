import { useEffect, useRef } from 'react';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { useActiveJobList } from '@/shared/api/jobs';
import { useUserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useHomeQueues, type HomeQueue } from '@/shared/api/homeQueues';
import { useHomeTips } from '@/shared/hooks/useHomeTips';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { hasWork } from '@/shared/ui/entryPage';
import { homeQueueRoute } from '@/shared/ui/homeQueueRoutes';
import { Icon } from '@/shared/ui/Icon';
import { Lightbulb, UploadFile, Warning } from '@/shared/ui/icons';
import { LinearProgress } from '@/shared/ui/LinearProgress';
import { ListItem } from '@/shared/ui/ListItem';
import { useNavSections } from '@/shared/ui/navSections';
import { NoAccessPage } from '@/shared/ui/NoAccessPage';
import { PageHeader } from '@/shared/ui/PageHeader';
import { NOT_COUNTED, queueCountText } from '@/shared/ui/queueCount';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { JobRow } from '@/features/jobs/JobRow';

/**
 * Главная «Продолжить работу» (shell-08, ТЗ UI-33…UI-37; макет «B · Главная», сверка — «годен с правками»):
 * что ждёт человека, что идёт, что полезно знать. Блоки «Недавние» придут вторым PR, тогда раскладка
 * станет двухколонной; сейчас — одна колонка шириной страницы смет.
 *
 * <p>Главная ничего не считает сама: очереди и их числа отдаёт сервер в правах спрашивающего
 * (`/api/home/queues`), задачи — тот же кэш, что у кнопки активности. Без прав на модули их блоков нет, и
 * это не отказ — остальные блоки остаются ([UI-33]). Кроме Главной не открыто ничего — страница «нет
 * доступа» (AUTH-15) тем же правилом и тем же меню, что расчёт входа (`hasWork`, `EntryRedirect`).</p>
 */
export function HomePage() {
  const { user } = useAuth();
  const { isPending } = useAccess();
  const { isPending: settingsPending } = useUserSettings(!!user);
  const { work, settings } = useNavSections();
  // Ждёт и судит, как расчёт входа (`EntryRedirect`): пока доступ в пути, меню — одна Главная, и «нет
  // доступа» мелькнуло бы у всех; пространство приходит настройкой. Меню — суженное пространством, иначе
  // `/` и `/home` отвечали бы одному человеку по-разному.
  if (isPending || settingsPending) return null;
  if (!hasWork([...work, ...settings])) return <NoAccessPage missing={null} />;
  return (
    <div className="h-full overflow-auto">
      <div className="mx-auto max-w-5xl px-6 py-5 space-y-6">
        <PageHeader title="Продолжить работу" subtitle="Что ждёт вас и что вы открывали недавно" />
        <Waiting />
        <Running />
        <Tips />
      </div>
    </div>
  );
}

/** Заголовок блока — `h2` вне карточек (Дизайнер, 0.2). */
function BlockTitle({ id, children }: { id: string; children: string }) {
  return <h2 id={id} className="text-title-medium text-on-surface mb-3">{children}</h2>;
}

/**
 * «Ждут вас» ([UI-34]). Открытых очередей нет — блока нет; все пусты — одна строка; ответ в пути —
 * заголовок и полоса; отказ всего ответа — строка с «Повторить». Отказ одной очереди — её карточка
 * остаётся со словами «не посчитано», а не пропадает и не показывает ноль.
 */
function Waiting() {
  const { data, isPending, isError, refetch } = useHomeQueues();
  // Отказ перечитывания при прежних данных — тоже отказ: старые числа под словами «не обновились» без
  // карточек, а не уверенное «3» рядом с ошибкой.
  const cards = isError ? [] : (data ?? []).flatMap(q => {
    const route = homeQueueRoute(q.module, q.id);
    const shown = cardNumber(q);
    return route && shown !== null ? [{ q, route, shown }] : [];
  });

  if (!isPending && !isError && (data ?? []).length === 0) return null;
  return (
    <section aria-labelledby="home-waiting">
      <BlockTitle id="home-waiting">Ждут вас</BlockTitle>
      {isPending && <LinearProgress label="Очереди загружаются" />}
      {isError && (
        <p className="flex items-center gap-2 text-body-medium text-on-surface-variant">
          <Icon icon={Warning} size={20} className="text-error shrink-0" aria-label="ошибка" />
          <span>{data ? 'Числа очередей не обновились.' : 'Очереди не загрузились.'}</span>
          <Button variant="text" onClick={() => void refetch()}>Повторить</Button>
        </p>
      )}
      {!isPending && !isError && cards.length === 0 && (
        <p className="text-body-medium text-on-surface-variant">Сейчас ничего не ждёт</p>
      )}
      {cards.length > 0 && (
        // Телефон — две колонки (четыре карточки в одну заняли бы экран), шире — по ширине блока.
        <ul className="grid grid-cols-2 gap-3 md:grid-cols-[repeat(auto-fill,minmax(14rem,1fr))]">
          {cards.map(({ q, route, shown }) => {
            const mark = q.doubt && route.doubt ? route.doubt : { icon: route.icon, tone: route.tone, iconLabel: route.iconLabel };
            return (
              <li key={`${q.module}/${q.id}`} className="contents">
                <Card to={route.href} title={q.label} icon={mark.icon} iconTone={mark.tone} iconLabel={mark.iconLabel}
                  variant={q.quiet ? 'outlined' : 'elevated'} data-testid={`home-queue-${q.module}-${q.id}`}>
                  <span className="text-label-medium">{q.moduleTitle}</span>
                  <span className={shown.counted ? 'text-headline-small text-on-surface' : 'text-body-medium text-on-surface-variant'}>
                    {shown.text}
                  </span>
                  {/* Причина — словами в теле: подсказку по наведению цель карточки накрыла бы, а на касании её нет. */}
                  {(q.refused ?? q.doubt) && <span className="text-body-small">{q.refused ?? `Число неполное — ${q.doubt}.`}</span>}
                  <span>{q.hint}</span>
                </Card>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

/**
 * Что карточка говорит числом — или `null`, когда карточки нет (ноль без оговорки). Правило — общее с
 * чипом экрана очереди (`queueCountText`); «не пришло» — словами.
 */
function cardNumber(q: HomeQueue): { text: string; counted: boolean } | null {
  if (q.count === null) return { text: NOT_COUNTED, counted: false };
  const text = queueCountText(q.count, q.doubt !== null);
  return text === null ? null : { text, counted: true };
}

/** «Выполняется» ([UI-36]) — живые задачи из того же кэша, что у кнопки активности; без задач блока нет. */
function Running() {
  const jobs = useActiveJobList();
  if (jobs.length === 0) return null;
  return (
    <section aria-labelledby="home-running">
      <BlockTitle id="home-running">Выполняется</BlockTitle>
      <Card flush>
        {jobs.map(job => <JobRow key={job.id} job={job} />)}
      </Card>
    </section>
  );
}

/** Клавиша — в виде строки поиска шапки (`AppShell`). Общего `Kbd` нет — не этот change. */
function Key({ children }: { children: string }) {
  return (
    <kbd className="shrink-0 rounded-xs border border-outline-variant px-1.5 text-label-small text-on-surface-variant">
      {children}
    </kbd>
  );
}

/**
 * «Полезно знать» ([UI-37]) — три неочевидных умения по правам. На телефоне — одна строка, о поиске:
 * сочетания клавиш и перетаскивание на касании не работают. «Скрыть» — снэкбар с «Вернуть»; отказ
 * записи — блок возвращается на глазах (`useSyncedPreference` откатывает выбор) и об этом сказано.
 */
function Tips() {
  const { shown, setShown, save } = useHomeTips();
  const { data: access = NO_ACCESS } = useAccess();
  const toast = useSnackbar();
  const mac = /Mac|iPhone|iPad/.test(navigator.platform);
  const scans = hasPermission(access, 'costs.invoice.edit');

  const hid = useRef(false);
  useEffect(() => {
    if (save !== 'error' || !hid.current) return;
    hid.current = false;
    toast.error('Подсказки не скрыты: запись не дошла до сервера.');
  }, [save, toast]);

  if (!shown) return null;
  const hide = () => {
    hid.current = true;
    setShown(false);
    toast.info('Подсказки скрыты. Вернуть их можно в профиле.', {
      action: { label: 'Вернуть', onClick: () => setShown(true) },
    });
  };
  return (
    <section aria-labelledby="home-tips">
      <div className="flex items-center justify-between gap-2 mb-1">
        <h2 id="home-tips" className="flex items-center gap-2 text-title-medium text-on-surface">
          <Icon icon={Lightbulb} size={20} className="text-on-surface-variant" aria-hidden />
          Полезно знать
        </h2>
        <Button variant="text" onClick={hide}>Скрыть</Button>
      </div>
      <Card variant="filled" flush>
        <ListItem passive leading={<Key>{mac ? '⌘ K' : 'Ctrl K'}</Key>}
          headline={<><span className="md:hidden">Поиск в шапке — найти счёт, стройку или действие</span>
            <span className="max-md:hidden">Найти или сделать что угодно с любого экрана</span></>} />
        {scans && (
          <ListItem passive className="max-md:hidden" icon={UploadFile}
            headline="Перетащите сканы в окно счетов — из них заведутся счета" />
        )}
        <ListItem passive className="max-md:hidden" leading={<Key>?</Key>} headline="Все горячие клавиши" />
      </Card>
    </section>
  );
}
