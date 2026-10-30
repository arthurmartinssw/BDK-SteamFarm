package com.bdk.farm

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class Settings(context: Context) {
    private val prefs = context.getSharedPreferences("bdk", Context.MODE_PRIVATE)

    var baseUrl: String
        get() = prefs.getString("url", "") ?: ""
        set(value) {
            prefs.edit().putString("url", value).apply()
        }

    var token: String
        get() = decrypt(prefs.getString("token", null))
        set(value) {
            prefs.edit().apply {
                if (value.isEmpty()) remove("token") else putString("token", encrypt(value))
            }.apply()
        }

    var theme: String
        get() = prefs.getString("theme", "system") ?: "system"
        set(value) = prefs.edit().putString("theme", value).apply()

    var accent: String
        get() = prefs.getString("accent", "green") ?: "green"
        set(value) = prefs.edit().putString("accent", value).apply()

    var phoneNotify: Boolean
        get() = prefs.getBoolean("pn_on", false)
        set(value) = prefs.edit().putBoolean("pn_on", value).apply()

    var phoneInterval: Int
        get() = prefs.getInt("pn_interval", 15)
        set(value) = prefs.edit().putInt("pn_interval", value).apply()

    fun phoneType(key: String): Boolean = prefs.getBoolean("pn_$key", true)

    fun setPhoneType(key: String, on: Boolean) = prefs.edit().putBoolean("pn_$key", on).apply()

    var lastState: String
        get() = prefs.getString("w_state", "") ?: ""
        set(value) = prefs.edit().putString("w_state", value).apply()

    var unreachableCount: Int
        get() = prefs.getInt("w_fail", 0)
        set(value) = prefs.edit().putInt("w_fail", value).apply()

    var lastLoginWarnDay: Long
        get() = prefs.getLong("w_login_day", 0)
        set(value) = prefs.edit().putLong("w_login_day", value).apply()

    fun saveWidget(w: WidgetData) {
        prefs.edit()
            .putString("wd_state", w.state)
            .putString("wd_text", w.stateText)
            .putLong("wd_total", w.totalSeconds)
            .putInt("wd_games", w.games)
            .putBoolean("wd_manual", w.manualPause)
            .putString("wd_persona", w.personaText)
            .putLong("wd_at", w.updatedAt)
            .putString("wd_error", w.error)
            .apply()
    }

    fun loadWidget(): WidgetData? {
        if (!prefs.contains("wd_at")) return null
        return WidgetData(
            state = prefs.getString("wd_state", "") ?: "",
            stateText = prefs.getString("wd_text", "") ?: "",
            totalSeconds = prefs.getLong("wd_total", 0),
            games = prefs.getInt("wd_games", 0),
            manualPause = prefs.getBoolean("wd_manual", false),
            personaText = prefs.getString("wd_persona", "") ?: "",
            updatedAt = prefs.getLong("wd_at", 0),
            error = prefs.getString("wd_error", null),
        )
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }

        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    private fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val data = cipher.iv + cipher.doFinal(plain.toByteArray())
        return Base64.encodeToString(data, Base64.NO_WRAP)
    }

    private fun decrypt(stored: String?): String {
        if (stored.isNullOrEmpty()) return ""
        return try {
            val data = Base64.decode(stored, Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data.copyOfRange(0, 12)))
            String(cipher.doFinal(data, 12, data.size - 12))
        } catch (e: Exception) {
            ""
        }
    }

    private companion object {
        const val ALIAS = "bdk_token_key"
    }
}
