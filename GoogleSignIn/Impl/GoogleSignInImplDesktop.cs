#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using UnityEngine;

#if UNITY_EDITOR 
using UnityEditor;
#endif

using Newtonsoft.Json.Linq;

namespace Google.Impl
{
  internal class GoogleSignInImplDesktop : ISignInImpl, FutureAPIImpl<GoogleSignInUser>
  {
    GoogleSignInConfiguration configuration;

    public bool Pending { get; private set; }

    public GoogleSignInStatusCode Status { get; private set; }

    public GoogleSignInUser Result { get; private set; }

    public GoogleSignInImplDesktop(GoogleSignInConfiguration configuration)
    {
      this.configuration = configuration;
    }

    public void Disconnect()
    {
      throw new NotImplementedException();
    }

    public void EnableDebugLogging(bool flag)
    {
      throw new NotImplementedException();
    }

    public Future<GoogleSignInUser> SignIn()
    {
      SigningIn();
      return new Future<GoogleSignInUser>(this);
    }

    const string GoogleSignInCacheKey = "googleSignInCache";
    public void SignOut()
    {
#if UNITY_EDITOR 
      SessionState.EraseString(GoogleSignInCacheKey + "Code");
      SessionState.EraseString(GoogleSignInCacheKey);
#else
      PlayerPrefs.DeleteKey(GoogleSignInCacheKey + "Code");
      PlayerPrefs.DeleteKey(GoogleSignInCacheKey);
      PlayerPrefs.Save();
#endif
    }

    public Future<GoogleSignInUser> SignInSilently()
    {
      Status = GoogleSignInStatusCode.SIGN_IN_REQUIRED;
#if UNITY_EDITOR 
      string authCode = SessionState.GetString(GoogleSignInCacheKey + "Code",null);
      string json = SessionState.GetString(GoogleSignInCacheKey,null);
#else
      string authCode = PlayerPrefs.GetString(GoogleSignInCacheKey + "Code",null);
      string json = PlayerPrefs.GetString(GoogleSignInCacheKey,null);
#endif
      Pending = !string.IsNullOrEmpty(authCode) && !string.IsNullOrEmpty(json);
      if(Pending)
      {
        var taskScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        GetUserInfo(configuration,authCode,json,taskScheduler).ContinueWith((task) => {
          try
          {
            Result = task.Result;
            Status = GoogleSignInStatusCode.SUCCESS_CACHE;
          }
          catch(Exception e)
          {
            Status = GoogleSignInStatusCode.ERROR;

            Debug.LogException(e);
            if(e is AggregateException ae)
            {
              foreach(var inner in ae.InnerExceptions)
                Debug.LogException(inner);
            }

            throw;
          }
          finally
          {
            Pending = false;
          }
        });
      }
      return new Future<GoogleSignInUser>(this);
    }

    void SigningIn()
    {
      Pending = true;
      var state = GenerateRandomBase64Url(32);
      var codeVerifier = GenerateRandomBase64Url(64);
      var codeChallenge = GenerateCodeChallenge(codeVerifier);

      IListener listener = null;
      
      try
      {
        listener = 
          #if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
          new DeepLinkWindowsListener(ThreadSafeAppInfo.Identifier);
          #else
          new LoopbackListener();
          #endif
        
        var scopes = "openid email profile";
        if (configuration.AdditionalScopes != null)
        {
          scopes += " " + string.Join(" ", configuration.AdditionalScopes);
        }

        var openURL = "https://accounts.google.com/o/oauth2/v2/auth"
        + $"?client_id={Uri.EscapeDataString(configuration.ClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(listener.RedirectUri)}"
        + $"&response_type=code"
        + $"&scope={Uri.EscapeDataString(scopes)}"
        + $"&state={state}"
        + $"&code_challenge={codeChallenge}"
        + $"&code_challenge_method=S256";

        Debug.Log(openURL);
        Application.OpenURL(openURL);
      }
      catch(Exception e)
      {
        Debug.LogException(e);
        throw;
      }

      var taskScheduler = TaskScheduler.FromCurrentSynchronizationContext();
      listener.ListenAsync().ContinueWith(async task => {
        try
        {
          var queryDict = System.Web.HttpUtility.ParseQueryString(task.Result);
          if (queryDict.Get("state") != state) 
          {
            throw new Exception($"Received wrong state value {queryDict.Get("state")}, expected: {state}");
          }
          
          if (queryDict.Get("code") is not { } code || string.IsNullOrEmpty(code)) 
          {
            Status = GoogleSignInStatusCode.INVALID_ACCOUNT;
            listener.OnGetCodeFailed();
            listener.Dispose();
            return;
          }
          
          listener.OnGetCodeSuccess();

          string json = await HttpWebRequest.CreateHttp("https://www.googleapis.com/oauth2/v4/token").Post("application/x-www-form-urlencoded"
          , $"code={code}"
          + $"&client_id={configuration.ClientId}"
          + $"&client_secret={configuration.ClientSecret}"
          + $"&redirect_uri={listener.RedirectUri}"
          + $"&grant_type=authorization_code"
          + $"&code_verifier={codeVerifier}"
          ).ContinueWith(t => t.Result, taskScheduler);

          Result = await GetUserInfo(configuration,code,json,taskScheduler);

#if UNITY_EDITOR 
          SessionState.SetString(GoogleSignInCacheKey,json);
          SessionState.SetString(GoogleSignInCacheKey + "Code",code);
#else
          PlayerPrefs.SetString(GoogleSignInCacheKey,json);
          PlayerPrefs.SetString(GoogleSignInCacheKey + "Code",code);
          PlayerPrefs.Save();
#endif

          Status = GoogleSignInStatusCode.SUCCESS;
        }
        catch(Exception e)
        {
          Status = GoogleSignInStatusCode.ERROR;

          Debug.LogException(e);
          if(e is AggregateException ae)
          {
            foreach(var inner in ae.InnerExceptions)
              Debug.LogException(inner);
          }

          throw;
        }
        finally
        {
          Pending = false;
          
          listener.Dispose();
        }
      },taskScheduler);
    }

    private string GenerateRandomBase64Url(int byteLength) 
    {
      var bytes = new byte[byteLength];
      RandomNumberGenerator.Fill(bytes);
      return Base64UrlEncodeNoPadding(bytes);
    }

    private string GenerateCodeChallenge(string codeVerifier) 
    {
      using var sha256 = SHA256.Create();
      var       bytes  = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
      return Base64UrlEncodeNoPadding(bytes);
    }

    private string Base64UrlEncodeNoPadding(byte[] bytes) 
    {
      return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    

		static async Task<GoogleSignInUser> GetUserInfo(GoogleSignInConfiguration configuration,string authCode,string json,TaskScheduler taskScheduler)
    {
      var jobj = JObject.Parse(json);

			var accessToken = (string)jobj.GetValue("access_token")!;
			var expiresIn = (int)jobj.GetValue("expires_in")!;
			var scope = (string)jobj.GetValue("scope")!;
			var tokenType = (string)jobj.GetValue("token_type")!;

			var user = new GoogleSignInUser();
			if(configuration.RequestAuthCode)
				user.AuthCode = authCode;

			if(configuration.RequestIdToken)
				user.IdToken = (string)jobj.GetValue("id_token")!;

			var request = HttpWebRequest.CreateHttp("https://openidconnect.googleapis.com/v1/userinfo");
			request.Method = "GET";
			request.Headers.Add("Authorization","Bearer " + accessToken);

			var data = await request.GetResponseAsStringAsync().ContinueWith((task) => task.Result,taskScheduler);
			var userInfo = JObject.Parse(data);
			user.UserId = (string)userInfo.GetValue("sub")!;
			user.DisplayName = (string)userInfo.GetValue("name")!;

			if(configuration.RequestEmail)
				user.Email = (string)userInfo.GetValue("email")!;

			if(configuration.RequestProfile)
			{
				user.GivenName = (string)userInfo.GetValue("given_name")!;
				user.FamilyName = (string)userInfo.GetValue("family_name")!;
				user.ImageUrl = Uri.TryCreate((string)userInfo.GetValue("picture")!,UriKind.Absolute,out var url) ? url : null!;
			}

			return user;
		}
	}

  public static partial class DesktopExt
  {
    public static Task<string> Post(this HttpWebRequest request,string contentType,string data,Encoding encoding = null)
    {
      if(encoding == null)
        encoding = Encoding.UTF8;

      request.Method = "POST";
      request.ContentType = contentType;
      using(var stream = request.GetRequestStream())
        stream.Write(encoding.GetBytes(data));

      return request.GetResponseAsStringAsync(encoding);
    }

    public static async Task<string> GetResponseAsStringAsync(this HttpWebRequest request,Encoding encoding = null)
    {
      using(var response = await request.GetResponseAsync())
      {
        using(var stream = response.GetResponseStream())
          return stream.ReadToEnd(encoding ?? Encoding.UTF8);
      }
    }

    public static string ReadToEnd(this Stream stream,Encoding encoding = null) => new StreamReader(stream,encoding ?? Encoding.UTF8).ReadToEnd();
    public static void Write(this Stream stream,byte[] data) => stream.Write(data,0,data.Length);
    public static partial void TryBringGameToFront();
  }

  internal static class ThreadSafeAppInfo {
    public static bool IsEditor { get; private set; }
    public static string ProductName { get; private set; }
    public static string Identifier { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InitApplicationInfo() 
    {
      IsEditor    = Application.isEditor;
      ProductName = Application.productName;
      Identifier  = Application.identifier;
      if (Application.isEditor || string.IsNullOrEmpty(Identifier)) {
        Identifier = $"com.{Clean(Application.companyName)}.{Clean(Application.productName)}";
      }

      static string Clean(string part) => Regex.Replace(part, @"[^a-zA-Z0-9]", "");
    }
  }
}

#endif

