# Объяснение геометки фото в правах Work

## Context

Граница изменения задана в [proposal.md](proposal.md) и [строке WORK-55 матрицы](../../work-00-epic/verification.md#объяснение-прав-для-фото-с-геометкой-work-55). Из [Work-17](../2026-11-21-work-17-photos/design.md) и [действующей спецификации обработки фото](../../../specs/core/spec.md#requirement-work-55-поворот-по-exif-применяется-геометка-сохраняется-данными) следует решение хранить геометку данными фото.

- `ExifReader` извлекает координаты из EXIF загруженного JPEG. `PhotoPreparer` и `ModulePhotosPort` передают их вместе с результатом обработки. `LinePhoto` сохраняет широту и долготу, при наличии высоту и время съёмки, в отдельных колонках. `LinePhotos` записывает подготовленные JPEG и превью. Источники — [ExifReader](../../../../src/server/BHS.CRG.Infrastructure/Generation/ExifReader.cs), [PhotoPreparer](../../../../src/server/BHS.CRG.Infrastructure/Generation/PhotoPreparer.cs), [ModulePhotosPort](../../../../src/server/BHS.CRG.Api/Modules/Ports/ModulePhotosPort.cs), [LinePhoto](../../../../src/server/BHS.CRG.Modules.Work/Data/LinePhoto.cs), [LinePhotos](../../../../src/server/BHS.CRG.Modules.Work/Photos/LinePhotos.cs).
- `ReportPhotoDto` в текущем отчёте и в представлении версии содержит идентификатор, состояние и, в зависимости от представления, имя, признак досылки или ссылку на версию фото. Геометка остаётся в записи фото. Адреса файла и превью возвращают подготовленный JPEG. В галерее пользователь с допуском видит изображение. Источники — [ReportViews](../../../../src/server/BHS.CRG.Modules.Work/Endpoints/ReportViews.cs), [ReportHistory](../../../../src/server/BHS.CRG.Modules.Work/Endpoints/ReportHistory.cs), [LinePhotos](../../../../src/server/BHS.CRG.Modules.Work/Photos/LinePhotos.cs), [галерея строки](../../../../src/client/src/features/work/entry/LinePhotos.tsx).

## Goals / Non-Goals

**Цель** — выполнить [дельту WORK-55](specs/work/spec.md) в существующих пояснениях и проверить полную читаемость текста. Выдача координат, обработка и хранение фото, ворота API, формы ответов, роли и оформление экрана лежат за пределами правки текста.

## Decisions

### Права, к которым относится текст

| Право | Доступ к фото при действующих проверках | Системная роль в поставке |
| --- | --- | --- |
| `work.report.own` | Просмотр фото своего отчёта и прикрепление в пределах правил редактирования и досылки | Монтажник |
| `work.report.review` | Просмотр фото отчёта в охвате приёмки, прикрепление в отчёте, который менеджер вправе редактировать. Приёмка — решение по отчёту в целом | Менеджер проекта |
| `work.report.read` | Просмотр фото доступных отчётов всех строек в режиме чтения | Руководитель через `*.read.all` и `ReadAllMark.In` |

Администратор в поставке получает все три. `work.sites.all` расширяет охват назначений и приёмки, в том числе доступ к фото **вместе** с `work.report.review`. Самостоятельного разрешения на фото у него нет. `work.facts.read` — отдельное право контракта `IActualWorks`. Ответ контракта содержит принятые объёмы и числовую диагностику. Тексты этих двух прав и составное `*.read.all` остаются вне правки. Источники — [WorkModule](../../../../src/server/BHS.CRG.Modules.Work/WorkModule.cs), [SystemRoles](../../../../src/server/BHS.CRG.Api/Auth/SystemRoles.cs), [PermissionCatalog](../../../../src/server/BHS.CRG.Modules/PermissionCatalog.cs), [PhotoEndpoints](../../../../src/server/BHS.CRG.Modules.Work/Photos/PhotoEndpoints.cs), [ActualWorksProvider](../../../../src/server/BHS.CRG.Modules.Work/Exchange/ActualWorksProvider.cs).

Доступ к фото и превью повторяет область чтения отчёта. Для черновика учитывается сотрудник отчёта или автор ввода, которому доступен этот отчёт. Для правок фото действуют проверки права записи, состояния, периода и досылки. Право чтения всех отчётов само по себе даёт просмотр. Правила одинаково существенны для формулировок о текущем фото и о фото из версии. Источники — [ReportJournalEndpoint](../../../../src/server/BHS.CRG.Modules.Work/Endpoints/ReportJournalEndpoint.cs), [ReportReads](../../../../src/server/BHS.CRG.Modules.Work/Data/ReportReads.cs), [LinePhotos](../../../../src/server/BHS.CRG.Modules.Work/Photos/LinePhotos.cs).

### Где разместить пояснение

Добавить пояснение в `Opens` трёх записей `AppPermission` в `WorkModule.Permissions`. Сохранить назначение и границы каждого права. Для приёмки явно упомянуть доступ к фото в пределах её охвата. Опорная формулировка — «Если в исходном снимке есть читаемая геометка, её координаты сохраняются отдельно как данные фото». Условия и действия пользователя описывать в соответствующем праве. Формулировка касается геометки из файла и хранения. Она не обещает чтение координат в карточке, запрос геолокации устройства, наличие GPS у любого снимка или другие свойства обработки и защиты файлов.

`RoleEditor.PermissionGroups` выдаёт `Gives` и `Opens` как `gives` и `opens` в `GET /api/roles/permissions` под `core.users.manage`. `GET /api/account/permissions` берёт те же метаданные и оставляет действующие права вошедшего. «Роли и права» и «Мои права» уже показывают оба поля. Во втором поле редактора есть перенос по ширине и прокрутка области подробностей. Короткая фраза в источнике покрывает оба экрана. Проверку переноса выполнить на собранном приложении. Источники — [RoleEditor](../../../../src/server/BHS.CRG.Api/Auth/RoleEditor.cs), [RoleEndpoints](../../../../src/server/BHS.CRG.Api/Endpoints/Users/RoleEndpoints.cs), [AccountEndpoints](../../../../src/server/BHS.CRG.Api/Endpoints/Account/AccountEndpoints.cs), [RolesPage](../../../../src/client/src/features/settings/RolesPage.tsx), [MyPermissionsSection](../../../../src/client/src/features/account/MyPermissionsSection.tsx).

Оба руководства уже описывают хранение геометки. В руководстве администратора также есть обязанность уведомить монтажников перед началом работы с фото. Адрес правки — пояснения прав. Добавление текста лишь в руководство оставило бы пробел в редакторе ролей. Источники — [руководство администратора](../../../../docs/ADMIN_GUIDE.md), [руководство пользователя](../../../../docs/USER_GUIDE.md).

## Risks / Trade-offs

- Ложное обещание доступности координат → назвать отдельно просмотр фото и сохранение геометки. Координаты в редакторе прав служат предметом объяснения, а не данными ответа.
- Обрезка длинного пояснения → сохранить короткую фразу и проверить её при обычной и минимальной поддерживаемой ширине редактора, включая последнюю строку и прокрутку. Использовать существующую компоновку.
- Случайная правка доступа или истории → проверить разницу каталогов и применить существующие проверки ролей, ворот, фото и версий отчёта.

## Migration Plan

Текст поставляется вместе с обычным обновлением приложения. Откат — возврат предыдущего текста в `WorkModule.Permissions`.
