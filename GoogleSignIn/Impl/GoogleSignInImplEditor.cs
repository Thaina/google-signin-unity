#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;

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
  internal class GoogleSignInImplEditor : ISignInImpl, FutureAPIImpl<GoogleSignInUser>
  {
    GoogleSignInConfiguration configuration;

    public bool Pending { get; private set; }

    public GoogleSignInStatusCode Status { get; private set; }

    public GoogleSignInUser Result { get; private set; }

    public GoogleSignInImplEditor(GoogleSignInConfiguration configuration)
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

    static HttpListener BindLocalHostFirstAvailablePort()
    {
      int maxRetries = 10;
      while (maxRetries-- > 0) 
      {
        try 
        {
          int port;
          using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) 
          {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)socket.LocalEndPoint).Port;
          }

          var listener = new HttpListener();
          listener.Prefixes.Add($"http://{IPAddress.Loopback}:{port}/");
          listener.Start();
          return listener;
        } 
        catch (Exception e) 
        {
          Debug.LogWarning($"Failed to bind to localhost, {maxRetries} retries remaining... Detail: {e.Message}");
        }
      }
      
      throw new Exception("Failed to bind to localhost.");
    }

    void SigningIn()
    {
      Pending = true;
      var httpListener = BindLocalHostFirstAvailablePort();
      var state = GenerateRandomBase64Url(32);
      var codeVerifier = GenerateRandomBase64Url(64);
      var codeChallenge = GenerateCodeChallenge(codeVerifier);
      
      try
      {
        var scopes = "openid email profile";
        if (configuration.AdditionalScopes != null)
        {
          scopes += " " + string.Join(" ", configuration.AdditionalScopes);
        }

        var openURL = "https://accounts.google.com/o/oauth2/v2/auth"
        + $"?client_id={Uri.EscapeDataString(configuration.WebClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(httpListener.Prefixes.First())}"
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
      httpListener.GetContextAsync().ContinueWith(async(task) => {
        try
        {
          Debug.Log(task);
          var context = task.Result;
          var queryString = context.Request.Url.Query;
          var queryDictionary = System.Web.HttpUtility.ParseQueryString(queryString);
          if (queryDictionary.Get("state") != state
           || queryDictionary.Get("code") is not { } code || string.IsNullOrEmpty(code)) 
          {
            Status = GoogleSignInStatusCode.INVALID_ACCOUNT;
            SendHtmlResponse(context.Response, isSuccess: false);
            return;
          }
          
          SendHtmlResponse(context.Response, isSuccess: true);
          EditorExt.TryBringGameToFront();

          string json = await HttpWebRequest.CreateHttp("https://www.googleapis.com/oauth2/v4/token").Post("application/x-www-form-urlencoded"
          , $"code={code}"
          + $"&client_id={configuration.WebClientId}"
          + $"&client_secret={configuration.ClientSecret}"
          + $"&redirect_uri={httpListener.Prefixes.First()}"
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
          
          httpListener.Stop();
          httpListener.Close();
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
    
    private void SendHtmlResponse(HttpListenerResponse response, bool isSuccess) 
    {
      var titleColor = isSuccess ? "#4CAF50" : "#F44336";
      var titleContent = isSuccess ? "Authentication Successful!" : "Authentication Failed!";
      var detail = isSuccess ? "You can now close this window and return to the game." : "Cannot get code.";
      var statusCode = isSuccess ? 200 : 404;
      
      byte[] buffer = Encoding.UTF8.GetBytes($@"
        <html>
        <head>
            <style>
                :root {{
                    color-scheme: light dark;
                }}
                body {{
                    background-color: Canvas;
                    color: CanvasText;
                    font-family: system-ui, -apple-system, sans-serif;
                    text-align: center;
                    padding-top: 50px;
                    transition: background-color 0.3s, color 0.3s;
                }}
                h1 {{
                    color: {titleColor};
                }}
            </style>
        </head>
        <body>
            <h1>{titleContent}</h1>
            <p>{detail}</p>
        </body>
        </html>");

      try 
      {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
      } 
      catch 
      { 
        // Browser might close early
      }
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

  public static partial class EditorExt
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

  public static class ThreadSafeAppInfo {
    public static bool IsEditor { get; private set; }
    public static string ProductName { get; private set; }
    public static string Identifier { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InitApplicationInfo() 
    {
      IsEditor    = Application.isEditor;
      ProductName = Application.productName;
      Identifier  = Application.identifier;
    }
  }
}

#endif

