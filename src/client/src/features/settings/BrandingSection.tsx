import { useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Delete, Upload, Warning } from '@/shared/ui/icons';
import { CollapsibleSection } from './CollapsibleSection';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { brandIconUrl } from '@/shared/ui/brandIcons';
import { useImageFailure } from '@/shared/ui/useImageFailure';
import {
  useBranding, useSaveProductName, useUploadLogo, useRemoveLogo, logoUrl,
  type BrandIcons, type Branding,
} from '@/shared/api/branding';
import { elongatedWarning, homeScreenNameHint, iconsFailure, rebuildPoll } from './brandingIcons';

/** Тот же предел, что на сервере (BrandingDefaults.MaxProductNameLength). */
const MAX_NAME = 64;

/** Сколько держится сообщение о непостроенных значках — как у сообщений об отказе. */
const FAILURE_TOAST = 8000;

/**
 * Название продукта и логотип компании (ТЗ CORE-25.1, issue #967).
 *
 * Своя секция, а не поле в «Интеграциях»: там внешние службы, а здесь — как система называется и
 * выглядит на ЭТОМ экземпляре. Видно результат сразу: шапка и заголовок вкладки читают ту же
 * настройку, что правится здесь.
 *
 * Логотип — ещё и значок вкладки и приложения (issue #1315): значки строит сервер, а секция
 * показывает, что из логотипа вышло, — и говорит словами, когда не вышло.
 */
export function BrandingSection() {
  // Пока отказ построения из тех, что сервер повторяет сам, секция спрашивает оформление снова —
  // иначе плашка «не построены» пережила бы сам отказ (см. `rebuildPoll`).
  const { data, isError, refetch } = useBranding(b => rebuildPoll(b?.icons));
  // Пока настройка НЕ ПОЛУЧЕНА, сохранять нечего: поле пусто не потому, что название сняли, а
  // потому, что его ещё не знают, — и «Сохранить» отправило бы пустоту, стерев заданное (ревью
  // PR #1061). Та же мысль, что у знака в шапке: «ещё не знаем» — не «пусто».
  const saveName = useSaveProductName();
  const uploadLogo = useUploadLogo();
  const removeLogo = useRemoveLogo();
  const toast = useToast();
  const fileInput = useRef<HTMLInputElement>(null);

  // Значение формы = правка пользователя, иначе сохранённое. Эффектом не синхронизируем: ввод
  // затирался бы ответом сервера ровно в момент набора.
  const [edited, setEdited] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const name = edited ?? (data?.isCustom ? data.productName : '');

  async function submitName() {
    try {
      await saveName.mutateAsync(name.trim() ? name.trim() : null);
      setEdited(null);
      toast.success(name.trim() ? 'Название сохранено.' : 'Название сброшено к общему.');
    } catch (e) {
      toast.apiError(e, 'Не удалось сохранить название.');
    }
  }

  async function pickLogo(file: File | undefined) {
    if (!file) return;
    try {
      const saved = await uploadLogo.mutateAsync(file);
      // Логотип сохранён, а значки из него не вышли — это не «успех»: зелёный тост здесь был бы
      // отказом, переодетым в результат. Подробности — под логотипом, постоянной плашкой.
      // Держится столько же, сколько отказ: загрузка идёт до пятнадцати секунд, и четыре секунды
      // обычного сообщения администратор, отвлёкшийся на это время, пропустил бы.
      if (saved.icons.state === 'failed')
        toast.info('Логотип загружен, но значки из него построить не удалось — подробности под логотипом.',
          { duration: FAILURE_TOAST });
      else toast.success('Логотип загружен.');
    } catch (e) {
      toast.apiError(e, 'Не удалось загрузить логотип.');
    }
  }

  // Подсказки под названием: сначала прежняя — про общее название, к ней — про длину под значком.
  // Пока настройка не получена, о названии сказать нечего: «не задано» было бы догадкой.
  const nameHints = [
    data && (data.isCustom ? 'Пусто — вернуться к общему названию' : `Не задано, действует общее: ${data.productName}`),
    homeScreenNameHint(name),
  ].filter(Boolean).join('. ');

  return (
    <CollapsibleSection title="Название и логотип" storageKey="branding" defaultOpen={false}>
      <p className="text-body-small text-on-surface-variant">
        Видны в шапке и на странице входа. Название — ещё в заголовке вкладки и под значком
        установленного приложения; логотип — ещё и сам значок: на вкладке, в закладках и на экране
        телефона. Пока они не заданы, система называет себя общим именем и показывает свой значок.
      </p>

      {/* Настройка не получена и уже не придёт сама — это не «ещё грузится»: без слов секция стояла
          бы с пустым полем, запертой «Сохранить» и без строки логотипа, то есть отказом, одетым в
          загрузку (проверка перед PR). Только пока показать нечего: упавший перезапрос уже
          показанную настройку не закрывает. */}
      {isError && !data && (
        <div className="flex items-center gap-3" data-testid="branding-load-failed">
          <span className="text-body-medium text-error">Название и логотип не загрузились.</span>
          <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
        </div>
      )}

      <div>
        <TextField
          containerClassName="max-w-sm"
          label="Название продукта"
          value={name}
          maxLength={MAX_NAME}
          onChange={e => setEdited(e.target.value)}
          hint={nameHints}
        />
      </div>

      <div className="flex items-center gap-3">
        <Button variant="filled" onClick={submitName} disabled={!data || saveName.isPending}>
          Сохранить
        </Button>
      </div>

      <div className="border-t border-outline-variant pt-4">
        <p className="text-body-small text-on-surface-variant mb-2">
          Логотип — PNG, JPEG, WebP или SVG, до 5 МБ. Он же доступен печатным формам как ассет
          уровня системы: в шаблоне это{' '}
          <code className="text-label-small">image(&quot;/assets/company-logo.png&quot;)</code>{' '}
          — с тем расширением, с каким файл загружен. Значок вкладки и приложения система строит из
          логотипа сама: вписывает его в квадрат целиком, без обрезки.
        </p>

        {/* Пока настройка не получена, строки логотипа нет вовсе: «нет» в рамке и кнопка «Загрузить»
            на месте неполученного были бы «ещё не знаем», выданным за «пусто». */}
        {data && (
          <LogoRow data={data} uploading={uploadLogo.isPending} removing={removeLogo.isPending}
            onUpload={() => fileInput.current?.click()} onRemove={() => setRemoving(true)} />
        )}
        <input
          ref={fileInput}
          type="file"
          accept=".png,.jpg,.jpeg,.webp,.svg"
          className="hidden"
          onChange={e => { void pickLogo(e.target.files?.[0]); e.target.value = ''; }}
        />

        {data && <IconsNote icons={data.icons} named={data.isCustom} />}
      </div>

      {/* Постоянно, а не «после смены»: секцию открывают, чтобы менять оформление, а вопрос «почему
          на телефоне старый значок» возникает назавтра — оговорка из состояния сессии к тому
          времени исчезла бы. Касается и названия, и снятия логотипа, поэтому внизу и без условия. */}
      <p className="text-body-small text-on-surface-variant max-w-xl">
        Приложение, уже установленное на телефон или компьютер, получает новые значок и название не
        сразу: срок выбирает браузер, это может занять несколько дней. На iPhone они сами не
        обновляются: значок с экрана «Домой» удаляют и добавляют заново.
      </p>

      <ConfirmDialog
        open={removing}
        onOpenChange={o => { if (!o) setRemoving(false); }}
        title="Убрать логотип?"
        description="Система вернётся к своему значку — в шапке, на вкладке и в приложении. Печатные формы, ставящие логотип, останутся без него."
        confirmLabel="Убрать логотип"
        // Отказ показывает сам диалог (он остаётся открытым и называет причину), поэтому здесь
        // только успех: тост о неудаче дублировал бы уже показанное на месте.
        onConfirm={async () => {
          await removeLogo.mutateAsync();
          setRemoving(false);
          toast.success('Логотип убран.');
        }}
      />
    </CollapsibleSection>
  );
}

/** Строка логотипа: сам логотип, кнопки и — за чертой — что из него вышло значками. */
function LogoRow({ data, uploading, removing, onUpload, onRemove }: {
  data: Branding; uploading: boolean; removing: boolean; onUpload: () => void; onRemove: () => void;
}) {
  const logo = logoUrl(data);
  // Файл логотипа сохранён, а браузер его не показывает — не изображение под верным расширением
  // либо не читается из хранилища. Говорим словом, а не битой рамкой с alt-текстом: шапка в этом
  // случае молча рисует общий значок (`BrandLogo`), и узнать, что логотипа на экране нет, больше
  // неоткуда.
  //
  // Отказ помнится по адресу логотипа ВМЕСТЕ с меткой значков. По адресу — чтобы заменённый логотип
  // не оставался «нечитаемым». С меткой — потому что отказ картинки не должен пережить свою причину:
  // хранилище не отвечало, значки не построились, рамка сказала «не читается»; хранилище поднялось,
  // опрос принёс построенные значки — а адрес логотипа прежний, и без метки рамка стояла бы со
  // словами «не читается» рядом с превью, построенными из этого самого логотипа (проверка перед PR).
  //
  // И недолго: случайный сбой загрузки картинки при уже построенных значках метку не меняет и
  // опроса не запускает — картинку пробуют снова (см. `useImageFailure`).
  const [unread, fail] = useImageFailure(logo && `${logo}|${data.icons.version}`);
  // Пока идёт одна правка логотипа, вторая заперта: загрузка длится до пятнадцати секунд, и
  // «Убрать», нажатое в это время, отвечало бы раньше — экран остался бы с тем ответом, что пришёл
  // последним, а не с тем, что на сервере (ревью PR #1321).
  const busy = uploading || removing;
  return (
    <div className="flex flex-wrap items-center gap-4" data-testid="branding-logo-row">
      <div className="flex items-center justify-center w-16 h-16 rounded-md border border-outline-variant bg-surface overflow-hidden">
        {!logo && <span className="text-label-small text-on-surface-variant">нет</span>}
        {logo && unread && (
          <span className="text-label-small leading-tight text-center text-warning" data-testid="branding-logo-unread">
            {/* Перенос задан явно: в одну строку слова встают в рамку впритык, от края до края. */}
            не<br />читается
          </span>
        )}
        {logo && !unread && (
          <img src={logo} alt="Логотип компании" className="max-w-full max-h-full object-contain"
            onError={fail} />
        )}
      </div>

      <Button variant="outlined" icon={<Icon icon={Upload} size={18} />} onClick={onUpload} loading={uploading}
        disabled={removing}>
        {data.hasLogo ? 'Заменить' : 'Загрузить'}
      </Button>
      {data.hasLogo && (
        <Button variant="text" icon={<Icon icon={Delete} size={18} />} onClick={onRemove} disabled={busy}>
          Убрать
        </Button>
      )}

      <IconPreviews icons={data.icons} />
    </div>
  );
}

/**
 * Превью значков — только ПОСТРОЕННЫХ из логотипа. Картинки здесь значат одно: «вот что вышло из
 * вашего логотипа». Стандартный значок на этом месте сделал бы состояния «логотипа нет» и «значки
 * не построены» неотличимыми на вид — отказом, переодетым в результат; действующий значок без
 * логотипа администратор и так видит на самой вкладке.
 */
function IconPreviews({ icons }: { icons: BrandIcons }) {
  // Превью не загрузилось (429 корзины значков, сервер перезапускается) — говорим словами: битая
  // картинка с alt-текстом на месте «вот что вышло из логотипа» читалась бы как сам результат.
  const [missing, fail] = useImageFailure(icons.state === 'custom' ? icons.version : null);
  if (icons.state !== 'custom') return null;
  const tab = brandIconUrl('tab', icons.version);
  if (missing)
    return (
      <p className="border-l border-outline-variant pl-4 text-label-small leading-tight text-warning max-w-[9rem]"
        data-testid="brand-icon-previews-missing">
        Значки построены, но их картинки не загрузились.
      </p>
    );
  return (
    <div className="flex items-end gap-4 border-l border-outline-variant pl-4" data-testid="brand-icon-previews">
      <figure className="flex flex-col items-center gap-1">
        {/* 16 точек, а не 32, как в файле: крупнее значок на вкладке не бывает нигде, и для широкого
            логотипа это разница между «видно» и «полоска». Половины закреплены и с темой приложения
            не переключаются: вкладка браузера ей не следует. */}
        <div className="flex rounded-sm border border-outline-variant overflow-hidden">
          <span className="flex items-center justify-center w-7 h-7 bg-tab-light">
            <img src={tab} alt="Значок вкладки на светлом фоне" width={16} height={16} onError={fail} />
          </span>
          <span className="flex items-center justify-center w-7 h-7 bg-tab-dark">
            <img src={tab} alt="Значок вкладки на тёмном фоне" width={16} height={16} onError={fail} />
          </span>
        </div>
        <figcaption className="text-label-small text-on-surface-variant">вкладка</figcaption>
      </figure>

      <figure className="flex flex-col items-center gap-1">
        {/* Значок iPhone: он без своего скругления и прозрачности — скругляем сами, как система.
            Рамка обязательна: белая плитка на светлой поверхности иначе не видна, и полей не
            разглядеть. 40 точек: меньше 48 этот значок на телефоне не бывает, а 40 ещё помещается
            в строку. */}
        <img src={brandIconUrl('apple', icons.version)} alt="Значок приложения" width={40} height={40}
          className="rounded-[22%] border border-outline-variant" onError={fail} />
        <figcaption className="text-label-small text-on-surface-variant">приложение</figcaption>
      </figure>
    </div>
  );
}

/** Слова под строкой логотипа: отказ построения — плашкой, вытянутый логотип — предупреждением. */
function IconsNote({ icons, named }: { icons: BrandIcons; named: boolean }) {
  const failure = iconsFailure(icons);
  if (failure)
    // Warning, а не danger: логотип сохранён, действует запасное значение — стандартные значки.
    // Заметность даёт заливка и то, что плашка стоит на месте ожидаемого результата — превью.
    return (
      <p role="status" data-testid="brand-icons-failed"
        className="mt-3 flex items-start gap-1.5 rounded-xs px-2 py-1.5 text-body-small bg-warning-container text-on-warning-container border border-outline-variant max-w-xl">
        <Icon icon={Warning} size={18} className="shrink-0 mt-px" />
        <span>{failure}</span>
      </p>
    );

  const elongated = elongatedWarning(icons, named);
  if (!elongated) return null;
  // Без блокировки и без подтверждения перед загрузкой: вытянутый логотип — законный выбор, а путь
  // к разборчивому значку — та же «Заменить» строкой выше.
  return (
    <p data-testid="brand-icons-elongated" className="mt-3 flex items-start gap-1.5 text-body-small text-warning max-w-xl">
      <Icon icon={Warning} size={18} className="shrink-0 mt-px" />
      <span>{elongated}</span>
    </p>
  );
}
