#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net;
using System.Net.NetworkInformation;
using System.Collections.Specialized;
using UnityEngine;

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

    public Future<GoogleSignInUser> SignInSilently()
    {
      SigningIn();
      return new Future<GoogleSignInUser>(this);
    }

    public void SignOut()
    {
      Debug.Log("No need on editor?");
    }

    static HttpListener BindLocalHostFirstAvailablePort()
    {
      ushort minPort = 49215;
#if UNITY_EDITOR_WIN
      var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
      return Enumerable.Range(minPort, ushort.MaxValue - minPort).Where((i) => !listeners.Any((x) => x.Port == i)).Select((port) => {
#elif UNITY_EDITOR_OSX
      return Enumerable.Range(minPort, ushort.MaxValue - minPort).Select((port) => {
#else
      return Enumerable.Range(0,10).Select((i) => UnityEngine.Random.Range(minPort,ushort.MaxValue)).Select((port) => {
#endif
        try
        {
          var listener = new HttpListener();
          listener.Prefixes.Add($"http://localhost:{port}/");
          listener.Start();
          return listener;
        }
        catch(System.Exception e)
        {
          Debug.LogException(e);
          return null;
        }
      }).FirstOrDefault((listener) => listener != null);
    }

    public static NameValueCollection ParseQueryString(string query)
    {
      var nvc = new NameValueCollection();

      if (string.IsNullOrEmpty(query))
      {
        return nvc;
      }

      // Remove leading '?' if it's a full URL query string
      if (query.StartsWith("?"))
      {
        query = query.Substring(1);
      }

      // Split by '&' to get individual key-value pairs
      string[] pairs = query.Split('&');

      foreach (string pair in pairs)
      {
        if (string.IsNullOrEmpty(pair))
        {
          continue; // Skip empty pairs (e.g., "a=1&&b=2")
        }

        // Find the first '='
        int indexOfEquals = pair.IndexOf('=');

        string key;
        string value;

        if (indexOfEquals == -1)
        {
          // No '=' found, treat the whole segment as a key with an empty value
          // Example: "param_without_value" -> param_without_value=""
          key = pair;
          value = string.Empty;
        }
        else
        {
          // Split key and value based on the first '='
          key = pair.Substring(0, indexOfEquals);
          value = pair.Substring(indexOfEquals + 1);
        }

        // URL-decode both key and value
        // WebUtility.UrlDecode handles %xx decoding and converting '+' to space.
        key = WebUtility.UrlDecode(key);
        value = WebUtility.UrlDecode(value);

        // Add to the NameValueCollection.
        // NameValueCollection automatically handles multiple values for the same key.
        nvc.Add(key, value);
      }

      return nvc;
    }

    void SigningIn()
    {
      Pending = true;
      var httpListener = BindLocalHostFirstAvailablePort();
      try
      {
        var openURL = "https://accounts.google.com/o/oauth2/v2/auth?" + Uri.EscapeUriString("scope=openid email profile&response_type=code&redirect_uri=" + httpListener.Prefixes.FirstOrDefault() + "&client_id=" + configuration.WebClientId);
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
          var queryDictionary = ParseQueryString(queryString);
          if(queryDictionary == null || queryDictionary.Get("code") is not string code || string.IsNullOrEmpty(code))
          {
            Status = GoogleSignInStatusCode.INVALID_ACCOUNT;

            context.Response.StatusCode = 404;
            context.Response.OutputStream.Write(Encoding.UTF8.GetBytes("Cannot get code"));
            context.Response.Close();
            return;
          }

          context.Response.StatusCode = 200;
          context.Response.OutputStream.Write(Encoding.UTF8.GetBytes("Can close this page"));
          context.Response.Close();

          var jobj = await HttpWebRequest.CreateHttp("https://www.googleapis.com/oauth2/v4/token").Post("application/x-www-form-urlencoded","code=" + code + "&client_id=" + configuration.WebClientId + "&client_secret=" + configuration.ClientSecret + "&redirect_uri=" + httpListener.Prefixes.FirstOrDefault() + "&grant_type=authorization_code").ContinueWith((task) => {
            return JObject.Parse(task.Result);
          },taskScheduler);

          var accessToken = (string)jobj.GetValue("access_token");
          var expiresIn = (int)jobj.GetValue("expires_in");
          var scope = (string)jobj.GetValue("scope");
          var tokenType = (string)jobj.GetValue("token_type");

          var user = new GoogleSignInUser();
          if(configuration.RequestAuthCode)
            user.AuthCode = code;

          if(configuration.RequestIdToken)
            user.IdToken = (string)jobj.GetValue("id_token");

          var request = HttpWebRequest.CreateHttp("https://openidconnect.googleapis.com/v1/userinfo");
          request.Method = "GET";
          request.Headers.Add("Authorization", "Bearer " + accessToken);

          var data = await request.GetResponseAsStringAsync().ContinueWith((task) => task.Result,taskScheduler);
          var userInfo = JObject.Parse(data);
          user.UserId = (string)userInfo.GetValue("sub");
          user.DisplayName = (string)userInfo.GetValue("name");

          if(configuration.RequestEmail)
            user.Email = (string)userInfo.GetValue("email");

          if(configuration.RequestProfile)
          {
            user.GivenName = (string)userInfo.GetValue("given_name");
            user.FamilyName = (string)userInfo.GetValue("family_name");
            user.ImageUrl = Uri.TryCreate((string)userInfo.GetValue("picture"),UriKind.Absolute,out var url) ? url : null;
          }

          Result = user;

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
        }
      },taskScheduler);
    }
  }

  public static class EditorExt
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
  }
}
#endif
