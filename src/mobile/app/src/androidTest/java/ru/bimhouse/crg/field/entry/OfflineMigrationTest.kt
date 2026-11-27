package ru.bimhouse.crg.field.entry

import androidx.room.testing.MigrationTestHelper
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class OfflineMigrationTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext
    private val owner = ReportOwner("migration-test", UUID.randomUUID())

    @get:Rule
    val helper = MigrationTestHelper(instrumentation, OfflineDatabase::class.java)

    @After
    fun cleanUp() {
        context.deleteDatabase(owner.databaseName())
    }

    @Test
    fun opensVersionOneWithSameReportLinesAndPendingActions() = runBlocking {
        val reportId = UUID.randomUUID()
        val rowId = UUID.randomUUID().toString()
        val createId = UUID.randomUUID().toString()
        val submitId = UUID.randomUUID().toString()
        val actionTime = "2026-11-16T16:00:00Z"
        val line = WorkLine(id = rowId, volume = "12.25")
        val report = ReportRecord(
            id = reportId.toString(),
            constructionId = null,
            unlistedSiteName = "Новый объект",
            companyZoneId = "Asia/Vladivostok",
            siteZoneId = null,
            windowZoneId = "Asia/Vladivostok",
            date = "2026-11-16",
            shift = Shift.NIGHT,
            windowEndsAt = "2026-11-17T14:00:00Z",
            hours = "8",
            note = null,
            status = ReportStatus.SUBMITTED,
            submittedAt = actionTime,
            revision = 2,
        )
        val submitted = LocalReport(report, listOf(line))
        val draft = submitted.copy(report = report.copy(status = ReportStatus.DRAFT, submittedAt = null, revision = 1))

        helper.createDatabase(owner.databaseName(), 1).use { legacy ->
            legacy.execSQL("""
                INSERT INTO reports (
                    id, unlistedSiteName, companyZoneId, windowZoneId, date, shift,
                    windowEndsAt, hours, status, submittedAt, revision
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """.trimIndent(), arrayOf<Any?>(
                report.id, report.unlistedSiteName, report.companyZoneId, report.windowZoneId, report.date,
                "NIGHT", report.windowEndsAt, report.hours, "SUBMITTED", actionTime, 2,
            ))
            legacy.execSQL("""
                INSERT INTO report_lines (reportId, position, id, volume, isCrew, materials, participants)
                VALUES (?, 0, ?, ?, 0, '[]', '[]')
            """.trimIndent(), arrayOf(report.id, rowId, line.volume))
            val insertAction = """
                INSERT INTO report_outbox (sequence, actionId, reportId, action, deviceTime, snapshotJson)
                VALUES (?, ?, ?, ?, ?, ?)
            """.trimIndent()
            legacy.execSQL(insertAction, arrayOf<Any>(4, createId, report.id, "CREATE", actionTime, Json.encodeToString(draft)))
            legacy.execSQL(insertAction, arrayOf<Any>(5, submitId, report.id, "SUBMIT", actionTime, Json.encodeToString(submitted)))
        }

        OfflineReportStore.open(context, owner).use { store ->
            val migrated = requireNotNull(store.report(reportId))
            assertEquals(submitted, migrated)
            assertNull(migrated.lines.single().variantId)
            val pending = store.pendingActions()
            assertEquals(listOf(4L, 5L), pending.map { it.sequence })
            assertEquals(listOf(createId, submitId), pending.map { it.actionId })
            assertEquals(listOf(draft, submitted), pending.map { it.snapshot() })
            assertEquals(listOf(ReportAction.CREATE, ReportAction.SUBMIT), pending.map { it.action })
        }
    }
}
