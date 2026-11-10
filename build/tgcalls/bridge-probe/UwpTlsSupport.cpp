namespace Unigram {
namespace Native {
namespace Calls {
namespace Proof {
void EnsureUwpTlsSupport() {
    // Avoid explicit static TLS in a dynamically activated WinRT component.
    // BoringSSL manages its own per-thread data through the Windows TLS APIs.
}
}
}
}
}
