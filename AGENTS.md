# AGENTS.md

Указания агентам, работающим с этим репозиторием (Codex и другие, кто читает `AGENTS.md`).

⚠️ **Это копия `CLAUDE.md`.** Единственный источник — он: правку вносите туда и переносите сюда тем
же коммитом. Два расходящихся файла с правилами — это два разных набора правил, и узнают об этом
тогда, когда агенты начнут делать разное.

## Что это за проект

Система генерации **исполнительной документации** для электромонтажных строительных проектов.
Код — в `src/`.

Этот файл читается целиком каждой сессией, поэтому в нём — только правила и ловушки. «Почему так
сделано» — в [docs/DEV_NOTES.md](docs/DEV_NOTES.md), правила интерфейса — в [docs/UI.md](docs/UI.md);
история и обоснования сюда не возвращаются.

## Стек

| Слой | Технология |
|---|---|
| Frontend | React 19 + TypeScript, Radix UI, Tailwind v4, React Query, Monaco (редактор Typst-шаблонов) |
| Backend | ASP.NET Core 10 (Minimal APIs), EF Core 10 (Npgsql), MediatR |
| Auth | ASP.NET Identity + JWT, роли с гранулярными правами (`модуль.объект.действие`, `AUTH-1`) (без SSO / корп. интеграций) |
| БД | PostgreSQL 18 |
| Blob-хранилище | Garage (self-hosted, S3-совместимое) |
| PDF | **Typst** (CLI, env `TYPST_PATH`). DOCX на ВЫХОДЕ **не поддерживается** (на входе Word читается — см. «Офисные файлы → PDF») |
| Распознавание/поиск | Ollama / Anthropic / Gemini (распознавание сканов), Serper / Yandex (веб-поиск) — для документов качества |
| Скриптовой движок | Jint (JavaScript — вычисляемые колонки DataSet) |
| Офисные файлы → PDF | LibreOffice за HTTP-обёрткой Gotenberg — отдельный контейнер `converter` в сети без выхода наружу |
| Плагины | .NET AssemblyLoadContext + HTTP-плагины |

## Структура solution

```
src/
  server/
    BHS.CRG.slnx          — solution file (.NET 10 format)
    BHS.CRG.Api/          — ASP.NET Core Minimal API (точка входа)
    BHS.CRG.Application/  — MediatR команды/запросы, интерфейсы (IBlobStorage, IRepository)
    BHS.CRG.Domain/       — доменные сущности (чистый C#, без зависимостей)
    BHS.CRG.Infrastructure/ — EF Core, Garage (S3), Typst-генерация, распознавание/поиск, плагины
    BHS.CRG.Plugins/      — контракты плагинов (IDataSourcePlugin)
    BHS.CRG.Modules/      — КОНТРАКТЫ ядра для модулей: IAppModule, права, порты к данным, ModuleDbContext.
                            ⚠️ Ни одной ссылки на наши проекты — через него модуль получает ровно то,
                            что положено (ModuleBoundaryTests падает на любой ProjectReference)
    BHS.CRG.Modules.Costs/ — модуль «Счета и накладные»: своя схема базы `costs`, свой набор миграций
                            (Migrations/), ссылки только на контракты и домен
    BHS.CRG.Modules.Plan/ — модуль «Планирование» (этап 3): схема `plan`, устроен как `costs`.
                            Клиент — features/planning: «plan» в клиенте уже значит план документов (#796)
    BHS.CRG.Modules.Work/ — модуль «Учёт работ» (этап 4): схема `work`, устроен как `costs` и `plan`.
                            ⚠️ `workNav` в клиенте — «рабочие пункты» меню, а не этот модуль
  client/                 — React SPA (Vite + Tailwind v4)
    src/
      features/<область>/ — экраны по областям: catalog, templates, document-sets, datasets, costs,
                            planning, tables, settings…
      shared/api/         — apiClient (axios + JWT), React Query hooks, types.ts
      shared/ui/          — библиотека компонентов MD3: экраны собираются только из неё (docs/UI.md)
      theme/              — тема MD3 (файлы пишет `npm run md3:theme`)
```

## Команды разработки

```bash
# Инфраструктура (PostgreSQL + Garage) — всё в контейнерах, как в поставке.
# ⚠️ База — 127.0.0.1:5433, НЕ localhost и НЕ 5432: на 5432 бывает нативная служба (приложение молча
#    ушло бы в чужую базу), а localhost идёт в ::1, где порт контейнера молчит по семь секунд на
#    подключение. Почему так и как перенести данные из нативной службы — DEV_NOTES, «Дев-стенд».
# ⚠️ Именно `compose wait garage-init`: `up -d` отвечает 0 и при упавшей инициализации хранилища.
docker compose up -d && docker compose wait garage-init

# Backend (запуск с автомиграцией при старте: сначала схема ядра, потом схемы включённых модулей)
dotnet run --project src/server/BHS.CRG.Api

# Frontend (dev-сервер на :5173, proxy /api → :5000)
cd src/client && npm run dev

# TypeScript проверка — ВАЖНО: -b. Корневой tsconfig только ссылки; `tsc --noEmit` на нём
# не проверяет ничего и всегда «зелёный»
cd src/client && npx tsc -b

# Backend сборка
cd src/server && dotnet build BHS.CRG.slnx

# Backend тесты (xUnit). ⚠️ Нужен настоящий Typst CLI — в PATH или TYPST_PATH: без него тесты
# значков и кодировки вывода компилятора красные, с названной причиной.
cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj

# Весь набор backend — так, как в CI (четыре процесса на своих базах, по два разом).
# Один класс или фильтр — обычным `dotnet test`.
bash scripts/backend-tests.sh

# Frontend тесты (vitest; *.test.ts рядом с кодом)
cd src/client && npm test

# Храповики: падают, если нарушений стало БОЛЬШЕ (накопленное чинить не требуется)
cd src/client && npm run lint:ratchet           # линт по правилам ESLint
cd src/client && npm run lint:ratchet:update    # переписать базовый уровень (осознанно!)
cd src/client && npm run ui:ratchet             # свой компонент вместо shared/ui — правила и куда
                                                # вместо: docs/UI.md, «Кто сторожит»
# ⚠️ Класс вне словаря темы MD3 (`text-xs`, `rounded-2xl`, `bg-gray-500`, `rounded-[28px]`) ловит НЕ
#    храповик, а сторож scripts/theme-classes.test.mjs внутри `npm test` ([UI-82])

# Логика скрипта обновления (без Docker и сети)
bash deploy/update.tests.sh

# Конвертер офисных файлов на живом контейнере (нужны Docker, сеть и pdftotext)
bash deploy/converter.tests.sh
```

### Миграции EF

⚠️ **`--context` называем всегда**: контекстов несколько, и без него перепутанный `--project` молча
кладёт миграцию в чужой набор (почему — DEV_NOTES, «Второй контекст базы»).

| Схема | `--project` | `--context` | `--startup-project` |
|---|---|---|---|
| ядро | `src/server/BHS.CRG.Infrastructure` | `AppDbContext` | `src/server/BHS.CRG.Api` |
| `costs` | `src/server/BHS.CRG.Modules.Costs` | `CostsDbContext` | не нужен (фабрика контекста) |
| `plan` | `src/server/BHS.CRG.Modules.Plan` | `PlanDbContext` | не нужен |
| `work` | `src/server/BHS.CRG.Modules.Work` | `WorkDbContext` | не нужен |

```bash
dotnet ef migrations add <Name> --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext

# Откат только что созданной миграции МОДУЛЯ — с --force: без него remove идёт в базу, а базы у
# фабрики нет, и команда падает, оставив файлы на месте.
dotnet ef migrations remove --force --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext

# Ручное применение (обычно не нужно — app мигрирует сам). ⚠️ У МОДУЛЯ базу называет только
# окружение: без переменной — отказ адресом-заглушкой, нарочно.
ConnectionStrings__Postgres="Host=127.0.0.1;Port=5433;Username=postgres;Password=xxsystem;Database=bhs_crg" \
  dotnet ef database update --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext
```

## Стенды

**HTTPS** — `https://localhost/`: перед API (`:5000`) и дев-сервером клиента (`:5173`) стоит nginx из
того же `docker compose` (issue #940). Без защищённого контекста камера, геолокация, service worker и
установка как приложение недоступны вовсе, а заголовков `X-Forwarded-*` без этого nginx на стенде не
бывает — логику за обратным прокси проверяют только через `https://localhost/`. ⚠️ **API и клиент обязаны слушать не только петлю**:
контейнер приходит к ним с адреса шлюза. Поэтому Kestrel — на `http://+:5000`, vite — с `--host`;
запущенные иначе дают 502, похожий на поломку приложения. Сертификат и телефон —
[DEV_NOTES](docs/DEV_NOTES.md#стенд-по-https-сертификат-и-доступ-с-телефона).

**1С (OData)** — база `E:\Accounting\BHS.CRG`, `http://127.0.0.1:8090/bhs-crg/odata/standard.odata/`.
Копия рабочей (тесты могут писать), но данные настоящие: наружу не выпускаем. Работы с 1С — на
self-hosted раннере `runs-on: [self-hosted, 1c]`, и на PR **только своих**: пускает не `if:` workflow, а
хук на раннере `scripts/1c-stand/runner-job-started.ps1`. Новое событие или новый соавтор — сначала хук
и его тесты. Секреты — в окружении `1c`. Подробности —
[DEV_NOTES](docs/DEV_NOTES.md#стенд-1с-odata-и-раннер).

## CI

`.github/workflows/ci.yml` на каждый PR и push в master: backend (сборка + тесты, двумя долями),
frontend (`tsc -b`, сборка, vitest, храповики линта и интерфейса, `md3:migrate --check`), логика
скриптов вместе с живой проверкой конвертера и **живые прогоны в браузере** (тремя долями).
**Node — одна версия на всё: `.nvmrc` в корне** (сверяет `nodeVersion.test.ts`).

**Слияние запрещают три обязательные проверки** («Master ruleset», с 25.08.2026): **Backend (сборка +
тесты)**, **Frontend (типы + тесты + линт)** и **Скрипт обновления (логика)**. master меняется только
через PR, одобрений не требуется, ветка обязана быть актуальной (`strict`). Живые прогоны,
claude-review и OpenSpec не обязательны — сообщают.

⚠️ **Имена работ в `ci.yml` — договор с правилом**: обязательные проверки перечислены там ПО ИМЕНАМ.
Переименованная работа блокирует слияние навсегда, с сообщением «the base branch policy prohibits the
merge», которое на имя не указывает ничем (наступали, #890). Менять имя — только вместе с правилом, и
сначала правило.

⚠️ **Выпуск гейтит прогон ЦЕЛИКОМ**: `release.yml` спрашивает итог прогона и работ не различает, поэтому
падение ЛЮБОЙ из четырёх работ — в том числе необязательных живых прогонов — останавливает выпуск.
Упало по внешней причине (сеть, Docker Hub) — «Re-run failed jobs», итог пересчитается.

**PR только с планированием — без тяжёлого CI.** PR, тронувший ТОЛЬКО `docs/tz/**` и `openspec/**`,
четыре тяжёлые работы пропускает по `if:` (`scripts/planning-only.sh`), и Skipped засчитывается.
⚠️ `paths-ignore` вместо этого — запертое слияние: проверки повиснут в «Expected — waiting». Хоть один
файл вне двух каталогов (`docs/*.md`, `CLAUDE.md`…) — полный CI.

**OpenSpec — строгая проверка на каждом PR**, мимо пропуска «только планирование». Отказ дают и
предупреждения: требование длиннее 500 знаков (примеры — в сценарий, несколько поведений — несколькими
требованиями с той же меткой) и заглушка вместо `## Purpose` у новой спеки. Версия CLI прибита в
`ci.yml` (`OPENSPEC_VERSION`); локально — та же:

```bash
npm install -g @fission-ai/openspec@1.14.3
openspec validate --all --strict --no-interactive
```

**Храповики не требуют чинить накопленное — требуют не добавлять.** Стало лучше — уровень опускается
тем же PR (линт локально опускает сам, CI падает с просьбой закоммитить).
- Линт — по правилам, базовый уровень `src/client/eslint-baseline.json`.
- **Размер файлов** (issue #1041): файл больше **500 строк кода** (пустые и комментарии не в счёт)
  обязан стоять в `src/file-size-baseline.json` и не расти; новый файл выше порога не заводится.
  Поднять уровень руками можно, сказав в описании PR, почему файл обязан быть таким. Смотрит весь
  рукописный код: `src`, `deploy`, `docs/tools`, `scripts`.

```bash
cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet
```

📄 Почему так и из чего состоят прогоны —
[DEV_NOTES](docs/DEV_NOTES.md#ci-состав-прогона-и-связь-с-выпуском).

## Версия приложения

Единственный источник — `src/server/Directory.Build.props`, `<Version>`. Клиент своей версии не
имеет (`package.json` = `0.0.0`): UI берёт её из `/api/version`.

**Версию в PR НЕ меняем — её поднимает CI после слияния в master** (`bump-version.yml`). **Теги версий
руками не создаём**: тег `vX.Y.Z` означает выпуск и ставит его только workflow `Release`.

- **PATCH** — по умолчанию, каждым слиянием.
- **MINOR — метка `version:minor` на PR** (PATCH обнуляется).
- **MAJOR — решение владельца продукта**, правкой `<Version>` руками, отдельным PR.
- `[skip bump]` **в заголовке коммита, которым PR попадает в master**, — слияние версию не двигает. У
  PR из одного коммита squash берёт заголовок коммита, а не PR: смотрите поле в окне «Squash and merge».
- Слияние «только планирование» версию не двигает само — и его же не видит шаг `Release` «Версия
  поднята за все слияния». Оба решают по `scripts/planning-only.sh`: меняется один — меняется и другой.

⚠️ **После слияния приходит коммит бота** (секунд через двадцать; после «только планирование» — нет).
Следующий PR обновляйте ПОСЛЕ него, иначе — второе обновление и второй прогон CI.

⚠️ **Красный «Повышение версии» = версия НЕ поднята.** Историю не чинить: следующий успешный прогон
(или «Re-run jobs») поднимет за все пропущенные слияния. До тех пор `Release` отказывает.

```bash
bash scripts/get-version.sh          # текущая версия
bash scripts/unbumped-merges.sh      # слияния, за которые версия ещё не поднята (пусто — всё поднято)
bash scripts/bump-version.tests.sh   # логика скриптов версии
```

📄 Бот, ключ и граница — [DEV_NOTES](docs/DEV_NOTES.md#повышение-версии-бот-ключ-и-граница).

## Документация и развёртывание

- `docs/` — инструкции (Markdown + PDF): `DEPLOYMENT.md`, `USER_GUIDE.md`, `ADMIN_GUIDE.md`
  (индекс — `docs/README.md`). Сборка PDF — `docs/tools/` (`npm run pdf`). Содержание в начале этих
  трёх инструкций строит `npm run toc` там же — руками не правят; добавили или переименовали главу —
  перестройте (забытое ловит «Frontend»).
- [`docs/DEV_NOTES.md`](docs/DEV_NOTES.md) — «почему так сделано» и полный листинг REST API.
- [`docs/UI.md`](docs/UI.md) — **правила интерфейса**: Material 3, цвет только ролью MD3 (`bg-primary`,
  `text-on-surface`; тема — `src/client/src/theme/md3-theme.json`), шкала радиусов и теней.
  ⚠️ **Новый экран, компонент или заметная переделка взаимодействия — сначала три макета в Claude
  (артефакты типа «Design») и выбор владельца, потом код** (issue #1545). Дизайн-система —
  [артефакт «BHS.CRG UI»](https://claude.ai/artifact/UPzvpNJpoqNWsGWMAXCYQh); до кода её догоняет
  владелец, агент синхронизацию не запускает. Что в `shared/ui` новее — по коду и `.d.ts`. Порядок и
  исключения — [docs/UI.md](docs/UI.md#новый-экран-порядок).
- `deploy/` — Docker Compose на весь стек + Dockerfile'ы и `.env.example`. api и web — образы из GHCR
  (`APP_VERSION` в `.env`), выпуск — ручной запуск workflow `Release`. Установка —
  `deploy/install.sh` (с нуля, вплоть до первого администратора; в каталоге с `.env` отказывается),
  обновление — `deploy/update.sh`; логика обоих покрыта `*.tests.sh`. Образ `api` включает Typst CLI.
- ⚠️ Сборка сервера в **Release** требует ключ лицензии Six Labors (ImageSharp 4.x): в CI — секрет
  `SIXLABORS_LICENSE_KEY`, локально — `sixlabors.lic` в корне; Debug собирается без него.
- **Garage**: порт **3900**, ключи — `GK`+24 hex; без применённой раскладки отвечает отказом на всё —
  инициализирует идемпотентный `garage-init`. **Версия Garage записана в трёх местах** (поставка,
  дев-стенд, `FROM` в `Dockerfile.garage-init`) — сверяет сторож в CI.
- **Конвертер `converter`** (Gotenberg + LibreOffice): сеть внутренняя, порт на хост не публикуется —
  на дев-стенде API ходит к нему через nginx, `http://127.0.0.1:3910` (`OfficeConverter:BaseUrl`).
  Сервис объявлен **дважды** (поставка и дев-стенд), каждая строка настройки найдена пробой и без неё
  ломается молча, с кодом 200; сверяет `deploy/converter.tests.sh`.

📄 [DEV_NOTES, «Поставка»](docs/DEV_NOTES.md#поставка-прибитые-версии-переход-на-garage-dependabot).

## REST API

Полный листинг — в [docs/DEV_NOTES.md](docs/DEV_NOTES.md#rest-api-основные-маршруты); источник
истины — `src/server/BHS.CRG.Api/Endpoints/`. Здесь — только то, на чём легко ошибиться:

- `schema`, `data` и `content` в телах запросов — **строка с JSON**, а не объект. Отправленный
  объект даёт отказ, по которому это не очевидно.
- **DOCX не поддерживается — на ВЫХОДЕ**: единственный формат генерации — `Pdf`. На ВХОДЕ Word и
  Excel читаются (issue #1270): файл счёта `.docx`, `.xlsx`, `.xls` прикладывается, показывается
  читаемым образом и распознаётся по нему. Фраза — не запрет принимать офисные файлы.
- Запись конфигурации закрыта **правом, а не ролью**: типы и поля — `core.types.edit`, Typst-шаблоны и
  библиотека — `id.config.edit`, настройки экземпляра — `core.system.manage`. Политики по имени роли
  (`Admin`) в API нет и заводить её нельзя (issue #989).
- Ход долгих операций доставляется **поллингом** (`GET /api/jobs/active`), не сокетом.
- Правки источника набора данных, собранные по его копии на странице, **обязаны назвать `ifMatch`**:
  обработка, извлечение (`PUT /api/datasets/sources/{id}`) и «Сохранить как шаблон» — `processingVersion`,
  материализация — `materializationVersion`. Без него — 400, с устаревшим — 409 «источник тем временем
  изменили» (issue #1141).
- **Пишущие адреса сущностей модулей и общих данных обязаны назвать версию заголовком `If-Match`** —
  значение приходит в ответе чтения полем `version`. Касается счетов (`/api/costs/invoices/{id}/…`),
  накладных (`/api/costs/waybills/{id}/…`), записей общих данных (`PUT /api/common-data/{id}`,
  `PUT /api/employees/{id}`) и сохранённых представлений таблиц (`PUT` / `DELETE
  /api/tables/{адрес}/views/{id}`). Без заголовка — 400, с устаревшим — 409. Названные исключения и
  порядок отказов — в спецификациях `costs` и `core`.
- **Подсказки поля** (`GET /api/field-suggestions?type=&field=&q=&scope=&scopeId=&limit=`, issue #1530)
  различают ответы нарочно: поле, которое не подсказывается, — **200 и пусто**; неизвестный тип или
  поле — 400; таймаут (~300 мс) — **503, а не пусто**; нет прав ни на один источник — 403. `type` —
  любой тип семьи, где поле видно: объявившего сервер находит сам. Клиент зовёт адрес после ввода
  символа с задержкой, **не на фокус**: каждый вызов читает окно записей с данными.

Внутренняя кухня модуля `costs` (книга исправлений, взгляд «на момент закрытия», режимы НДС, удаление
черновиков), валюта учёта, назначение списков общих данных (`purpose`) и версия строки — в
[`openspec/specs/costs/spec.md`](openspec/specs/costs/spec.md) и
[`openspec/specs/core/spec.md`](openspec/specs/core/spec.md).

## Архитектура

### Доменные спецификации (OpenSpec) и ТЗ

- **Действующие спецификации** (построенное) — читать перед правкой соответствующей области:
  [`core`](openspec/specs/core/spec.md), [`auth`](openspec/specs/auth/spec.md),
  [`costs`](openspec/specs/costs/spec.md), [`id`](openspec/specs/id/spec.md),
  [`types-and-tags`](openspec/specs/types-and-tags/spec.md); остальные — `openspec/specs/`. Метки
  требований (`[CORE-*]`, `[AUTH-*]`, `[COST-*]`…) сохранены из ТЗ.
- **ТЗ будущих этапов** — вход для `/opsx:propose`; индекс и порядок этапов —
  [`docs/tz/README.md`](docs/tz/README.md), [`90-этапы.md`](docs/tz/90-этапы.md).
- **Нарезанные этапы** — зонтик `<модуль>-00-epic` (общая архитектура в `design.md`, перечень дочерних
  change и PR в `tasks.md`) и дочерние `<модуль>-NN-*`: этап 3 —
  [`plan-00-epic`](openspec/changes/plan-00-epic/design.md), этап 4 —
  [`work-00-epic`](openspec/changes/work-00-epic/design.md). Этап 3 строит механизмы, которых ждёт этап 4
  (контракт, право контракта, позиции перечня, навигация) — таблица «что взял» в D2 и D18 зонтиков.
- ⚠️ **Одна сессия — один change.** Если оказалось, что сначала нужен другой change, в этой сессии его
  не делают. Свою работу останавливают (несохранённое — WIP-коммитом в свою ветку), а тот change
  реализуют `/opsx:apply <change>` в чистой дочерней сессии со своим worktree. Текущая сессия ждёт, пока
  код того change и его архив не будут слиты (вместе с отметкой в зонтике), затем обновляет свою ветку
  от master и продолжает. Наступали в #1340: две ветки в одной копии, конфликты после squash-слияния и
  забытый архив.
- Настройка OpenSpec (контекст, правила артефактов) — `openspec/config.yaml`.

### Права и режимы работы

1. **Настройка** (права `core.types.edit`, `id.config.edit`, `core.system.manage`,
   `core.users.manage`; в поставке — у роли «Администратор»): типы документов (схема полей),
   Typst-шаблоны, привязки наборов данных/плагинов, пользователи, настройки.
2. **Генерация** (права модуля `id`, `id.document.*`): комплект `DocumentSet`, реквизиты, связи с
   объектами, наборы данных и документы качества → PDF.

Роли — записи Identity в базе, у пользователя их несколько; права роли — коды
`модуль.объект.действие`, объявленные модулями (`AUTH-1`); действующие права — объединение прав всех
ролей. Разграничение в UI и API — по праву, а не по имени роли: адрес модуля закрыт воротами модуля и
права (`AUTH-8`, `AUTH-9`).

### Инвариант: `CatalogScope` — не граница безопасности

`CatalogScope` (`Set` / `Section` / `Construction` / `System`) организует данные и задаёт приоритет
их разрешения. **Правами он не управляет.** Привязки данных к пользователю в системе нет вовсе:
любой вошедший видит и правит объекты всех уровней и всех строек. Это решение, а не упущение —
пользователи суть сотрудники одной компании с равным допуском (issue #675, 2026-08-05).
Не выдавайте проверку уровня за проверку прав.

**Условие пересмотра:** учётная запись выдана кому-то вне компании (заказчик, технадзор,
субподрядчик) — тогда изоляция данных обязательна и делается прежде остального. Затронет
наследование по поддереву, `_baseRef` из родительской области, провайдеры системных наборов,
библиотеку документов качества, инструменты MCP (они действуют правами пользователя) и подсказки
поля по ранее введённому (`/api/field-suggestions` отдаёт значения других строек).

### Пайплайн генерации документа

```
DomainObject с документной фасетой (реквизиты JSON + $ref-ссылки на объекты)
    │
    ▼ EntityResolver — разворачивает $ref, сливает _baseRef-наследование, подмешивает объекты общих данных
    ▼ DataSetResolver / QualityLinkResolver — наборы данных и документы качества (по функциональным тэгам)
    ▼
GenerationContext (единый JSON-контекст)
    │
    ▼ TypstGenerator: контекст → data.json; шаблон + typeblocks.typ + userlib.typ → Typst CLI (TYPST_PATH)
    ▼
PDF
```

Шаблон — **Typst-документ** (`Template.Content`). При генерации во временной папке создаются
`data.json` (контекст), `typeblocks.typ` (авто-сгенерированные функции отображения составных типов),
`userlib.typ` (общая библиотека, правит админ); картинки из data-URI материализуются в файлы
(`TypstImageMaterializer`). Отладка — `GET /api/generate/debug-bundle/{instanceId}` (ZIP со всеми этими
файлами) во внешнем Typst.

### Модель данных ядра (PostgreSQL)

```
DocumentType: схема полей JSONB, Kind (Composite / Document), привязки плагинов
DomainObject: тип, Data JSONB (реквизиты + _baseRef), расположение (ScopeLevel, ScopeId),
              Facet — только у документов (документ живёт в (Set, setId)). Заменил прежние
              EntityCatalog / CommonDataEntry / DocumentInstance (issue #84)
Template: documentTypeId, Content TEXT (Typst); версии, одна действующая (TemplateCurrentVersion)
Construction → Section → DocumentSet (комплект)
GeneratedFile: objectId (документ), формат (только Pdf), путь в хранилище, шаблон
```

### Плагины

```csharp
interface IDataSourcePlugin
{
    string Id { get; }
    EntitySchema[] ProvidedSchemas { get; }
    Task<SearchResult> SearchAsync(string entityType, string query, CancellationToken ct);
    Task<JsonDocument> FetchAsync(string entityType, string externalId, CancellationToken ct);
}
```

.NET-плагины загружаются через `AssemblyLoadContext`. HTTP-плагины работают через стандартный REST-контракт (те же методы, но по HTTP).

### Типы документов, которым нужны шаблоны

АОСР, ЖурналПрокладкиКабеля, КабельныйЖурнал, ВедомостьМатериалов, ПротоколИзмеренияИзоляции, ПротоколИзмеренияЗаземления, ПротоколИзмеренияМеталосвязи, ПротоколИзмеренияФазаНоль, РеестрДокументов, РеестрРабот, ВедомостьСхем, ТитульныйЛист, ПНР-документы (5 форм).
