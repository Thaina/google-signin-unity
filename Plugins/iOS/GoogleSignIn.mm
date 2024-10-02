#import "GoogleSignIn.h"
#import <GoogleSignIn/GIDSignInResult.h>
#import <GoogleSignIn/GIDGoogleUser.h>
#import <GoogleSignIn/GIDProfileData.h>
#import <GoogleSignIn/GIDSignIn.h>
#import <GoogleSignIn/GIDToken.h>
#import <UnityAppController.h>
#import "UnityInterface.h"
#import <memory>

// These values are in the Unity plugin code.  The iOS specific
// codes are mapped to these.
static const int kStatusCodeSuccessCached = -1;
static const int kStatusCodeSuccess = 0;
static const int kStatusCodeApiNotConnected = 1;
static const int kStatusCodeCanceled = 2;
static const int kStatusCodeInterrupted = 3;
static const int kStatusCodeInvalidAccount = 4;
static const int kStatusCodeTimeout = 5;
static const int kStatusCodeDeveloperError = 6;
static const int kStatusCodeInternalError = 7;
static const int kStatusCodeNetworkError = 8;
static const int kStatusCodeError = 9;

void UnpauseUnityPlayer() {
  dispatch_async(dispatch_get_main_queue(), ^{
    if (UnityIsPaused() > 0) {
      UnityPause(0);
    }
  });
}

struct SignInResult {
  int result_code;
  bool finished;
  NSString* serverAuthCode;
};

std::unique_ptr<SignInResult> currentResult_;
NSRecursiveLock *resultLock = [NSRecursiveLock alloc];

@implementation GoogleSignInHandler

GIDConfiguration* signInConfiguration = nil;
NSString* loginHint = nil;
NSMutableArray* additionalScopes = nil;

+ (GoogleSignInHandler *)sharedInstance {
  static dispatch_once_t once;
  static GoogleSignInHandler *sharedInstance;
  dispatch_once(&once, ^{
    sharedInstance = [self alloc];
  });
  return sharedInstance;
}

- (void)signIn:(GIDSignIn *)signIn
    presentViewController:(UIViewController *)viewController {
  NSLog(@"Presenting Google Sign-In UI");
  UnityPause(true);
  [UnityGetGLViewController() presentViewController:viewController
                                           animated:YES
                                         completion:^{
                                             NSLog(@"Google Sign-In UI Presented");
                                         }];
}

- (void)signIn:(GIDSignIn *)signIn
    dismissViewController:(UIViewController *)viewController {
  NSLog(@"Dismissing Google Sign-In UI");
  UnityPause(false);
  [UnityGetGLViewController() dismissViewControllerAnimated:YES completion:^{
      NSLog(@"Google Sign-In UI Dismissed");
  }];
}

- (void)signIn:(GIDSignIn *)signIn
    didSignInForUser:(GIDGoogleUser *)user
           withError:(NSError *)_error {
  if (_error == nil) {
    NSLog(@"Sign-in successful. User: %@", user.userID);
    NSLog(@"Auth code: %@", user.serverAuthCode ? user.serverAuthCode : @"No auth code returned");
    if (currentResult_) {
      currentResult_->result_code = kStatusCodeSuccess;
      currentResult_->serverAuthCode = user.serverAuthCode;
      currentResult_->finished = true;
    } else {
      NSLog(@"Error: No currentResult to set status on!");
    }
  } else {
    NSLog(@"Sign-in failed with error: %@", _error.localizedDescription);
    if (currentResult_) {
      switch (_error.code) {
      case kGIDSignInErrorCodeUnknown:
        currentResult_->result_code = kStatusCodeError;
        break;
      case kGIDSignInErrorCodeKeychain:
        currentResult_->result_code = kStatusCodeInternalError;
        break;
      case kGIDSignInErrorCodeHasNoAuthInKeychain:
        currentResult_->result_code = kStatusCodeError;
        break;
      case kGIDSignInErrorCodeCanceled:
        currentResult_->result_code = kStatusCodeCanceled;
        break;
      default:
        NSLog(@"Unmapped error code: %ld, returning Error",
              static_cast<long>(_error.code));
        currentResult_->result_code = kStatusCodeError;
      }

      currentResult_->finished = true;
      UnpauseUnityPlayer();
    } else {
      NSLog(@"Error: No currentResult to set status on!");
    }
  }
}

- (void)signIn:(GIDSignIn *)signIn
    didDisconnectWithUser:(GIDGoogleUser *)user
                withError:(NSError *)_error {
  if (_error == nil) {
    NSLog(@"Successfully disconnected user: %@", user.userID);
  } else {
    NSLog(@"Failed to disconnect user with error: %@", _error.localizedDescription);
  }
}

@end

extern "C" {

bool GoogleSignIn_Configure(void *unused, bool useGameSignIn,
                            const char *webClientId, bool requestAuthCode,
                            bool forceTokenRefresh, bool requestEmail,
                            bool requestIdToken, bool hidePopups,
                            const char **additionalScopes, int scopeCount,
                            const char *accountName) {
    NSLog(@"Configuring Google Sign-In");
    
    NSString *path = [[NSBundle mainBundle] pathForResource:@"GoogleService-Info" ofType:@"plist"];
    NSDictionary *dict = [NSDictionary dictionaryWithContentsOfFile:path];
    NSString *clientId = [dict objectForKey:@"CLIENT_ID"];
    GIDConfiguration* config = [[GIDConfiguration alloc] initWithClientID:clientId];
    
    if (webClientId) {
      config = [[GIDConfiguration alloc] initWithClientID:clientId serverClientID:[NSString stringWithUTF8String:webClientId]];
      NSLog(@"Using WebClientId: %s", webClientId);
    }
    
    [GoogleSignInHandler sharedInstance]->signInConfiguration = config;

    if (scopeCount > 0) {
        NSMutableArray *tmpary = [[NSMutableArray alloc] initWithCapacity:scopeCount];
        for (int i = 0; i < scopeCount; i++) {
            [tmpary addObject:[NSString stringWithUTF8String:additionalScopes[i]]];
            NSLog(@"Adding additional scope: %s", additionalScopes[i]);
        }
        [GoogleSignInHandler sharedInstance]->additionalScopes = tmpary;
    }

    if (accountName) {
      [GoogleSignInHandler sharedInstance]->loginHint = [NSString stringWithUTF8String:accountName];
      NSLog(@"Setting account name hint: %s", accountName);
    }

    NSLog(@"Google Sign-In configured successfully");
    return !useGameSignIn;
}

void *GoogleSignIn_SignIn() {
  NSLog(@"Starting Google Sign-In process");
  SignInResult *result = startSignIn();
  
  if (!result) {
    [[GIDSignIn sharedInstance] signInWithPresentingViewController:UnityGetGLViewController()
                                hint:[GoogleSignInHandler sharedInstance]->loginHint
                                completion:^(GIDSignInResult *result, NSError *error) {
        GIDGoogleUser *user = result.user;
        NSLog(@"Google Sign-In process completed. User ID: %@", user.userID);
        NSLog(@"Auth code received: %@", result.serverAuthCode ? result.serverAuthCode : @"No auth code");
        currentResult_.get()->serverAuthCode = result.serverAuthCode;
        [[GoogleSignInHandler sharedInstance] signIn:[GIDSignIn sharedInstance] didSignInForUser:user withError:error];
    }];
    result = currentResult_.get();
  }
  return result;
}

} // extern "C"
