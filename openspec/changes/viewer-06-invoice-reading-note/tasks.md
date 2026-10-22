# Tasks: строка-оговорка вида для чтения в одну строку

## 0. До кода

- [ ] 0.1 Три варианта строки-оговорки в Claude Design (`design.md`, «Макеты») и выбор владельца. Варианты
  показаны в колонке 340 и 620 px со всеми состояниями чипа. Проверка — макет, вариант и ссылка вписаны
  в `design.md` и в описание PR
- [ ] 0.2 Разбор правки контракта `ModuleRenditionLayout.Note` с Архитектором. Проверка — вывод в описании
  PR до кода

## 1. Сервер: пометка о раскладке отдельно

- [ ] 1.1 `ExcelPageLayout.IsLayoutNote` и тест на четыре формы `AppliedNote`, `FailedNote` и чужие пометки
  (`SmallTextNote`, `SheetsNote`, `UncheckedNote`, потерянный текст) [COST-6.2]. Проверка —
  `ExcelPageLayoutTests` зелёный
- [ ] 1.2 `ModuleRenditionLayout.Note`; `ModuleRenditionsPort` отделяет пометку о раскладке от `Notes` при
  чтении; `InvoiceImageView.LayoutNote` [COST-6.2]. Проверка — интеграционный тест: у книги по профилю
  `layoutNote` с именем профиля и без дубля в `notes`; у PDF и Word `layoutNote` = `null`; незнакомая
  пометка остаётся в `notes`; ворота модуля и права адреса не изменились (`EndpointGateInventoryTests`
  зелёный)

## 2. Клиент: одна строка

- [ ] 2.1 `InvoiceImage.layoutNote` в `shared/api/invoiceImage.ts`; строка-оговорка по выбранному макету:
  фраза, чип, «Перестроить», перенос в `< 420 px` (container query) [COST-6.2]. Проверка — `npx tsc -b`
- [ ] 2.2 Подсказка чипа (`Tooltip` вокруг `Popover.Trigger`): `layoutNotice` + `layoutNote`. Без чипа — прежние
  строки. `data-testid="layout-notice"` на тексте состояния [COST-6.2]. Проверка —
  `InvoiceFileImage.test.tsx` проходит все состояния: `changed`, `deleted`, `none`, «не применён», `file`, не
  книга. При показанном чипе пометка о раскладке не дублируется строкой; пометка другого рода
  выводится
- [ ] 2.3 Живой прогон `invoice-layout-checks.mjs` переписан под новую строку и подсказку; прямоугольник
  `image-caveat` не пересекается с областью листа [COST-6.2]. Проверка — живой прогон зелёный в CI
- [ ] 2.4 `docs/USER_GUIDE.md` 13.4, «Раскладка на страницы — по профилю печати»: состояния и чип — в одной
  строке и подсказке чипа. Проверка — ревью PR; проверка оглавления в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "FullyQualifiedName~ExcelPageLayout|FullyQualifiedName~PrintProfile|FullyQualifiedName~InvoiceOfficeFile|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory"`
  — зелёный
- [ ] 3.2 `cd src/client && npm test` — зелёный
- [ ] 3.3 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.4 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.5 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный

## Workflow follow-up

- PR: метки `version:minor` и `ui`; «Closes #1495».
