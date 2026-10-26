# Design: закрыть фреймы

Общая архитектура — `../viewer-00-epic/design.md` (D3, D8, D9, D12). Здесь только решения этого change.

## Context

Сверено по master `c9586405`. Это состояние ДО `viewer-01`…`04`; к началу этого change оно сдвинется.
- **Фреймы.** `<iframe>` в клиенте только в четырёх местах `features/`, их переводят `viewer-01`…`04`. В
  `shared/` фреймов и `createElement('iframe')` нет. Строка `WeakMap<object, unknown[]>`
  (`shared/utils/rowIdentity.ts:46`) — не фрейм, и правило обязано её не считать (`viewer-01`, разбор по
  дереву).
- **Храповик** (`scripts/ui-ratchet-lib.mjs`): исключений по пути нет; `dirs` и `everywhere` — только
  каталоги верхнего уровня `src/`. У `native-title` законен `title` у `iframe` (`:399`). То же записано в
  `docs/UI.md` (строка «Подсказка» и абзац о `native-title`).
- **CSP поставки** (`deploy/nginx.conf:23`): `frame-src 'self' blob:`. Комментарий (`:14–22`)
  объясняет `'unsafe-eval'` только вычисляемыми полями, а `blob:` у frame — «предпросмотром PDF».
- **`window.open`** после `viewer-02` и `viewer-04` остаётся только в `openAttachmentInNewTab`
  (`shared/api/attachments.ts:138`, `:142`). Его зовут:
  - `features/quality-docs/QualityDocForm.tsx:320`, `QualityDocsPage.tsx:219`;
  - `features/datasets/SourcePreviewDialog.tsx:48`;
  - `features/document-sets/editor/LinkPickerModal.tsx:209`;
  - `features/settings/BugReportsPage.tsx:217` — снимок экрана, картинка.

## Goals / Non-Goals

**Goals:** храповик на нуле по `features/` и `shared/`; `frame-src` только для фрейма печати; одна
функция открытия вкладки.

**Non-Goals:** `object-src`, `worker-src` и прочие директивы не трогаются. Заголовки поставки, кроме
CSP, — тоже.

## Decisions

- **Исключение по пути в храповике.** Область правила решается по пути файла, а не по каталогу обхода.
  Механизм вводит открытый change `sharedui-ratchet-shared-scope` (#1469, D1: `watches(rule, file)`,
  область по умолчанию — `features/` и `shared/` без `shared/ui/`).
  - Тот change слит раньше — `native-pdf-frame` получает свою область на его механизме: `features/` и
    весь `shared/`, кроме `shared/ui/viewer/`.
  - Не слит — минимальный вариант того же механизма вводится здесь: поле `skip` с префиксами путей от
    `src/`, `countViolations` получает путь. Тот change тогда строится на нём.
  - Тест: файл под `shared/ui/viewer/` не считается, соседний `shared/ui/X.tsx` — считается.
  - Альтернатива — держать файл печати в уровне с 1 (как `WindowStep` у `portal-container`).
    Отвергнута: issue требует уровня 0 и «кроме `shared/ui/viewer`», а уровень с единицей молча
    пропустил бы второй фрейм в том же файле.
- **Область правила** — `features/` и `shared/` вместе с `shared/ui/`, кроме `shared/ui/viewer/`. Кнопки
  и окна `shared/ui` — первые, кто мог бы завести фрейм. `scopeNote` говорит «ещё в shared/, кроме
  shared/ui/viewer».
- **`native-title`.** Ветка `tag !== 'iframe'` удаляется, тест на неё переписан: фреймов нет, исключение
  мёртвое.
- **CSP.** `frame-src blob:` — без `'self'`: фреймов своего источника в клиенте нет, а печать грузит
  `blob:`.
  - `'none'` из разбора Архитектора не берём: тогда не будет фрейма печати (решение владельца, D3).
  - Комментарий: `blob:` у frame — только фрейм печати просмотрщика; `'unsafe-eval'` держит ещё и
    wasm pdf.js. Без него декодеры уйдут на медленный JS — ломается не молча, но медленно.
- **`openAttachmentInNewTab`** — обёртка над `openInNewTab` с загрузчиком `loadAttachmentBlob`
  (`viewer-03`). Поправки по ревью PR #1637:
  - **Тип вне белого списка скачивается**, а не подписывается PDF (опция `saveUnknown`): форма документа
    качества принимает `.tif`, и подписанный PDF скан давал во вкладке ошибку вместо файла. Только для
    вложений — байты просмотрщика без опции идут как PDF, как он их и показывает.
  - **Белый список один** — `shownTypeOf` в `openInNewTab.ts`; `VIEWABLE_TYPES` вложений убран (уже
    расходился: без `image/bmp`). Имя файла — из `Content-Disposition`, иначе последняя часть пути.
  - **Отказ виден на месте**: места зовут `useOpenAttachment` (тост `apiError`), а не `void …` — иначе
    `TabBlocked` и отказ загрузки уходили в необработанный промис, и кнопка молчала.
- **Сторож одной функции** — `no-restricted-properties` (`window.open`, `globalThis.open`, `self.open`,
  разбор) и `no-restricted-globals` (голый `open`) в `eslint.config.js`, кроме
  `shared/ui/viewer/openInNewTab.ts` и тестов (те смотрят на вызов). Новое правило в храповике линта
  стартует с нуля.

## Risks / Trade-offs

- [Оставшийся фрейм молча опустеет под суженным `frame-src`] → перед правкой CSP — grep по всему
  `src/client/src` (`<iframe`, `createElement('iframe'`); храповик с уровнем 0 и `shared/`.
- [Печать Ctrl+P сломается, если `frame-src` сузить сильнее нужного] → ручная проверка печати в
  собранном образе `web` в описании PR; сторож CSP держит `vite preview` на той же строке.
- [`viewer-01` слит, а места ещё нет] → этот change стартует только после `viewer-02`…`04`.
