package ru.bimhouse.crg.field.entry

import android.content.Context
import androidx.room.withTransaction
import kotlinx.serialization.json.Json
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Идентификатор действия назначает вызывающий и повторно использует при повторе сохранения. */
data class LocalAction(val id: UUID, val deviceTime: Instant)

class OfflineReportStore internal constructor(private val db: OfflineDatabase) : AutoCloseable {
    private val dao = db.dao()

    suspend fun report(id: UUID): LocalReport? = db.withTransaction {
        dao.report(id.toString())?.let { load(it) }
    }

    suspend fun reports(): List<LocalReport> = db.withTransaction {
        dao.reports().map { load(it) }
    }

    suspend fun pendingActions(): List<PendingReportAction> = dao.pending()

    suspend fun suggested(placement: ReportPlacement, now: Instant): SuggestedShift {
        val zone = SubmissionWindow.effectiveZone(placement.companyZone, placement.siteZone)
        val today = now.atZone(zone).toLocalDate()
        val nightEndsAt = dao.nightWindowEndTimes(
            placement.constructionId?.toString(), placement.unlistedSiteName, today.minusDays(1).toString(),
        ).maxOfOrNull { Instant.parse(it) }
        return SubmissionWindow.suggested(today, now, nightEndsAt)
    }

    suspend fun create(id: UUID, input: ReportInput, action: LocalAction): LocalReport =
        change(id, action, ReportAction.CREATE) { key ->
            val placement = input.placement
            val zone = SubmissionWindow.effectiveZone(placement.companyZone, placement.siteZone)
            val report = ReportRecord(
                id = key,
                constructionId = placement.constructionId?.toString(),
                unlistedSiteName = placement.unlistedSiteName,
                companyZoneId = placement.companyZone.id,
                siteZoneId = placement.siteZone?.id,
                windowZoneId = zone.id,
                date = input.date.toString(),
                shift = input.shift,
                windowEndsAt = SubmissionWindow.endsAt(zone, input.date, input.shift).toString(),
                hours = input.hours,
                note = input.note,
                status = ReportStatus.DRAFT,
                submittedAt = null,
                revision = 1,
            )
            dao.insert(report)
            replaceLines(key, input.lines)
            LocalReport(report, input.lines)
        }

    suspend fun edit(id: UUID, revision: Long, input: ReportInput, action: LocalAction): LocalReport =
        change(id, action, ReportAction.EDIT) { key ->
            val current = requireRevision(key, revision)
            check(current.status != ReportStatus.ACCEPTED) { "Принятый отчёт закрыт для правки." }
            val placement = input.placement
            val constructionId = placement.constructionId?.toString()
            val date = input.date.toString()
            val keyChanged = current.constructionId != constructionId ||
                current.date != date || current.shift != input.shift
            check(!keyChanged || current.status.keyEditable) { "Стройка, дата и смена поданного отчёта зафиксированы." }
            val zone = SubmissionWindow.effectiveZone(placement.companyZone, placement.siteZone)
            val report = current.copy(
                constructionId = constructionId,
                unlistedSiteName = placement.unlistedSiteName,
                companyZoneId = if (keyChanged) placement.companyZone.id else current.companyZoneId,
                siteZoneId = if (keyChanged) placement.siteZone?.id else current.siteZoneId,
                windowZoneId = if (keyChanged) zone.id else current.windowZoneId,
                date = date,
                shift = input.shift,
                windowEndsAt = if (keyChanged) {
                    SubmissionWindow.endsAt(zone, input.date, input.shift).toString()
                } else {
                    current.windowEndsAt
                },
                hours = input.hours,
                note = input.note,
                revision = current.revision + 1,
            )
            dao.update(report)
            replaceLines(key, input.lines)
            LocalReport(report, input.lines)
        }

    suspend fun submit(id: UUID, revision: Long, action: LocalAction): LocalReport =
        change(id, action, ReportAction.SUBMIT) { key ->
            val current = requireRevision(key, revision)
            check(current.status.keyEditable) { "Отчёт уже подан." }
            check(current.constructionId != null || !current.unlistedSiteName.isNullOrBlank()) {
                "Укажите название стройки."
            }
            val submittedAt = SubmissionWindow.momentOfManual(action.deviceTime, Instant.parse(current.windowEndsAt))
            val report = current.copy(
                status = ReportStatus.SUBMITTED, submittedAt = submittedAt.toString(), revision = current.revision + 1,
            )
            dao.update(report)
            load(report)
        }

    /** Подтверждённая привязка этого отчёта сохраняет исходные дату, смену, пояс и окно. */
    suspend fun applyConfirmedSite(id: UUID, constructionId: UUID, siteZone: ZoneId?): LocalReport =
        db.withTransaction {
            require(siteZone == null || siteZone.id in ZoneId.getAvailableZoneIds())
            val current = requireNotNull(dao.report(id.toString()))
            val targetId = constructionId.toString()
            check(current.constructionId == null || current.constructionId == targetId)
            if (current.constructionId == targetId) return@withTransaction load(current)
            val report = current.copy(
                constructionId = targetId,
                unlistedSiteName = null,
                siteZoneId = siteZone?.id,
                revision = current.revision + 1,
            )
            dao.update(report)
            load(report)
        }

    private suspend fun change(
        id: UUID,
        action: LocalAction,
        kind: ReportAction,
        write: suspend (String) -> LocalReport,
    ): LocalReport = db.withTransaction {
        val key = id.toString()
        val actionId = action.id.toString()
        dao.action(actionId)?.let { previous ->
            check(previous.reportId == key && previous.action == kind)
            return@withTransaction previous.snapshot()
        }
        val saved = write(key)
        dao.enqueue(PendingReportAction(
            actionId = actionId,
            reportId = key,
            action = kind,
            deviceTime = action.deviceTime.toString(),
            snapshotJson = Json.encodeToString(saved),
        ))
        saved
    }

    private suspend fun requireRevision(key: String, revision: Long): ReportRecord {
        val report = requireNotNull(dao.report(key)) { "Отчёт не найден." }
        check(report.revision == revision) { "Отчёт уже изменён." }
        return report
    }

    private suspend fun replaceLines(reportId: String, lines: List<WorkLine>) {
        dao.deleteLines(reportId)
        dao.insertLines(lines.mapIndexed { index, line -> ReportLineRecord(reportId, index, line) })
    }

    private suspend fun load(report: ReportRecord): LocalReport =
        LocalReport(report, dao.lines(report.id).map { it.value })

    override fun close() = db.close()

    companion object {
        fun open(context: Context, owner: ReportOwner): OfflineReportStore =
            OfflineReportStore(OfflineDatabase.open(context, owner))
    }
}
