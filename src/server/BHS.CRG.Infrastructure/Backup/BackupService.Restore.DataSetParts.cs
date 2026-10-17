using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.DataSets;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

// Части PDF-наборов после восстановления (ревью PR #1408): о копии без частей отчёт обязан сказать.
public partial class BackupService
{
    /// <summary>
    /// Наборы восстановлены — а вырезанные из них PDF документов есть? (issue #1399)
    ///
    /// <para>Отказ обязан выглядеть отказом. Копия, снятая до #1399, частей не несёт, а отчёт без этой
    /// проверки писал «Файлы: восстановлено N из N» — и документы потом печатались без вложений, без
    /// единой ошибки. Предупреждение в руководстве администратора помогает только тому, кто его
    /// прочёл; отчёт восстановления читают все. Тот же приём, что у файлов модулей
    /// (<see cref="IModuleSchemaBackup.WarnAboutMissingFilesAsync" />).</para>
    ///
    /// <para>Пути берутся тем же сбором, что у копии (<see cref="CollectGroupingParts" />,
    /// <see cref="CollectRowFilePaths" />): о частях копия и проверка не разойдутся. Хранилище
    /// спрашивают только о путях, которых НЕТ В АРХИВЕ, — у исправной копии вопросов к нему нет вовсе.
    /// Часть, уцелевшая на этой установке, отказом не считается. Предупреждения сбора (непрочитанное
    /// разбиение) здесь не повторяются: их копия сказала паспортом при снятии.</para>
    ///
    /// <para>Отказ самой проверки восстановление не роняет: база уже зафиксирована.</para>
    /// </summary>
    private async Task WarnAboutDataSetPartsAsync(
        BackupManifest manifest, HashSet<string> archived, List<string> warnings, CancellationToken ct)
    {
        var files = manifest.DataSetFiles ?? [];
        if (files.Length == 0) return;

        try
        {
            var registries = (manifest.DataSetSources ?? [])
                .Where(s => s.SheetOrPath == PdfProfiles.GostDocumentsMarker)
                .ToLookup(s => s.FileId);
            var present = new Dictionary<string, bool>(StringComparer.Ordinal);
            var lacking = new List<string>();

            foreach (var file in files)
            {
                var parts = new HashSet<string>(StringComparer.Ordinal);
                CollectGroupingParts(file, parts, []);
                foreach (var source in registries[file.Id]) CollectRowFilePaths(source, parts, []);

                var absent = 0;
                foreach (var part in parts)
                {
                    if (archived.Contains(part)) continue;
                    if (!present.TryGetValue(part, out var here))
                        present[part] = here = await blob.GetSizeAsync(part, ct) is not null;
                    if (!here) absent++;
                }

                if (absent > 0) lacking.Add($"«{file.Name}» — {absent} из {parts.Count}");
            }

            if (lacking.Count == 0) return;

            warnings.Add(
                "В копии нет вырезанных PDF документов, и в хранилище они не найдены: " +
                $"{string.Join("; ", lacking)}. Наборы вернулись, но документы с этими вложениями будут " +
                "печататься без них. Так выглядит копия, снятая прежней версией, которая части не брала, " +
                "или копия, при снятии которой их не оказалось в хранилище. Части вырезаются заново из " +
                "файла набора: откройте у набора «Разбиение» и сохраните его.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось проверить части PDF-наборов после восстановления");
            warnings.Add(
                "Данные восстановлены, но проверить, на месте ли вырезанные PDF документов наборов, не " +
                $"удалось: {ex.Message}. Откройте вложения у нескольких документов из копии.");
        }
    }
}
