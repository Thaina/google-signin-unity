package com.google.googlesignin;

import android.app.PendingIntent;
import android.content.Intent;
import android.content.IntentSender;
import android.os.Bundle;
import android.util.Log;

import androidx.annotation.Nullable;

import com.unity3d.player.UnityPlayerActivity;

public class GoogleSignInActivity extends UnityPlayerActivity {

    public static final int REQUEST_CODE = 1001;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Log.d("GoogleSignInActivity", "GoogleSignInActivity started");

        // Retrieve PendingIntent from intent extras
        PendingIntent pendingIntent = getIntent().getParcelableExtra("pendingIntent");

        if (pendingIntent != null) {
            try {
                // Start the PendingIntent for the Google Sign-In resolution
                startIntentSenderForResult(pendingIntent.getIntentSender(), REQUEST_CODE, null, 0, 0, 0);
                Log.d("GoogleSignInActivity", "PendingIntent started for Google Sign-In");
            } catch (IntentSender.SendIntentException e) {
                Log.e("GoogleSignInActivity", "Error starting PendingIntent: " + e.getMessage());
                // Notify GoogleSignInHelper of the failure via the callback
                GoogleSignInHelper.handleSignInResult(RESULT_CANCELED, null); // Trigger failure callback
                finish(); // Finish the activity after failure
            }
        } else {
            Log.e("GoogleSignInActivity", "PendingIntent is null");
            // Notify GoogleSignInHelper of the failure via the callback
            GoogleSignInHelper.handleSignInResult(RESULT_CANCELED, null); // Trigger failure callback
            finish(); // Finish the activity since there is no PendingIntent to handle
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, @Nullable Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode == REQUEST_CODE) {
            Log.d("GoogleSignInActivity", "Received onActivityResult for Google Sign-In");
            // Pass the result back to GoogleSignInHelper to trigger the appropriate callback
            GoogleSignInHelper.handleSignInResult(resultCode, data);
            finish(); // Finish this activity after processing the result
        }
    }
}
