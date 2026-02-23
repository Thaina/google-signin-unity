/*
 * Copyright 2017 Google Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
package com.google.googlesignin;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.exceptions.GetCredentialException;

import com.google.android.gms.auth.api.identity.Identity;
import com.google.android.gms.auth.api.identity.SignInCredential;
import com.google.android.gms.common.api.ApiException;
import com.google.android.gms.tasks.TaskExecutors;

import org.jetbrains.annotations.NotNull;

import java.lang.ref.WeakReference;

/**
 * Transparent activity to handle the sign-in result for both legacy SignInClient and CredentialManager.
 */
public class SignInActivity extends Activity {
    private static final String TAG = "GoogleSignInActivity";
    private static final int RC_SIGN_IN = 9001;

    private static SignInResultCallback resultCallback;
    private static CredentialManagerCallback<GetCredentialResponse, GetCredentialException> credentialManagerCallback;
    private static GetCredentialRequest credentialRequest;
    private static boolean resultSent = false;
    private static WeakReference<SignInActivity> activeActivity;
    private CancellationSignal cancellationSignal;

    public interface SignInResultCallback {
        void onSignInResult(SignInCredential credential);
        void onSignInError(Exception exception);
        void onSignInCancelled();
    }

    public static void setResultCallback(SignInResultCallback callback) {
        resultCallback = callback;
        resultSent = false;
    }

    public static void setCredentialManagerRequest(GetCredentialRequest request, CredentialManagerCallback<GetCredentialResponse, GetCredentialException> callback) {
        credentialRequest = request;
        credentialManagerCallback = callback;
        resultSent = false;
    }

    public static void finishActivity() {
        if (activeActivity != null) {
            SignInActivity activity = activeActivity.get();
            if (activity != null && !activity.isFinishing()) {
                activity.finish();
            }
            activeActivity = null;
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        activeActivity = new WeakReference<>(this);

        Intent intent = getIntent();
        if (intent == null) {
            finish();
            return;
        }

        if (intent.hasExtra("pending_intent")) {
            // Legacy sign-in flow
            try {
                startIntentSenderForResult(
                    intent.getParcelableExtra("pending_intent"),
                    RC_SIGN_IN,
                    null, 0, 0, 0);
            } catch (Exception e) {
                Log.e(TAG, "Error starting sign-in intent", e);
                sendError(e);
                finish();
            }
        } else if (credentialRequest != null) {
            // Credential Manager flow (Android 14+)
            cancellationSignal = new CancellationSignal();
            CredentialManager credentialManager = CredentialManager.create(this);
            CredentialManagerCallback<GetCredentialResponse, GetCredentialException> credentialManagerCallback1 = new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                @Override
                public void onResult(GetCredentialResponse result) {
                    if (credentialManagerCallback != null) {
                        credentialManagerCallback.onResult(result);
                        resultSent = true;
                    }
                    finish();
                }

                @Override
                public void onError(@NotNull GetCredentialException e) {
                    if (credentialManagerCallback != null) {
                        credentialManagerCallback.onError(e);
                        resultSent = true;
                    }
                    finish();
                }
            };

            credentialManager.getCredentialAsync(
                this,
                credentialRequest,
                cancellationSignal,
                TaskExecutors.MAIN_THREAD,
                credentialManagerCallback1
            );
        } else {
            finish();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode == RC_SIGN_IN) {
            try {
                SignInCredential credential = Identity.getSignInClient(this).getSignInCredentialFromIntent(data);
                if (resultCallback != null) {
                    resultCallback.onSignInResult(credential);
                    resultSent = true;
                }
            } catch (ApiException e) {
                if (e.getStatusCode() == 16) { // CANCELED
                    Log.i(TAG, "Sign-in cancelled by user");
                    if (resultCallback != null) {
                        resultCallback.onSignInCancelled();
                        resultSent = true;
                    }
                } else {
                    Log.e(TAG, "Sign-in failed", e);
                    sendError(e);
                }
            } catch (Exception e) {
                Log.e(TAG, "Unexpected error", e);
                sendError(e);
            }
            finish();
        }
    }

    private void sendError(Exception e) {
        if (resultCallback != null) {
            resultCallback.onSignInError(e);
            resultSent = true;
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        // If we regained focus but no result was sent, it might mean the system UI was dismissed silently
        if (hasFocus && !resultSent && !isFinishing()) {
            Log.w(TAG, "Window regained focus without result. Potential silent dismissal.");
        }
    }

    @Override
    protected void onDestroy() {
        if (!resultSent && resultCallback != null) {
            resultCallback.onSignInCancelled();
        } else if (!resultSent && credentialManagerCallback != null) {
            credentialManagerCallback.onError(new androidx.credentials.exceptions.GetCredentialCancellationException("Activity destroyed without result"));
        }

        if (cancellationSignal != null) {
            cancellationSignal.cancel();
        }

        if (activeActivity != null && activeActivity.get() == this) {
            activeActivity = null;
        }

        resultCallback = null;
        credentialManagerCallback = null;
        credentialRequest = null;
        resultSent = false;
        super.onDestroy();
    }
}
