#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN

using System.Threading.Tasks;

namespace Google.Impl {
    internal class DeepLinkWindowsListener : IListener {
        private readonly TaskCompletionSource<string> tcs;
        private readonly DeepLinkWindows deepLink;
        
        public string RedirectUri { get; }

        public DeepLinkWindowsListener(string appUri) {
            tcs = new TaskCompletionSource<string>();
            deepLink = DeepLinkWindows.StartListen(appUri, res => tcs.SetResult(res), dontDestroyOnLoad: false);
            RedirectUri = appUri + ":";
        }

        public Task<string> ListenAsync() {
            return tcs.Task;
        }

        public void OnGetCodeFailed() { }
        
        public void OnGetCodeSuccess() { }

        public void Dispose() {
            deepLink.StopListenAndDestroy();
        }
    }
}

#endif