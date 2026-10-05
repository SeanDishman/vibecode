package com.vibecode.mobile.data

import android.app.Activity
import android.app.KeyguardManager
import android.os.Build
import android.os.Bundle
import androidx.activity.result.contract.ActivityResultContracts
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.Fragment
import androidx.fragment.app.FragmentActivity

/** A failed, cancelled or unavailable authenticator must never unlock desktop control. */
object AppLock {
    private const val COMBINED =
        BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL
    private const val STRONG = BiometricManager.Authenticators.BIOMETRIC_STRONG
    private const val CREDENTIAL_TAG = "vibecode.credential.confirm"

    fun isAvailable(activity: FragmentActivity): Boolean =
        activity.getSystemService(KeyguardManager::class.java)?.isDeviceSecure == true

    /** onUnavailable is an error notification only: the caller must leave the app locked. */
    fun prompt(activity: FragmentActivity, onSuccess: () -> Unit, onUnavailable: () -> Unit) {
        if (!isAvailable(activity)) {
            onUnavailable()
            return
        }
        val manager = BiometricManager.from(activity)
        val allowed = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) COMBINED else STRONG
        if (manager.canAuthenticate(allowed) != BiometricManager.BIOMETRIC_SUCCESS) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
                credential(activity, onSuccess, onUnavailable)
            } else onUnavailable()
            return
        }
        val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onSuccess()
                override fun onAuthenticationError(code: Int, message: CharSequence) {
                    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R &&
                        (code == BiometricPrompt.ERROR_NEGATIVE_BUTTON || code == BiometricPrompt.ERROR_HW_UNAVAILABLE ||
                            code == BiometricPrompt.ERROR_LOCKOUT || code == BiometricPrompt.ERROR_LOCKOUT_PERMANENT)) {
                        credential(activity, onSuccess, onUnavailable)
                    } else onUnavailable()
                }
            })
        runCatching {
            val info = BiometricPrompt.PromptInfo.Builder()
                .setTitle("Unlock VibeCode")
                .setSubtitle("This phone can control your PC.")
                .setAllowedAuthenticators(allowed)
                .apply { if (allowed == STRONG) setNegativeButtonText("Use screen lock") }
                .build()
            prompt.authenticate(info)
        }.onFailure { onUnavailable() }
    }

    private fun credential(activity: FragmentActivity, success: () -> Unit, unavailable: () -> Unit) {
        val manager = activity.supportFragmentManager
        if (manager.isStateSaved) { unavailable(); return }
        if (manager.findFragmentByTag(CREDENTIAL_TAG) != null) return
        runCatching {
            manager.beginTransaction().add(CredentialFragment().apply {
                onSuccess = success
                onUnavailable = unavailable
            }, CREDENTIAL_TAG).commitNow()
        }.onFailure { unavailable() }
    }
}

/** API 26-29 cannot use STRONG | DEVICE_CREDENTIAL in BiometricPrompt. Use the OS credential screen. */
class CredentialFragment : Fragment() {
    var onSuccess: (() -> Unit)? = null
    var onUnavailable: (() -> Unit)? = null
    private var launched = false
    private val confirmation = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val accepted = it.resultCode == Activity.RESULT_OK
        finish(accepted)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Callbacks deliberately do not survive process death/recreation; the app stays locked and can retry.
        launched = savedInstanceState != null
    }

    @Suppress("DEPRECATION")
    override fun onResume() {
        super.onResume()
        if (onSuccess == null) { finish(false); return }
        if (launched) return
        launched = true
        val keyguard = requireContext().getSystemService(KeyguardManager::class.java)
        val intent = keyguard?.createConfirmDeviceCredentialIntent("Unlock VibeCode", "Confirm your screen lock to control your PC.")
        if (intent == null) { finish(false); return }
        runCatching { confirmation.launch(intent) }.onFailure { finish(false) }
    }

    private fun finish(accepted: Boolean) {
        val callback = if (accepted) onSuccess else onUnavailable
        onSuccess = null
        onUnavailable = null
        if (isAdded) parentFragmentManager.beginTransaction().remove(this).commitAllowingStateLoss()
        callback?.invoke()
    }
}
