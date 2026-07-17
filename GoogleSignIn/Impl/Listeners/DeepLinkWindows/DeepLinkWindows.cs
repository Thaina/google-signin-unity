#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Google.Impl {
    internal class DeepLinkWindows : MonoBehaviour {
        private Action<string> callback;

        private string resPath;

        private bool isStopped;

        /// <summary>
        /// Use <see cref="StartListen"/> instead.
        /// </summary>
        private DeepLinkWindows() { }

        public static DeepLinkWindows StartListen(string appUri, Action<string> callback, bool dontDestroyOnLoad = true) {
            var instanceObj = new GameObject(nameof(DeepLinkWindows));
            if (dontDestroyOnLoad) DontDestroyOnLoad(instanceObj);

            var instance = instanceObj.AddComponent<DeepLinkWindows>();
            instance.Initialize(appUri, callback);

            return instance;
        }

        private void Initialize(string appUri, Action<string> callback) {
            this.callback = callback;
            this.resPath  = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString()).Replace("/", "\\");
            var relayPath = Path.Combine(Application.streamingAssetsPath, Application.productName + ".exe").Replace("/", "\\");
            var pid       = Process.GetCurrentProcess().Id;
            var hwnd      = Process.GetCurrentProcess().MainWindowHandle.ToInt64().ToString();


            using var key = Registry.CurrentUser.CreateSubKey($@"SOFTWARE\Classes\{appUri}")
             ?? throw new Exception("Unable to create registry key.");

            key.SetValue("URL Protocol", "");

            using var commandKey = key.CreateSubKey(@"shell\open\command")
             ?? throw new Exception("Unable to create registry sub key.");

            commandKey.SetValue("", $"\"{relayPath}\" \"%1\" \"{resPath}\" \"{pid}\" \"{hwnd}\"");

            StartCoroutine(IEListen(.5f));
        }

        private IEnumerator IEListen(float sleepTime) {
            var sleepUnit = new WaitForSecondsRealtime(sleepTime);

            while (!isStopped && !TryResponse()) yield return sleepUnit;
        }

        private void OnApplicationFocus(bool focus) {
            if (!isStopped && focus) TryResponse();
        }

        private void OnDestroy() {
            StopListen();
        }

        private bool TryResponse() {
            string res;

            try {
                res = File.ReadAllText(resPath);
            } catch { return false; }

            try {
                callback?.Invoke(res[res.IndexOf('?')..]);
            } catch (Exception e) { Debug.LogException(e); }

            StopListenAndDestroy();

            return true;
        }

        private void StopListen() {
            if (isStopped) return;
            isStopped = true;

            File.Delete(resPath);
        }

        public void StopListenAndDestroy() {
            StopListen();
            Destroy(gameObject);
        }
    }
}

#endif