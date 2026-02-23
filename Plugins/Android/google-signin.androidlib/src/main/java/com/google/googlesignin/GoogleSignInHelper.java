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
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.CancellationSignal;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.credentials.ClearCredentialStateRequest;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.exceptions.ClearCredentialException;
import androidx.credentials.exceptions.GetCredentialException;

import com.google.android.gms.auth.api.identity.AuthorizationRequest;
import com.google.android.gms.auth.api.identity.AuthorizationResult;
import com.google.android.gms.auth.api.identity.BeginSignInRequest;
import com.google.android.gms.auth.api.identity.Identity;
import com.google.android.gms.auth.api.identity.SignInClient;
import com.google.android.gms.auth.api.identity.SignInCredential;
import com.google.android.gms.common.GoogleApiAvailability;
import com.google.android.gms.common.Scopes;
import com.google.android.gms.common.api.ApiException;
import com.google.android.gms.common.api.CommonStatusCodes;
import com.google.android.gms.common.api.Scope;
import com.google.android.gms.common.util.Strings;
import com.google.android.gms.tasks.OnSuccessListener;
import com.google.android.gms.tasks.SuccessContinuation;
import com.google.android.gms.tasks.Task;
import com.google.android.gms.tasks.TaskCompletionSource;
import com.google.android.gms.tasks.TaskExecutors;
import com.google.android.libraries.identity.googleid.GetGoogleIdOption;
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption;
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential;
import com.unity3d.player.UnityPlayer;

import org.jetbrains.annotations.NotNull;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;

/**
 * Helper class used by the native C++ code to interact with Google Sign-in API.
 * The general flow is
 * Call configure, then one of signIn or signInSilently.
 */
public class GoogleSignInHelper {

  // Set to true to get more debug logging.
  public static boolean loggingEnabled = false;

  /**
   * Enables verbose logging
   */
  public static void enableDebugLogging(boolean flag) {
    loggingEnabled = flag;
  }

  private static Task<AuthorizationResult> task;
  private static Function<Boolean, Task<AuthorizationResult>> signInFunction;
  private static SignInClient signInClient;

  public static boolean isPending() {
    return task != null && !task.isComplete() && !task.isCanceled();
  }

  public static int getStatus() {
    if(signInFunction == null)
      return CommonStatusCodes.DEVELOPER_ERROR;

    if(task == null)
      return CommonStatusCodes.SIGN_IN_REQUIRED;

    if(task.isCanceled())
      return CommonStatusCodes.CANCELED;

    if(task.isSuccessful())
      return CommonStatusCodes.SUCCESS;

    Exception e = task.getException();
    if(e != null)
    {
      logError("onFailure with INTERNAL_ERROR : " + e.getClass().toString() + " " + e.getMessage());
      return CommonStatusCodes.INTERNAL_ERROR;
    }

    return CommonStatusCodes.ERROR;
  }

  /**
   * Detects if the current GMS version is faulty (introduced in 26.02.35, fix window is post-Feb 11).
   * Faulty versions should fall back to legacy SignInClient to avoid CredentialManager UI exceptions.
   * Flagging versions up to 26.08.00 (August release window) for extra safety.
   */
  public static boolean isUseSignInClient()
  {
      if(Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
          return true;

      Context context = UnityPlayer.currentActivity;
      if (context == null) return false;

      try {
          PackageInfo pi = context.getPackageManager().getPackageInfo(GoogleApiAvailability.GOOGLE_PLAY_SERVICES_PACKAGE, 0);
          long longVersionCode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P ? pi.getLongVersionCode() : pi.versionCode;

          // The traditional version code (MMYYDDHHH) is in the lower 32 bits.
          int gmsVersionCode = (int) longVersionCode;

          Log.d(TAG, "GMS Full Version Code: " + longVersionCode);
          Log.d(TAG, "GMS Version Code (lower 32 bits): " + gmsVersionCode);
          Log.d(TAG, "GMS Version Name: " + pi.versionName);

          // Faulty range: Starts at 26.02.35 (260235000).
          // Extended for safety up to 26.08.00 (260800000) to ensure fix is stable.
          if (gmsVersionCode >= 260235000 && gmsVersionCode < 260800000) {
              logInfo("Detected faulty GMS version (" + gmsVersionCode + "). Falling back to legacy SignInClient.");
              return true;
          }
      } catch (Exception e) {
          Log.e(TAG, "Error checking GMS version code", e);
      }

      return false;
  }

  /**
   * Sets the configuration of the sign-in api that should be used.
   */
  @SuppressWarnings("deprecation")
  public static void configure(
          boolean useGamesConfig,
          String webClientId,
          boolean requestAuthCode,
          boolean forceRefreshToken,
          boolean requestEmail,
          boolean requestIdToken,
          boolean hideUiPopups,
          String defaultAccountName,
          String[] additionalScopes,
          IListener requestHandle) {
    logDebug("TokenFragment.configure called");

    if (isUseSignInClient()) {
      logInfo("Using legacy SignInClient");
      signInClient = Identity.getSignInClient(UnityPlayer.currentActivity);

      signInFunction = new Function<Boolean, Task<AuthorizationResult>>() {
        @Override
        public Task<AuthorizationResult> apply(@NonNull Boolean silent) {
          if(isPending()) {
            TaskCompletionSource<AuthorizationResult> source = new TaskCompletionSource<>();
            source.trySetException(new Exception("Last task still pending"));
            return source.getTask();
          }

          BeginSignInRequest.Builder requestBuilder = BeginSignInRequest.builder();
          BeginSignInRequest.GoogleIdTokenRequestOptions.Builder googleIdTokenOptionsBuilder =
              BeginSignInRequest.GoogleIdTokenRequestOptions.builder()
                  .setSupported(true)
                  .setFilterByAuthorizedAccounts(silent);

          if (!Strings.isEmptyOrWhitespace(webClientId)) {
            googleIdTokenOptionsBuilder.setServerClientId(webClientId);
          }
          if (requestIdToken) {
            requestBuilder.setGoogleIdTokenRequestOptions(googleIdTokenOptionsBuilder.build());
          }
          requestBuilder.setAutoSelectEnabled(hideUiPopups);

          BeginSignInRequest beginSignInRequest = requestBuilder.build();
          TaskCompletionSource<SignInCredential> credentialSource = new TaskCompletionSource<>();

          signInClient.beginSignIn(beginSignInRequest)
              .addOnSuccessListener(result -> {
                try {
                  SignInActivity.setResultCallback(new SignInActivity.SignInResultCallback() {
                    @Override
                    public void onSignInResult(SignInCredential credential) {
                      credentialSource.trySetResult(credential);
                    }
                    @Override
                    public void onSignInError(Exception exception) {
                      credentialSource.trySetException(exception);
                    }
                    @Override
                    public void onSignInCancelled() {
                      credentialSource.trySetException(new ApiException(new com.google.android.gms.common.api.Status(CommonStatusCodes.CANCELED)));
                    }
                  });

                  Intent intent = new Intent(UnityPlayer.currentActivity, SignInActivity.class);
                  intent.putExtra("pending_intent", result.getPendingIntent().getIntentSender());
                  UnityPlayer.currentActivity.startActivity(intent);
                } catch (Exception e) {
                  credentialSource.trySetException(e);
                }
              })
              .addOnFailureListener(credentialSource::trySetException);

          return credentialSource.getTask().onSuccessTask(credential -> {
            requestHandle.onAuthenticatedLegacy(credential);

            AuthorizationRequest.Builder authRequestBuilder = new AuthorizationRequest.Builder();
            if (requestAuthCode && !Strings.isEmptyOrWhitespace(webClientId))
              authRequestBuilder.requestOfflineAccess(webClientId, forceRefreshToken);

            int additionalCount = additionalScopes != null ? additionalScopes.length : 0;
            List<Scope> scopes = new ArrayList<>(2 + additionalCount);
            scopes.add(new Scope(Scopes.PROFILE));
            if (requestEmail) scopes.add(new Scope(Scopes.EMAIL));
            if (additionalCount > 0) {
              for (String scope : additionalScopes) scopes.add(new Scope(scope));
            }
            if (!scopes.isEmpty()) authRequestBuilder.setRequestedScopes(scopes);

            return Identity.getAuthorizationClient(UnityPlayer.currentActivity).authorize(authRequestBuilder.build());
          })
          .addOnFailureListener(requestHandle)
          .addOnCanceledListener(requestHandle)
          .addOnSuccessListener(requestHandle::onAuthorized);
        }
      };
    } else {
      logInfo("Using CredentialManager");
      signInFunction = new Function<Boolean, Task<AuthorizationResult>>() {
        @Override
        public Task<AuthorizationResult> apply(@NonNull Boolean silent) {
          if(isPending()) {
            TaskCompletionSource<AuthorizationResult> source = new TaskCompletionSource<>();
            source.trySetException(new Exception("Last task still pending"));
            return source.getTask();
          }

          GetCredentialRequest.Builder builder = new GetCredentialRequest.Builder()
                  .setPreferImmediatelyAvailableCredentials(hideUiPopups);

          if(silent) {
            GetGoogleIdOption.Builder optionBuilder = new GetGoogleIdOption.Builder()
                    .setFilterByAuthorizedAccounts(hideUiPopups)
                    .setAutoSelectEnabled(hideUiPopups);
            if(defaultAccountName != null) optionBuilder.setNonce(defaultAccountName);
            if(!Strings.isEmptyOrWhitespace(webClientId)) optionBuilder.setServerClientId(webClientId);
            builder.addCredentialOption(optionBuilder.build());
          } else {
            builder.addCredentialOption(new GetSignInWithGoogleOption.Builder(webClientId).build());
          }

          TaskCompletionSource<GetCredentialResponse> source = new TaskCompletionSource<>();
          SignInActivity.setCredentialManagerRequest(builder.build(),
                  new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                    @Override
                    public void onResult(GetCredentialResponse result) {
                      source.trySetResult(result);
                    }
                    @Override
                    public void onError(@NotNull GetCredentialException e) {
                      source.trySetException(e);
                    }
                  });

          Intent intent = new Intent(UnityPlayer.currentActivity, SignInActivity.class);
          UnityPlayer.currentActivity.startActivity(intent);

          return source.getTask().onSuccessTask(response -> {
            Credential credential = response.getCredential();
            GoogleIdTokenCredential googleIdToken = GoogleIdTokenCredential.createFrom(credential.getData());
            requestHandle.onAuthenticated(googleIdToken);

            AuthorizationRequest.Builder authRequestBuilder = new AuthorizationRequest.Builder();
            if (requestAuthCode && !Strings.isEmptyOrWhitespace(webClientId))
              authRequestBuilder.requestOfflineAccess(webClientId, forceRefreshToken);

            int additionalCount = additionalScopes != null ? additionalScopes.length : 0;
            List<Scope> scopes = new ArrayList<>(2 + additionalCount);
            scopes.add(new Scope(Scopes.PROFILE));
            if (requestEmail) scopes.add(new Scope(Scopes.EMAIL));
            if (additionalCount > 0) {
              for (String scope : additionalScopes) scopes.add(new Scope(scope));
            }
            if (!scopes.isEmpty()) authRequestBuilder.setRequestedScopes(scopes);

            return Identity.getAuthorizationClient(UnityPlayer.currentActivity).authorize(authRequestBuilder.build());
          }).addOnFailureListener(requestHandle).addOnCanceledListener(requestHandle).addOnSuccessListener(requestHandle::onAuthorized);
        }
      };
    }
  }

  public static Task<AuthorizationResult> signIn() {
    task = signInFunction.apply(false);
    return task;
  }

  public static Task<AuthorizationResult> signInSilently() {
    task = signInFunction.apply(true);
    return task;
  }

  public static void cancel() {
    SignInActivity.finishActivity();
    task = null;
  }

  @SuppressWarnings("deprecation")
  public static void signOut() {
    cancel();
    if (isUseSignInClient()) {
      if (signInClient != null) {
        signInClient.signOut()
            .addOnSuccessListener(unused -> logInfo("signOut (legacy)"))
            .addOnFailureListener(e -> logError("signOut error (legacy): " + e.getMessage()));
      }
    } else {
      CredentialManager.create(UnityPlayer.currentActivity).clearCredentialStateAsync(new ClearCredentialStateRequest(),
              new CancellationSignal(),
              TaskExecutors.MAIN_THREAD,
              new CredentialManagerCallback<Void, ClearCredentialException>() {
                @Override
                public void onResult(Void unused) { logInfo("signOut"); }
                @Override
                public void onError(@NonNull ClearCredentialException e) { logError(e.getMessage()); }
              });
    }
  }

  static final String TAG = GoogleSignInHelper.class.getSimpleName();
  public static void logInfo(String msg) { if (loggingEnabled) Log.i(TAG, msg); }
  public static void logError(String msg) { Log.e(TAG, msg); }
  public static void logDebug(String msg) { if (loggingEnabled) Log.d(TAG, msg); }
}
