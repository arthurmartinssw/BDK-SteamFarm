package com.bdk.farm

data class Game(
    val appId: Long,
    val name: String,
    val farmedSeconds: Long,
    val steamMinutes: Long?,
)

data class Status(
    val account: String,
    val steamId: String?,
    val state: String,
    val stateText: String,
    val uptimeSeconds: Long,
    val sessionSeconds: Long?,
    val totalFarmedSeconds: Long,
    val tokenExpiresAt: String?,
    val reconnects: Int,
    val pausesByOtherPc: Int,
    val manualPause: Boolean,
    val games: List<Game>,
)

data class OwnedGame(val appId: Long, val name: String, val minutes: Long)

data class Library(
    val configured: Boolean,
    val available: Boolean,
    val error: String?,
    val games: List<OwnedGame>,
    val selected: List<Long>,
)

/** Uma sessão de farm. end == null significa "em andamento". */
data class FarmSession(val start: Long, val end: Long?, val reason: String, val games: Int)

data class History(val now: Long, val sessions: List<FarmSession>)

/** Agenda de horários do farm. raw guarda o JSON original (os horários são editados no painel web). */
data class ScheduleInfo(
    val enabled: Boolean,
    val timeZone: String,
    val windows: List<String>,
    val active: Boolean,
    val nextChange: Long?,
    val raw: org.json.JSONObject,
)
