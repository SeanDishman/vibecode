package com.vibecode.mobile.data

import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity

/**
 * The screen lock in front of a paired app.
 *
 * The pairing token is a standing credential that can make an agent run arbitrary commands on someone's PC, so
 * an unattended unlocked phone is the weakest link in the whole design — weaker than the TLS pinning it sits
 * behind. This gates the app behind the same credential that protects the device itself.
 *
 * Every failure path here lets the user IN rather than keeping them out. That is deliberate: this lock is
 * defence in depth on top of a token that is already encrypted at rest, and the alternative failure mode — a
 * phone that can never open the app again because its fingerprint sensor broke — is far worse than the risk.
 */
object AppLock {

    private const val COMBINED =
        BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL
    private const val BIOMETRIC_ONLY = BiometricManager.Authenticators.BIOMETRIC_WEAK

    /**
     * Whether this device can actually challenge the user.
     *
     * False on a phone with no screen lock set at all. In that case the app deliberately does not lock: there is
     * no secret to check against, and a lock screen with no way past it would be a door with no key.
     */
    fun isAvailable(activity: FragmentActivity): Boolean = authenticators(activity) != null

    /** The strongest authenticator set this device will actually accept, or null if it has no lock configured. */
    private fun authenticators(activity: FragmentActivity): Int? {
        val manager = BiometricManager.from(activity)
        // Device credential is included first so a wet or broken fingerprint sensor is never the only way in.
        if (manager.canAuthenticate(COMBINED) == BiometricManager.BIOMETRIC_SUCCESS) return COMBINED
        if (manager.canAuthenticate(BIOMETRIC_ONLY) == BiometricManager.BIOMETRIC_SUCCESS) return BIOMETRIC_ONLY
        return null
    }

    /**
     * Shows the system prompt.
     *
     * @param onSuccess called on the main thread once the user proves who they are.
     * @param onUnavailable called when the device turned out to have no usable credential — or when the prompt
     *        itself could not be shown — so the caller can let the user through instead of stranding them.
     */
    fun prompt(
        activity: FragmentActivity,
        onSuccess: () -> Unit,
        onUnavailable: () -> Unit,
    ) {
        val allowed = authenticators(activity)
        if (allowed == null) {
            onUnavailable()
            return
        }

        val prompt = BiometricPrompt(
            activity,
            ContextCompat.getMainExecutor(activity),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onSuccess()

                override fun onAuthenticationError(code: Int, message: CharSequence) {
                    // A cancelled or failed prompt leaves the app locked; the lock screen keeps its own Unlock
                    // button for another attempt. The exception is a device that has no usable credential after
                    // all, which must not become a permanent lockout.
                    if (code == BiometricPrompt.ERROR_NO_DEVICE_CREDENTIAL ||
                        code == BiometricPrompt.ERROR_HW_NOT_PRESENT ||
                        code == BiometricPrompt.ERROR_NO_BIOMETRICS
                    ) onUnavailable()
                }
            },
        )

        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle("Unlock VibeCode")
            .setSubtitle("This phone can control your PC.")
            .setAllowedAuthenticators(allowed)
            .apply {
                // A biometric-only prompt is required to offer a negative button; a combined one is forbidden
                // from having it, because the credential path IS the fallback.
                if (allowed == BIOMETRIC_ONLY) setNegativeButtonText("Cancel")
            }
            .build()

        // Older platform versions reject some authenticator combinations at show time rather than at query time.
        runCatching { prompt.authenticate(info) }.onFailure { onUnavailable() }
    }
}
