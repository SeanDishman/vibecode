package com.vibecode.mobile

import android.content.Intent
import android.provider.Settings
import android.os.Bundle
import android.os.SystemClock
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.vibecode.mobile.data.AppLock
import com.vibecode.mobile.ui.ChatActions
import com.vibecode.mobile.ui.ChatListScreen
import com.vibecode.mobile.ui.ChatScreen
import com.vibecode.mobile.ui.EnrollingScreen
import com.vibecode.mobile.ui.LockScreen
import com.vibecode.mobile.ui.NewChatScreen
import com.vibecode.mobile.ui.PairScreen
import com.vibecode.mobile.ui.VibeCodeTheme
import com.vibecode.mobile.ui.VibeColors

/**
 * FragmentActivity rather than ComponentActivity because BiometricPrompt needs a fragment manager to host the
 * system dialog. Nothing else in the app uses fragments.
 */
class MainActivity : FragmentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // The window shows someone's source code, their agent's tool output, and their file paths. FLAG_SECURE
        // keeps all of it out of screenshots, screen recordings, non-secure external displays, and the blurred
        // thumbnail the recents switcher would otherwise persist to disk.
        //
        // Lifted in debug builds only, because FLAG_SECURE also blocks `adb shell screencap` and there is no way
        // to look at what you are building with it on. Debug artefacts are never distributed.
        if (!BuildConfig.DEBUG) {
            window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        }

        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )
        setContent { VibeCodeTheme { App(activity = this) } }
    }

    override fun onStop() {
        super.onStop()
        if (!isChangingConfigurations) backgroundedAt = SystemClock.elapsedRealtime()
    }

    /**
     * Whether enough time has passed in the background to ask again. A short grace period is the difference
     * between a lock people keep and one they turn off: glancing at a notification should not cost a fingerprint,
     * but leaving the phone on a desk for a minute should.
     */
    fun shouldRelock(): Boolean {
        val elapsed = backgroundedAt.takeIf { it != 0L }?.let { SystemClock.elapsedRealtime() - it }
        backgroundedAt = 0L
        return elapsed != null && elapsed > RELOCK_AFTER_MS
    }

    private companion object {
        const val RELOCK_AFTER_MS = 60_000L
        // Process-local so rotating the activity cannot discard an existing background timeout.
        var backgroundedAt = 0L
    }
}

@Composable
private fun App(activity: MainActivity, vm: AppViewModel = viewModel()) {
    val state by vm.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }

    // Re-lock when the app comes back from a long spell in the background, and prompt whenever it is locked.
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_START && activity.shouldRelock()) vm.lock()
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }

    if (state.locked) {
        var authenticationUnavailable by remember { mutableStateOf(false) }
        val unlock = {
            authenticationUnavailable = false
            AppLock.prompt(activity, onSuccess = vm::onUnlocked, onUnavailable = { authenticationUnavailable = true })
        }
        // One prompt per transition into the locked state; the screen's own button covers a cancelled attempt.
        LaunchedEffect(Unit) {
            unlock()
        }
        LockScreen(
            onUnlock = unlock,
            authenticationUnavailable = authenticationUnavailable,
            onSecuritySettings = { activity.startActivity(Intent(Settings.ACTION_SECURITY_SETTINGS)) },
        )
        return
    }

    LaunchedEffect(state.notice) {
        if (state.notice.isNotBlank()) {
            snackbar.showSnackbar(state.notice)
            vm.dismissNotice()
        }
    }

    // System back leaves the transcript rather than the app - the phone's own gesture is the natural way out of
    // a chat, and dropping straight to the launcher from there loses your place for no reason.
    BackHandler(enabled = state.screen is Screen.Chat && state.sheet == Sheet.None) { vm.closeChatScreen() }
    BackHandler(enabled = state.screen is Screen.NewChat) { vm.cancelNewChat() }
    BackHandler(enabled = state.screen is Screen.Pair && state.pair.step != PairStep.Address && !state.pair.busy) {
        vm.backToAddress()
    }

    Scaffold(
        containerColor = VibeColors.Bg0,
        // Child screens own their Scaffold/safe-drawing insets. The host only positions transient feedback.
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        snackbarHost = {
            SnackbarHost(snackbar, modifier = Modifier.imePadding().safeDrawingPadding()) { data ->
                Box(
                    Modifier
                        .padding(14.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(VibeColors.Bg3)
                        .padding(14.dp)
                ) {
                    Text(data.visuals.message, style = MaterialTheme.typography.bodyMedium, color = VibeColors.Text)
                }
            }
        },
    ) { outerPadding ->
        Box(Modifier.fillMaxSize().padding(outerPadding).background(VibeColors.Bg0)) {
            when (state.screen) {
                is Screen.Pair -> PairScreen(
                    state = state.pair,
                    onAddressChanged = vm::onAddressChanged,
                    onDiscover = vm::discover,
                    onConfirm = vm::confirmIdentity,
                    onBack = vm::backToAddress,
                    onCodeChanged = vm::onCodeChanged,
                    onSubmitCode = vm::submitCode,
                )

                is Screen.Enrolling -> EnrollingScreen(state = state, onRetry = vm::enroll)

                is Screen.Chats -> ChatListScreen(
                    state = state,
                    onOpen = vm::openChat,
                    onRetry = vm::retryNow,
                    onUnpair = vm::unpair,
                    onNewChat = vm::openNewChat,
                    onPin = vm::togglePin,
                    onRename = vm::rename,
                    onClosePane = vm::closeChatOnPc,
                )

                is Screen.NewChat -> NewChatScreen(
                    state = state.newChat,
                    onFolder = vm::onNewChatFolder,
                    onProvider = vm::onNewChatProvider,
                    onTitle = vm::onNewChatTitle,
                    onCreate = vm::createChat,
                    onCancel = vm::cancelNewChat,
                )

                // Rebuilt each recomposition rather than remembered: the pin/close lambdas need the chat that is
                // open right now, and a remembered bag is exactly the kind of thing that quietly goes stale.
                is Screen.Chat -> ChatScreen(
                    state = state,
                    actions = ChatActions(
                        onBack = vm::closeChatScreen,
                        onDraftChanged = vm::onDraftChanged,
                        onSend = vm::send,
                        onStop = vm::stopTurn,
                        onPermission = vm::respondToPermission,
                        onAnswer = vm::answerQuestion,
                        onPlan = vm::decidePlan,
                        onSheet = vm::showSheet,
                        onDismissSheet = vm::dismissSheet,
                        onModel = vm::setModel,
                        onEffort = vm::setEffort,
                        onMode = vm::setMode,
                        onFast = vm::setFastMode,
                        onSendQueued = vm::sendQueuedNow,
                        onCancelQueued = vm::cancelQueued,
                        onUndo = vm::undoPrompt,
                        onPin = { state.openChat?.let(vm::togglePin) },
                        onClosePane = { state.openChat?.let(vm::closeChatOnPc) },
                    ),
                )
            }
        }
    }
}
