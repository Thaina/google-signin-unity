using System;
using System.Threading.Tasks;

namespace Google.Impl {
    internal interface IListener : IDisposable {
        string RedirectUri { get; }

        Task<string> ListenAsync();
        void OnGetCodeFailed();
        void OnGetCodeSuccess();
    }
}