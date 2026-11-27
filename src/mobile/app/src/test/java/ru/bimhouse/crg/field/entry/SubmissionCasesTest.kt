package ru.bimhouse.crg.field.entry

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.runner.RunWith
import org.junit.runners.Parameterized
import org.junit.Test
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.ZoneId

@RunWith(Parameterized::class)
class SubmissionCasesTest(private val name: String, private val case: SubmissionCase) {
    @Test
    fun matchesServerExample() {
        val date = LocalDate.parse(case.date)
        val companyZone = ZoneId.of(case.companyZone)
        val siteZone = case.siteZone?.let(ZoneId::of)
        val zone = SubmissionWindow.effectiveZone(companyZone, siteZone)
        val shift = Shift.parse(case.shift)
        val endsAt = SubmissionWindow.endsAt(zone, date, shift)

        assertEquals(case.expect.windowEndsAt.instant(), endsAt)
        assertEquals(case.expect.momentOfManual.instant(),
            SubmissionWindow.momentOfManual(case.deviceTime.instant(), endsAt))

        val now = case.now.instant()
        val today = now.atZone(zone).toLocalDate()
        val nightEndsAt = if (case.hasNightYesterday) {
            SubmissionWindow.endsAt(zone, today.minusDays(1), Shift.NIGHT)
        } else {
            null
        }
        assertEquals(
            SuggestedShift(
                LocalDate.parse(case.expect.suggested.date),
                Shift.parse(case.expect.suggested.shift),
                case.expect.suggested.label,
            ),
            SubmissionWindow.suggested(today, now, nightEndsAt),
        )
        case.attemptedShiftAfterSubmission?.let {
            assertNotEquals(shift, Shift.parse(it))
            assertFalse(ReportStatus.SUBMITTED.keyEditable)
        }
    }

    private fun String.instant(): Instant = OffsetDateTime.parse(this).toInstant()

    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun cases(): List<Array<Any>> {
            val json = File(requireNotNull(System.getProperty("submissionCasesFile"))).readText()
            return Json.decodeFromString<SubmissionCases>(json).cases.map { arrayOf(it.name, it) }
        }
    }
}

@Serializable
private data class SubmissionCases(val about: String, val cases: List<SubmissionCase>)

@Serializable
data class SubmissionCase(
    val name: String,
    val companyZone: String,
    val siteZone: String?,
    val date: String,
    val shift: String?,
    val now: String,
    val hasNightYesterday: Boolean,
    val deviceTime: String,
    val attemptedShiftAfterSubmission: String? = null,
    val expect: ExpectedSubmission,
)

@Serializable
data class ExpectedSubmission(
    val windowEndsAt: String,
    val momentOfManual: String,
    val suggested: ExpectedSuggestion,
)

@Serializable
data class ExpectedSuggestion(val date: String, val shift: String, val label: String)
