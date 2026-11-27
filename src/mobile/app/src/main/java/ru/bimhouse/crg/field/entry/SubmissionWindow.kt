package ru.bimhouse.crg.field.entry

import kotlinx.serialization.Serializable
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

@Serializable
enum class Shift {
    DAY, NIGHT;

    companion object {
        fun parse(code: String?): Shift = when (code?.trim()?.lowercase(Locale.ROOT)) {
            null, "", "day" -> DAY
            "night" -> NIGHT
            else -> throw IllegalArgumentException("Неизвестная смена: $code")
        }
    }
}

@Serializable
enum class ReportStatus {
    DRAFT, SUBMITTED, ACCEPTED, RETURNED;

    val keyEditable: Boolean get() = this == DRAFT || this == RETURNED
}

data class SuggestedShift(val date: LocalDate, val shift: Shift, val label: String)

object SubmissionWindow {
    private val shortDate = DateTimeFormatter.ofPattern("dd.MM", Locale.ROOT)

    fun effectiveZone(companyZone: ZoneId, siteZone: ZoneId?): ZoneId = siteZone ?: companyZone

    fun endsAt(zone: ZoneId, date: LocalDate, shift: Shift): Instant =
        date.plusDays(if (shift == Shift.NIGHT) 2 else 1).atStartOfDay(zone).toInstant()

    fun momentOfManual(deviceTime: Instant, endsAt: Instant): Instant = minOf(deviceTime, endsAt)

    fun suggested(zone: ZoneId, now: Instant, hasNightYesterday: Boolean): SuggestedShift {
        val today = now.atZone(zone).toLocalDate()
        val yesterday = today.minusDays(1)
        if (hasNightYesterday && now < endsAt(zone, yesterday, Shift.NIGHT)) {
            return SuggestedShift(yesterday, Shift.NIGHT, "Ночная смена ${yesterday.format(shortDate)}")
        }
        return SuggestedShift(today, Shift.DAY, "Отчёт ${today.format(shortDate)}")
    }
}
