package com.bdk.farm

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

class ApiException(message: String, val auth: Boolean = false) : Exception(message)

/** Cliente da API do farm (a mesma que o painel web usa). Sem bibliotecas extras. */
class Api(baseUrl: String, private val token: String) {
    private val base = baseUrl.trim().trimEnd('/')

    private suspend fun call(method: String, path: String, body: JSONObject? = null): JSONObject =
        withContext(Dispatchers.IO) {
            val conn = URL(base + path).openConnection() as HttpURLConnection
            try {
                conn.requestMethod = method
                conn.connectTimeout = 10_000
                conn.readTimeout = 45_000
                conn.setRequestProperty("Authorization", "Bearer $token")
                conn.setRequestProperty("Accept", "application/json")

                if (method == "POST") {
                    conn.doOutput = true
                    conn.setRequestProperty("Content-Type", "application/json")
                    conn.outputStream.use { it.write((body ?: JSONObject()).toString().toByteArray()) }
                }

                val code = conn.responseCode
                val stream = if (code in 200..299) conn.inputStream else conn.errorStream
                val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty()

                if (code == 401) throw ApiException("Token inválido. Entre de novo com o link certo.", auth = true)

                val json = try {
                    if (text.isBlank()) JSONObject() else JSONObject(text)
                } catch (e: JSONException) {
                    throw ApiException("Resposta inesperada do farm (código $code).")
                }

                if (code !in 200..299) {
                    throw ApiException(json.optString("error").ifBlank { "O farm respondeu com erro $code." })
                }
                json
            } catch (e: IOException) {
                val msg = e.message.orEmpty()
                throw ApiException(
                    if (msg.contains("Cleartext", ignoreCase = true)) "O endereço precisa começar com https://."
                    else "Não consegui falar com o farm. A VPS está ligada e o endereço está certo?"
                )
            } finally {
                conn.disconnect()
            }
        }

    suspend fun status(): Status = parseStatus(call("GET", "/api/status"))
    suspend fun pause(): Status = parseStatus(call("POST", "/api/pause"))
    suspend fun resume(): Status = parseStatus(call("POST", "/api/resume"))
    suspend fun unlock(passphrase: String): Status =
        parseStatus(call("POST", "/api/unlock", JSONObject().put("passphrase", passphrase)))

    suspend fun history(days: Int): History {
        val j = call("GET", "/api/history?days=$days")
        val arr = j.getJSONArray("sessions")
        val list = (0 until arr.length()).map { i ->
            val s = arr.getJSONObject(i)
            FarmSession(
                start = s.getLong("start"),
                end = if (s.isNull("end")) null else s.getLong("end"),
                reason = s.optString("reason"),
                games = s.optJSONArray("games")?.length() ?: 0,
            )
        }
        return History(j.optLong("now", System.currentTimeMillis() / 1000), list)
    }

    suspend fun schedule(): ScheduleInfo = parseSchedule(call("GET", "/api/schedule"))

    /** Liga ou desliga a agenda mantendo os horários e o fuso que já estão salvos. */
    suspend fun setScheduleEnabled(current: ScheduleInfo, enabled: Boolean): ScheduleInfo {
        val body = JSONObject()
            .put("enabled", enabled)
            .put("timeZone", current.timeZone)
            .put("windows", current.raw.optJSONArray("windows") ?: JSONArray())
        return parseSchedule(call("POST", "/api/schedule", body))
    }

    suspend fun notifyConfigured(): Boolean = call("GET", "/api/notify").optBoolean("configured")

    suspend fun setWebhook(url: String): Boolean =
        call("POST", "/api/notify/webhook", JSONObject().put("url", url)).optBoolean("configured")

    suspend fun testNotify() {
        call("POST", "/api/notify/test")
    }

    suspend fun relogin(): Relogin = parseRelogin(call("GET", "/api/relogin"))

    suspend fun reloginStart(password: String, passphrase: String): Relogin = parseRelogin(
        call("POST", "/api/relogin/start", JSONObject().put("password", password).put("passphrase", passphrase))
    )

    suspend fun reloginCode(code: String): Relogin =
        parseRelogin(call("POST", "/api/relogin/code", JSONObject().put("code", code)))

    suspend fun reloginCancel(): Relogin = parseRelogin(call("POST", "/api/relogin/cancel"))

    /** Gera um token novo; o antigo deixa de valer na hora. */
    suspend fun rotateToken(): String = call("POST", "/api/token/rotate").optString("token")

    private fun parseRelogin(j: JSONObject) = Relogin(
        state = j.optString("state", "idle"),
        email = j.optString("email").ifBlank { null },
        wrongCode = j.optBoolean("wrongCode"),
        error = j.optString("error").ifBlank { null },
        needsPassphrase = j.optBoolean("needsPassphrase"),
        account = j.optString("account").ifBlank { null },
        newExpiry = j.optString("newExpiry").ifBlank { null },
    )

    suspend fun library(): Library = parseLibrary(call("GET", "/api/library"))
    suspend fun refreshLibrary(): Library = parseLibrary(call("POST", "/api/library/refresh"))
    suspend fun setKey(key: String): Library =
        parseLibrary(call("POST", "/api/webapikey", JSONObject().put("key", key)))

    suspend fun setGames(ids: List<Long>): Status {
        val arr = JSONArray()
        ids.forEach { arr.put(it) }
        return parseStatus(call("POST", "/api/games", JSONObject().put("appIds", arr)))
    }

    private fun parseStatus(j: JSONObject): Status {
        val arr = j.getJSONArray("games")
        val games = (0 until arr.length()).map { i ->
            val g = arr.getJSONObject(i)
            Game(
                appId = g.getLong("appId"),
                name = g.optString("name"),
                farmedSeconds = g.optLong("farmedSeconds"),
                steamMinutes = if (g.isNull("steamMinutes")) null else g.getLong("steamMinutes"),
            )
        }
        return Status(
            account = j.optString("account"),
            steamId = if (j.isNull("steamId")) null else j.getString("steamId"),
            state = j.optString("state"),
            stateText = j.optString("stateText"),
            uptimeSeconds = j.optLong("uptimeSeconds"),
            sessionSeconds = if (j.isNull("sessionSeconds")) null else j.getLong("sessionSeconds"),
            totalFarmedSeconds = j.optLong("totalFarmedSeconds"),
            tokenExpiresAt = if (j.isNull("tokenExpiresAt")) null else j.getString("tokenExpiresAt"),
            reconnects = j.optInt("reconnects"),
            pausesByOtherPc = j.optInt("pausesByOtherPc"),
            manualPause = j.optBoolean("manualPause"),
            games = games,
        )
    }

    private fun parseSchedule(j: JSONObject): ScheduleInfo {
        val arr = j.optJSONArray("windows") ?: JSONArray()
        val labels = listOf("Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom")

        val windows = (0 until arr.length()).map { i ->
            val w = arr.getJSONObject(i)
            val d = w.optJSONArray("days") ?: JSONArray()
            val days = (0 until d.length()).map { d.getInt(it) }
            val dayText = if (days.size == 7) "Todos os dias" else days.joinToString(", ") { labels[it - 1] }
            "$dayText · ${w.optString("start")}–${w.optString("end")}"
        }

        return ScheduleInfo(
            enabled = j.optBoolean("enabled"),
            timeZone = j.optString("timeZone", "America/Sao_Paulo"),
            windows = windows,
            active = j.optBoolean("active", true),
            nextChange = if (j.isNull("nextChange")) null else j.getLong("nextChange"),
            raw = j,
        )
    }

    private fun parseLibrary(j: JSONObject): Library {
        val arr = j.optJSONArray("games") ?: JSONArray()
        val games = (0 until arr.length()).map { i ->
            val g = arr.getJSONObject(i)
            OwnedGame(g.getLong("appId"), g.optString("name"), g.optLong("minutes"))
        }
        val sel = j.optJSONArray("selected") ?: JSONArray()
        return Library(
            configured = j.optBoolean("configured"),
            available = j.optBoolean("available"),
            error = if (j.isNull("error")) null else j.getString("error"),
            games = games,
            selected = (0 until sel.length()).map { sel.getLong(it) },
        )
    }
}
