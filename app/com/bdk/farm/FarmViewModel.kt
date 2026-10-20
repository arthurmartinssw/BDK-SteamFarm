package com.bdk.farm

import android.app.Application
import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class FarmViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = Settings(app)

    var connected by mutableStateOf(settings.baseUrl.isNotBlank() && settings.token.isNotBlank())
        private set
    var status by mutableStateOf<Status?>(null)
        private set
    var history by mutableStateOf<History?>(null)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var busy by mutableStateOf(false)
        private set

    // lock
    var unlockError by mutableStateOf<String?>(null)
        private set

    // seleção de jogos
    var showPicker by mutableStateOf(false)
        private set
    var library by mutableStateOf<Library?>(null)
        private set
    var pickerError by mutableStateOf<String?>(null)
        private set

    // agenda
    var schedule by mutableStateOf<ScheduleInfo?>(null)
        private set
    var scheduleMessage by mutableStateOf<String?>(null)
        private set

    // avisos no Discord
    var notifyConfigured by mutableStateOf<Boolean?>(null)
        private set
    var notifyMessage by mutableStateOf<String?>(null)
        private set

    // renovar login da Steam
    var relogin by mutableStateOf<Relogin?>(null)
        private set
    var reloginMessage by mutableStateOf<String?>(null)
        private set

    // trocar token
    var newToken by mutableStateOf<String?>(null)
        private set
    var tokenMessage by mutableStateOf<String?>(null)
        private set

    private var reloginJob: Job? = null
    private var refreshing = false
    private var pollJob: Job? = null

    // ---------- conexão ----------

    /** Aceita o link completo (https://host/#token=XXX) ou endereço + token separados. */
    fun connect(link: String, tokenInput: String): String? {
        val (url, tokenFromLink) = parseLink(link)
        val token = tokenInput.trim().ifEmpty { tokenFromLink.orEmpty() }

        if (!url.startsWith("https://") && !url.startsWith("http://")) return "Cole o endereço começando com https://"
        if (token.isEmpty()) return "Falta o token. Use o link completo com #token= ou preencha o campo."

        settings.baseUrl = url
        settings.token = token
        status = null
        history = null
        error = null
        connected = true
        refresh()
        return null
    }

    fun disconnect() {
        settings.token = ""
        connected = false
        status = null
        history = null
        error = null
    }

    private fun parseLink(text: String): Pair<String, String?> {
        val t = text.trim()
        val i = t.indexOf('#')
        if (i < 0) return t.trimEnd('/') to null

        val params = t.substring(i + 1).split('&').mapNotNull {
            val eq = it.indexOf('=')
            if (eq <= 0) null else it.substring(0, eq) to Uri.decode(it.substring(eq + 1))
        }.toMap()

        val base = params["api"]?.trimEnd('/') ?: t.substring(0, i).trimEnd('/')
        return base to params["token"]
    }

    // ---------- atualização ----------

    fun startPolling() {
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            while (true) {
                refresh()
                delay(15_000)
            }
        }
    }

    fun stopPolling() {
        pollJob?.cancel()
        pollJob = null
        reloginJob?.cancel()
        reloginJob = null
    }

    fun refresh() {
        if (!connected || refreshing) return
        refreshing = true
        launchApi { api ->
            try {
                status = api.status()
                error = null
                history = api.history(30)
                schedule = api.schedule()
                if (notifyConfigured == null) notifyConfigured = api.notifyConfigured()
                val r = api.relogin()
                relogin = r
                if (r.active && reloginJob == null) watchRelogin()
            } finally {
                refreshing = false
            }
        }
    }

    // ---------- ações ----------

    fun toggle() {
        val s = status ?: return
        if (busy) return
        busy = true
        launchApi { api ->
            try {
                status = if (s.manualPause) api.resume() else api.pause()
                error = null
            } finally {
                busy = false
            }
        }
    }

    fun unlock(passphrase: String) {
        if (busy || passphrase.isEmpty()) return
        busy = true
        launchApi(onError = { unlockError = it }) { api ->
            try {
                status = api.unlock(passphrase)
                unlockError = null
            } finally {
                busy = false
            }
        }
    }

    // ---------- escolher jogos ----------

    fun openPicker() {
        showPicker = true
        pickerError = null
        library = null
        launchApi(onError = { pickerError = it }) { api ->
            var lib = api.library()
            if (lib.configured && !lib.available && lib.error == null) lib = api.refreshLibrary()
            library = lib
        }
    }

    fun closePicker() {
        showPicker = false
    }

    fun saveKey(key: String) = pickerCall { api -> library = api.setKey(key.trim()) }

    fun retryLibrary() = pickerCall { api -> library = api.refreshLibrary() }

    fun saveGames(ids: List<Long>) = pickerCall { api ->
        status = api.setGames(ids)
        showPicker = false
    }

    private fun pickerCall(block: suspend (Api) -> Unit) {
        if (busy) return
        busy = true
        pickerError = null
        launchApi(onError = { pickerError = it }) { api ->
            try {
                block(api)
            } finally {
                busy = false
            }
        }
    }

    // ---------- agenda ----------

    fun setScheduleEnabled(enabled: Boolean) {
        val current = schedule ?: return
        if (busy) return
        busy = true
        scheduleMessage = null
        launchApi(onError = { scheduleMessage = it }) { api ->
            try {
                schedule = api.setScheduleEnabled(current, enabled)
                refresh()
            } finally {
                busy = false
            }
        }
    }

    // ---------- avisos no Discord ----------

    fun saveWebhook(url: String) = notifyCall("Salvo. Toque em \"Enviar teste\" para conferir.") { api ->
        notifyConfigured = api.setWebhook(url.trim())
    }

    fun removeWebhook() = notifyCall("Avisos desligados.") { api -> notifyConfigured = api.setWebhook("") }

    fun testWebhook() = notifyCall("Enviado! Confira o canal do Discord.") { api -> api.testNotify() }

    private fun notifyCall(success: String, block: suspend (Api) -> Unit) {
        if (busy) return
        busy = true
        notifyMessage = null
        launchApi(onError = { notifyMessage = it }) { api ->
            try {
                block(api)
                notifyMessage = success
            } finally {
                busy = false
            }
        }
    }

    // ---------- renovar login da Steam ----------

    fun startRelogin(password: String, passphrase: String) {
        if (busy) return
        busy = true
        reloginMessage = null
        launchApi(onError = { reloginMessage = it }) { api ->
            try {
                relogin = api.reloginStart(password, passphrase)
                watchRelogin()
            } finally {
                busy = false
            }
        }
    }

    fun sendReloginCode(code: String) {
        reloginMessage = null
        launchApi(onError = { reloginMessage = it }) { api ->
            relogin = api.reloginCode(code.trim())
            watchRelogin()
        }
    }

    fun cancelRelogin() {
        launchApi(onError = { reloginMessage = it }) { api -> relogin = api.reloginCancel() }
    }

    /** Enquanto o login está em andamento, consulta o servidor a cada 2 s. */
    private fun watchRelogin() {
        reloginJob?.cancel()
        reloginJob = viewModelScope.launch {
            try {
                while (relogin?.active == true) {
                    delay(2_000)
                    relogin = Api(settings.baseUrl, settings.token).relogin()
                }
            } catch (e: ApiException) {
                reloginMessage = e.message
            } finally {
                reloginJob = null
            }
        }
    }

    // ---------- trocar o token ----------

    fun rotateToken() {
        if (busy) return
        busy = true
        tokenMessage = null
        launchApi(onError = { tokenMessage = it }) { api ->
            try {
                val fresh = api.rotateToken()
                if (fresh.isNotBlank()) {
                    settings.token = fresh
                    newToken = fresh
                    tokenMessage = "Token trocado. Este app já usa o novo. Cole-o também no painel web e em outros aparelhos."
                }
            } finally {
                busy = false
            }
        }
    }

    // ---------- base ----------

    private fun launchApi(onError: (String) -> Unit = { error = it }, block: suspend (Api) -> Unit) {
        viewModelScope.launch {
            try {
                block(Api(settings.baseUrl, settings.token))
            } catch (e: ApiException) {
                onError(e.message ?: "Erro desconhecido.")
            }
        }
    }
}
