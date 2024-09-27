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

import android.content.IntentSender;
import android.content.Intent;
import android.app.PendingIntent;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.util.Log;
import android.util.Base64;

import androidx.annotation.NonNull;
import androidx.credentials.ClearCredentialStateRequest;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.exceptions.ClearCredentialException;
import androidx.credentials.exceptions.GetCredentialException;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.lang.reflect.Method;

import com.google.android.gms.common.api.Status;
import com.google.android.gms.auth.api.signin.GoogleSignIn;
import com.google.android.gms.auth.api.signin.GoogleSignInAccount;
import com.google.android.gms.auth.api.signin.GoogleSignInResult;
import com.google.android.gms.common.api.ApiException;
import com.google.android.gms.auth.api.identity.AuthorizationRequest;
import com.google.android.gms.auth.api.identity.AuthorizationResult;
import com.google.android.gms.auth.api.identity.Identity;
import com.google.android.gms.common.Scopes;
import com.google.android.gms.common.api.CommonStatusCodes;
import com.google.android.gms.common.api.Scope;
import com.google.android.gms.common.util.Strings;
import com.google.android.gms.tasks.OnCompleteListener;
import com.google.android.gms.tasks.OnSuccessListener;
import com.google.android.gms.tasks.OnFailureListener;
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

import com.google.googlesignin.GoogleSignInActivity;

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

  public static IListener requestHandle;
  private static CancellationSignal cancellationSignal;
  private static Task<AuthorizationResult> task;
  private static Function<Boolean, Task<AuthorizationResult>> signInFunction;
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
   * Sets the configuration of the sign-in api that should be used.
   *
   * @param useGamesConfig     - true if the GAMES_CONFIG should be used when
   *                           signing-in.
   * @param webClientId        - the web client id of the backend server
   *                           associated with this application.
   * @param requestAuthCode    - true if a server auth code is needed. This also
   *                           requires the web
   *                           client id to be set.
   * @param forceRefreshToken  - true to force a refresh token when using the
   *                           server auth code.
   * @param requestEmail       - true if email address of the user is requested.
   * @param requestIdToken     - true if an id token for the user is requested.
   * @param hideUiPopups       - true if the popups during sign-in from the Games
   *                           API should be hidden.
   *                           This only has affect if useGamesConfig is true.
   * @param defaultAccountName - the account name to attempt to default to when
   *                           signing in.
   * @param additionalScopes   - additional API scopes to request when
   *                           authenticating.
   * @param requestHandle      - the handle to this request, created by the native
   *                           C++ code, this is used
   *                           to correlate the response with the request.
   */
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

    signInFunction = new Function<Boolean, Task<AuthorizationResult>>() {
      @Override
      public Task<AuthorizationResult> apply(@NonNull Boolean silent) {
        if(isPending()) {
          TaskCompletionSource<AuthorizationResult> source = new TaskCompletionSource<>();
          source.trySetException(new Exception("Last task still pending"));
          return source.getTask();
        }

        cancellationSignal = new CancellationSignal();

        GetCredentialRequest.Builder getCredentialRequestBuilder = new GetCredentialRequest.Builder()
                .setPreferImmediatelyAvailableCredentials(hideUiPopups);

        if(silent) {
          GetGoogleIdOption.Builder getGoogleIdOptionBuilder = new GetGoogleIdOption.Builder()
                  .setFilterByAuthorizedAccounts(hideUiPopups)
                  .setAutoSelectEnabled(hideUiPopups);

          if(defaultAccountName != null)
            getGoogleIdOptionBuilder.setNonce(defaultAccountName);

          if(!Strings.isEmptyOrWhitespace(webClientId))
            getGoogleIdOptionBuilder.setServerClientId(webClientId);

          getCredentialRequestBuilder.addCredentialOption(getGoogleIdOptionBuilder.build());
        }
        else {
          GetSignInWithGoogleOption.Builder getSignInWithGoogleOptionBuilder = new GetSignInWithGoogleOption.Builder(webClientId);
          getCredentialRequestBuilder.addCredentialOption(getSignInWithGoogleOptionBuilder.build());
        }

        TaskCompletionSource<GetCredentialResponse> source = new TaskCompletionSource<>();

        CredentialManager.create(UnityPlayer.currentActivity).getCredentialAsync(UnityPlayer.currentActivity,
                getCredentialRequestBuilder.build(),
                cancellationSignal,
                TaskExecutors.MAIN_THREAD,
                new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                  @Override
                  public void onResult(GetCredentialResponse getCredentialResponse) {
                    source.trySetResult(getCredentialResponse);
                  }

                  @Override
                  public void onError(@NotNull GetCredentialException e) {
                    source.trySetException(e);
                  }
                });

        return source.getTask().onSuccessTask(new SuccessContinuation<GetCredentialResponse, AuthorizationResult>() {
          @NonNull
          @Override
          public Task<AuthorizationResult> then(GetCredentialResponse getCredentialResponse) throws Exception {
            try {
              Credential credential = getCredentialResponse.getCredential();
              Log.i(TAG, "credential.getType() : " + credential.getType());

              GoogleIdTokenCredential googleIdTokenCredential = GoogleIdTokenCredential.createFrom(credential.getData());
              requestHandle.onAuthenticated(googleIdTokenCredential);
            }
            catch (Exception e) {
              throw e;
            }

            AuthorizationRequest.Builder authorizationRequestBuilder = new AuthorizationRequest.Builder();
            if (requestAuthCode && !Strings.isEmptyOrWhitespace(webClientId))
              authorizationRequestBuilder.requestOfflineAccess(webClientId, forceRefreshToken);

            int additionalCount = additionalScopes != null ? additionalScopes.length : 0;
            List<Scope> scopes = new ArrayList<>(2 + additionalCount);
            scopes.add(new Scope(Scopes.PROFILE));
            if (requestEmail)
              scopes.add(new Scope(Scopes.EMAIL));
            if (additionalCount > 0) {
              for (String scope : additionalScopes) {
                scopes.add(new Scope(scope));
              }
            }

            if (!scopes.isEmpty())
              authorizationRequestBuilder.setRequestedScopes(scopes);

            return Identity.getAuthorizationClient(UnityPlayer.currentActivity).authorize(authorizationRequestBuilder.build());
          }
        }).addOnFailureListener(requestHandle).addOnCanceledListener(requestHandle).addOnSuccessListener(new OnSuccessListener<AuthorizationResult>() {
          @Override
          public void onSuccess(AuthorizationResult authorizationResult) {
            processAuthorizationResult(authorizationResult, requestHandle);
          }
        }).addOnCompleteListener(new OnCompleteListener<AuthorizationResult>() {
          @Override
          public void onComplete(@NonNull Task<AuthorizationResult> _unused) {
            cancellationSignal = null;
          }
        });
      }
    };
  }

  // Method to process the AuthorizationResult and convert it to SignInResultWrapper
    public static void processAuthorizationResult(AuthorizationResult authorizationResult, IListener requestHandle) {
        logDebug("Processing authorization result...");

        String serverAuthCode = authorizationResult.getServerAuthCode();
        if (serverAuthCode != null) 
        {
            logDebug("Server Auth Code: " + serverAuthCode);
            SignInResultWrapper wrappedResult = new SignInResultWrapper(authorizationResult.getServerAuthCode());
            requestHandle.onAuthorized(wrappedResult);
        } 
        else 
        {
            logDebug("No Server Auth Code returned.");
        }

        if (authorizationResult.hasResolution()) 
        {
            PendingIntent pendingIntent = authorizationResult.getPendingIntent();
            if (pendingIntent != null) 
            {
                try 
                {
                    Intent signInIntent = new Intent(UnityPlayer.currentActivity, GoogleSignInActivity.class);
                    signInIntent.putExtra("pendingIntent", pendingIntent);
                    UnityPlayer.currentActivity.startActivity(signInIntent);
                    logDebug("Started GoogleSignInActivity with PendingIntent.");
                } 
                catch (Exception e) 
                {
                    logError("Failed to launch GoogleSignInActivity: " + e.getMessage());
                }
            } 
            else 
            {
                logDebug("PendingIntent is null, even though hasResolution() is true.");
            }
        } 
        else 
        {
            logDebug("No resolution required.");
        }
    }

  // Method to handle sign-in results
  public static void handleSignInResult(int resultCode, Intent data) {
      logDebug("Handling sign-in result with resultCode: " + resultCode);
  
      if (resultCode == GoogleSignInActivity.RESULT_OK && data != null) {
          logDebug("Sign-in successful, inspecting Intent extras.");
  
          Bundle extras = data.getExtras();
          if (extras != null) {
              for (String key : extras.keySet()) {
                  Object value = extras.get(key);
  
                  if (key.equals("authorization_result") && value instanceof byte[]) 
                  {
                      byte[] authResultBytes = (byte[]) value;
                      printByteArray(authResultBytes);
                  } 
                  else 
                  {
                      logDebug("Key: " + key + " has value of type: " + (value != null ? value.getClass().getName() : "null"));
                  }
              }
          } else {
              logDebug("Intent has no extras.");
          }
      } else {
          logError("Sign-in failed or canceled, no data received.");
      }
  }

  public static void printByteArray(byte[] byteArray) {
      if (byteArray == null || byteArray.length == 0) {
          logDebug("Byte array is empty or null.");
          return;
      }
      
      StringBuilder byteStringBuilder = new StringBuilder();
      for (byte b : byteArray) {
          byteStringBuilder.append(String.format("%02X ", b));  // Convert byte to hex
      }
      
      logDebug("Byte array contents: " + byteStringBuilder.toString());
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
    if(isPending() && cancellationSignal != null){
      cancellationSignal.cancel();
      cancellationSignal = null;
    }

    task = null;
  }

  public static void signOut() {
    cancel();

    CredentialManager.create(UnityPlayer.currentActivity).clearCredentialStateAsync(new ClearCredentialStateRequest(),
            new CancellationSignal(),
            TaskExecutors.MAIN_THREAD,
            new CredentialManagerCallback<Void, ClearCredentialException>() {
              @Override
              public void onResult(Void unused) {
                logInfo("signOut");
              }

              @Override
              public void onError(@NonNull ClearCredentialException e) {
                logError(e.getMessage());
              }
            });
  }

  static final String TAG = GoogleSignInHelper.class.getSimpleName();

  public static void logInfo(String msg) {
    if (loggingEnabled) {
      Log.i(TAG, msg);
    }
  }

  public static void logError(String msg) {
    Log.e(TAG, msg);
  }

  public static void logDebug(String msg) {
    if (loggingEnabled) {
      Log.d(TAG, msg);
    }
  }
}
