# Tasks: просмотрщик PDF — компонент и вид для чтения счёта

## 0. До кода

- [ ] 0.1 Три варианта макета в Claude Design (`../viewer-00-epic/design.md`, D10) и выбор владельца.
  Варианты покрывают:
  - панель в колонке 340 и 620 px и при ширине `< 420 px`;
  - состояния из [UI-17];
  - режим поверх экрана;
  - тёмную тему.
  Проверка — макет, вариант и ссылка вписаны в `design.md`, раздел «Макеты», и в описание PR

## 1. Пакет, сборка, поставка

- [ ] 1.1 `pdfjs-dist` ровно `6.4.299` в `package.json` и `package-lock.json` [UI-21]. Проверка — `npm ci` чистый,
  версия без `^`
- [ ] 1.2 Плагин `pdfjsAssets()` в `vite.config.ts`: `wasm/`, `iccs/`, `cmaps/`, `standard_fonts/` →
  `dist/pdfjs/<версия>/`, в dev — из `node_modules` [UI-21]. Проверка — после `npm run build` каталоги на
  месте; сторож упаковки (vitest, `node`) сверяет имена `.wasm`/`.icc` из `pdf.worker.mjs` со списком
  плагина и краснеет на выдуманном имени
- [ ] 1.3 `worker: { format: 'es' }` и worker через `?worker` в `pdfEngine.ts` [UI-21]. Проверка — в
  `dist` нет `*.mjs` (шаг после `npm run build` в работе «Frontend» `ci.yml`, имя работы не меняется);
  редактор шаблонов (Monaco) открывается в `vite preview`
- [ ] 1.4 `preview.headers` с CSP из `deploy/nginx.conf` и тест сверки строк [UI-22]. Проверка — тест
  краснеет на правке строки в `nginx.conf`; живые прогоны зелёные под этой политикой
- [ ] 1.5 `no-restricted-imports` на `pdfjs-dist*` вне `shared/ui/viewer/pdfEngine.ts` [UI-10]. Проверка —
  пробный импорт в `features/` краснит `npm run lint:ratchet`
- [ ] 1.6 `.github/dependabot.yml`: экосистема `npm`, `/src/client`, `allow` только `pdfjs-dist`; пункт 5
  шапки переписан [UI-21]. Проверка — ревью PR; `actionlint` и проверка YAML в CI зелёные

## 2. Компонент `shared/ui/viewer`

- [ ] 2.1 `viewerState.ts` и `viewerState.test.ts`: шаги и границы масштаба, «по ширине» и «страница
  целиком» с поворотом, окно видимых страниц, якорь, потолок пикселей, номер страницы [UI-11] [UI-16].
  Проверка — `npm test -- viewerState` зелёный
- [ ] 2.2 `pdfEngine.ts`: загрузка байтами со свежей копией, `destroy`, перевод исключений
  (`InvalidPDFException`, `PasswordException`, сбой worker, ноль страниц), `TextLayer` [UI-10] [UI-12]
  [UI-17]. Проверка — тест настоящего движка в Node на `deploy/converter-reference.pdf`: верное число
  листов, кириллица в `getTextContent`; поддельный `%PDF-1.4 не настоящий` → «не PDF»
- [ ] 2.3 `PdfViewer.tsx` (lazy, проверка возможностей браузера) и `PdfViewerImpl.tsx` с лентой, ленивым
  рисованием, слоем текста, тёмной темой и доступностью [UI-10] [UI-12] [UI-16] [UI-17] [UI-18] [UI-19].
  Проверка — тест компонента с подменённым движком:
  - «стр. 3 из 12», кнопки на границах;
  - тексты отказов вместе с `actions`;
  - новый `documentKey` сбрасывает позицию;
  - `destroy` вызван ровно раз при размонтировании;
  - старый браузер → отказ без загрузки движка;
  - сбой ленивого куска → «приложение обновилось»
- [ ] 2.4 `ViewerToolbar.tsx`: страница, меню масштаба по образцу `LayoutChooser`, поворот, порог 420 px
  container query, режим масштаба в `localStorage` под try/catch [UI-11] [UI-19]. Проверка — тест:
  одностраничный — блока страниц нет; узкий — `−`/`+` в меню; хранилище бросает — показ работает
- [ ] 2.5 Клавиши: Ctrl+колесо (`passive: false`), Ctrl±, Ctrl+0, Home/End — только с фокусом или
  указателем над листом; строка в `ShortcutsHelp` [UI-15]. Проверка — тест: голые `+`/`−` и Ctrl+F не
  перехвачены
- [ ] 2.6 `printPdf.ts` и Ctrl+P; без встроенного PDF — «Открыть во вкладке» [UI-13]. Проверка — тест
  компонента: Ctrl+P с фокусом внутри зовёт печать файла, снаружи — нет. Ручная проверка Chrome, Firefox,
  Safari — в описании PR
- [ ] 2.7 `openInNewTab.ts`: синхронное окно, свой тип, отложенный отзыв, вариант с загрузчиком [UI-20].
  Проверка — тест: `window.open` вызван до разрешения загрузчика; тип `text/html` от сервера не
  проходит
- [ ] 2.8 Режим поверх экрана (`Modal fullScreen headerless`) [UI-14]. Проверка — тест: Esc закрывает,
  документ уничтожен один раз

## 3. Вид для чтения счёта

- [ ] 3.1 `loadInvoiceImage` → `Blob`; `BuiltImage` на просмотрщике, `documentKey =
  invoiceId|blobPath|builtAt`, «Открыть во вкладке», область `relative isolate overflow-hidden`
  [COST-6.2] [UI-10] [UI-20]. Проверка — `npx tsc -b`; тесты `features/costs` зелёные
- [ ] 3.2 `invoices-smoke.mjs:481` переписан [COST-6.2] [UI-12]:
  - область `getByRole('region', { name: /вид для чтения/ })`;
  - canvas первого листа не пуст (`getImageData` не только белое);
  - «из N» по числу листов `converter-reference.xlsx`;
  - выделение на листе даёт ожидаемую строку;
  - прямоугольник `image-caveat` не пересекается с областью листа.
  Проверка — живой прогон зелёный в CI
- [ ] 3.3 Правило `native-pdf-frame` в `scripts/ui-ratchet-lib.mjs` (теги и `createElement`, `features/`),
  случаи в `ui-ratchet.test.mjs`, уровень — три оставшихся места [UI-23]. Проверка — `npm run ui:ratchet`
  зелёный, пробный `<iframe>` в новом файле `features/` красный

## 4. Документы и приёмка

- [ ] 4.1 `docs/USER_GUIDE.md` 13.4 (панель, «Открыть во вкладке», Ctrl+P) и `npm run toc` в `docs/tools`.
  Проверка — проверка оглавления в работе «Frontend» зелёная
- [ ] 4.2 `docs/UI.md` (просмотрщик в таблице контролов, `native-pdf-frame`) и `docs/DEV_NOTES.md`
  («Поставка»: worker, каталог `pdfjs/<версия>/`, dependabot, `@napi-rs/canvas`). Проверка — ревью PR
- [ ] 4.3 Ручная приёмка (D8 зонтика): вид для чтения показан в dev, в `vite preview` и в собранном образе
  `web` (`docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.build.yml`). Из вида
  копируются номер, ИНН, сумма; Ctrl+P печатает файл; основной кусок сборки не вырос (размер
  `index-*.js` до и после). Проверка — описание PR

## 5. Верификация

- [ ] 5.1 `cd src/client && npm test` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный: файлы `shared/ui/viewer` ниже 500 строк кода
- [ ] 5.5 Сервер не менялся: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "FullyQualifiedName~InvoiceOfficeFile"` — зелёный (контракт `/scan/image/content` прежний)

## Workflow follow-up

- PR: метки `version:minor` и `ui`; «Closes #1490».
