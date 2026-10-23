# Tasks: предел частоты оформления — настройка развёртывания

Заведено задним числом: всё ниже построено и слито PR #1597 (23.10.2026). `<Version>` не трогать.

## 1. Сервер

- [x] 1.1 `BrandingRateLimit`: разбор `RateLimits:Branding` — пусто — 300, допустимо 300…30000, негодное
  — отказ запуска с причиной; цифры сверх int — выход за диапазон [CORE-25.1]. Проверка —
  `BrandingRateLimitTests`
- [x] 1.2 Политики `branding` и `branding-icons` берут предел из настройки, значки — с множителем 4;
  разбор — при регистрации, а не в ленивых опциях ограничителя [CORE-25.1]. Проверка —
  `BrandingRateLimitTests.Default_KeepsShippedLimits`, `RateLimitPolicyCoverageTests`

## 2. Поставка и документация

- [x] 2.1 `BRANDING_RATE_LIMIT` в `deploy/docker-compose.yml`, `deploy/.env.example` и таблице
  переменных `docs/DEPLOYMENT.md`; `update.sh` дописывает новую пустую переменную сам [CORE-25.1]
- [x] 2.2 `docs/DEV_NOTES.md`: предел оформления — настройка, значки следуют за ним вчетверо, стенд CI

## 3. CI (вне спецификации)

- [x] 3.1 `ci.yml`: оба запуска сервера живых прогонов — `RateLimits__Branding: 3000`
- [x] 3.2 Обвязка живых прогонов: 429 оформления с любой страницы — провал набора (`brandingRefusals`),
  проверка обвязки `branding-refusal-fails-the-summary`

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "FullyQualifiedName~BrandingRateLimitTests|FullyQualifiedName~RateLimitPolicyCoverage|FullyQualifiedName~FileSizeRatchet"`
- [x] 4.2 Весь список работы «Живые прогоны (браузер)» на стенде «как в CI»: 20 из 20, ни одного 429
  оформления; 350 запросов оформления подряд при пределе 3000 — все 200
- [x] 4.3 CI PR #1597 зелёный по всем работам
