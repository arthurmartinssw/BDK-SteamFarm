package com.bdk.farm

import java.time.LocalDate
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.roundToInt

private val PT = Locale.forLanguageTag("pt-BR")

fun fmtDuration(seconds: Long?): String {
    if (seconds == null) return "—"
    val s = maxOf(0L, seconds)
    val d = s / 86400
    val h = (s % 86400) / 3600
    val m = (s % 3600) / 60
    return when {
        d > 0 -> "${d}d ${h}h"
        h > 0 -> "${h}h ${m.toString().padStart(2, '0')}m"
        else -> "${m}m"
    }
}

fun fmtHours(seconds: Long): String {
    val h = seconds / 3600.0
    return when {
        h >= 100 -> "${h.roundToInt()} h"
        h >= 1 -> String.format(PT, "%.1f h", h)
        else -> "${seconds / 60} min"
    }
}

fun fmtDay(d: LocalDate): String =
    d.format(DateTimeFormatter.ofPattern("EEE dd/MM", PT)).replace(".", "")

fun fmtShortDay(d: LocalDate): String =
    d.format(DateTimeFormatter.ofPattern("EEE", PT)).replace(".", "")

fun fmtClock(epochSeconds: Long): String =
    java.time.Instant.ofEpochSecond(epochSeconds).atZone(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofPattern("HH:mm"))

fun fmtDayOf(epochSeconds: Long): String =
    fmtDay(java.time.Instant.ofEpochSecond(epochSeconds).atZone(ZoneId.systemDefault()).toLocalDate())

/** Dias até o login (token) vencer; null se não souber. */
fun daysUntil(iso: String?): Long? {
    if (iso == null) return null
    return try {
        val ms = OffsetDateTime.parse(iso).toInstant().toEpochMilli() - System.currentTimeMillis()
        Math.floorDiv(ms, 86_400_000L)
    } catch (e: Exception) {
        null
    }
}

/** "seg 22:00" no fuso da agenda (cai para o fuso do aparelho se o nome do fuso não for reconhecido). */
fun describeTime(epochSeconds: Long, zoneId: String): String {
    val zone = try {
        ZoneId.of(zoneId)
    } catch (e: Exception) {
        ZoneId.systemDefault()
    }
    return java.time.Instant.ofEpochSecond(epochSeconds).atZone(zone)
        .format(DateTimeFormatter.ofPattern("EEE HH:mm", PT)).replace(".", "")
}
