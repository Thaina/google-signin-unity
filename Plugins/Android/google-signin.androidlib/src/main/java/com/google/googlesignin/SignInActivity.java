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
import android.util.Log;

import com.google.android.gms.auth.api.identity.Identity;
import com.google.android.gms.auth.api.identity.SignInCredential;
import com.google.android.gms.common.api.ApiException;

/**
 * Transparent activity to handle the sign-in result for legacy SignInClient.
 */
public class SignInActivity extends Activity {
    private static final String TAG = "GoogleSignInActivity";
    private static final int RC_SIGN_IN = 9001;
    
    private static SignInResultCallback resultCallback;
    
    public interface SignInResultCallback {
        void onSignInResult(SignInCredential credential);
        void onSignInError(Exception exception);
    }
    
    public static void setResultCallback(SignInResultCallback callback) {
        resultCallback = callback;
    }
    
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        
        Intent intent = getIntent();
        if (intent != null && intent.hasExtra("pending_intent")) {
            try {
                startIntentSenderForResult(
                    intent.getParcelableExtra("pending_intent"),
                    RC_SIGN_IN,
                    null, 0, 0, 0);
            } catch (Exception e) {
                Log.e(TAG, "Error starting sign-in intent", e);
                if (resultCallback != null) {
                    resultCallback.onSignInError(e);
                }
                finish();
            }
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
                }
            } catch (ApiException e) {
                Log.e(TAG, "Sign-in failed", e);
                if (resultCallback != null) {
                    resultCallback.onSignInError(e);
                }
            } catch (Exception e) {
                Log.e(TAG, "Unexpected error", e);
                if (resultCallback != null) {
                    resultCallback.onSignInError(e);
                }
            }
            finish();
        }
    }
}
