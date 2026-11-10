#pragma once

#include <collection.h>
#include <memory>

namespace Unigram {
namespace Native {
namespace Calls {
namespace Proof {

public enum class NetworkType {
    Unknown,
    Gprs,
    Edge,
    ThirdGeneration,
    Hspa,
    Lte,
    WiFi,
    Ethernet,
    OtherHighSpeed,
    OtherLowSpeed,
    OtherMobile,
    Dialup,
};

public enum class CallState {
    WaitInit,
    WaitInitAck,
    Established,
    Failed,
    Reconnecting,
};

public enum class RemoteAudioState {
    Muted,
    Active,
};

public enum class VideoState {
    Inactive,
    Paused,
    Active,
};

public ref class ReflectorEndpoint sealed {
public:
    ReflectorEndpoint();

    property long long Id;
    property Platform::String^ Ipv4Address;
    property Platform::String^ Ipv6Address;
    property unsigned short Port;
    property bool IsTcp;
    property Windows::Foundation::Collections::IVector<unsigned char>^ PeerTag;
};

public ref class RtcServer sealed {
public:
    RtcServer();

    property unsigned char Id;
    property Platform::String^ Host;
    property unsigned short Port;
    property Platform::String^ Username;
    property Platform::String^ Password;
    property bool IsTurn;
    property bool IsTcp;
};

public ref class AudioCallConfiguration sealed {
public:
    AudioCallConfiguration();

    property Platform::String^ Version;
    property double InitializationTimeout;
    property double ReceiveTimeout;
    property bool EnableP2P;
    property bool AllowTcp;
    property int MaxApiLayer;
    property bool IsOutgoing;
    property bool IsVideo;
    property Platform::String^ CameraDeviceId;
    property bool CameraIsFront;
    property NetworkType InitialNetworkType;
    property Windows::Foundation::Collections::IVector<unsigned char>^ EncryptionKey;
    property Windows::Foundation::Collections::IVector<ReflectorEndpoint^>^ ReflectorEndpoints;
    property Windows::Foundation::Collections::IVector<RtcServer^>^ RtcServers;
};

public ref class AudioCallSession sealed {
public:
    static AudioCallSession^ Create(AudioCallConfiguration^ configuration);

    virtual ~AudioCallSession();

    void Start();
    void ReceiveSignalingData(Windows::Foundation::Collections::IVector<unsigned char>^ data);
    void SetMuted(bool value);
    bool SupportsVideo();
    void SetVideoState(VideoState value);
    void SwitchVideoCaptureDevice(Platform::String^ deviceId, bool isFrontCamera);
    void SetVideoOutput(
        bool local,
        Platform::Object^ visual,
        bool mirrored);
    void ClearVideoOutput(bool local);
    Platform::String^ SetAudioOutputEndpoint(bool speakerphone);
    void SetNetworkType(NetworkType value);
    Platform::String^ GetAudioDeviceStatus();
    void Stop();

    /// <summary>
    /// Stops the native session and blocks until tgcalls has destroyed the audio device,
    /// returning a fixed outcome token: "drained", "timeout", "reentrant" when called
    /// from the stopped callback itself, "faulted" when teardown threw, or "empty" when
    /// there was no native session. Call this before Dispose: the capture endpoint has a
    /// single owner on Windows 10 Mobile, so the next call must not start until this
    /// reports "drained".
    /// </summary>
    Platform::String^ Teardown();

    event Windows::Foundation::EventHandler<CallState>^ StateChanged;
    event Windows::Foundation::EventHandler<Windows::Foundation::Collections::IVector<unsigned char>^>^ SignalingData;
    event Windows::Foundation::EventHandler<bool>^ Stopped;
    event Windows::Foundation::EventHandler<int>^ SignalBarsChanged;
    event Windows::Foundation::EventHandler<float>^ AudioLevelChanged;
    event Windows::Foundation::EventHandler<RemoteAudioState>^ RemoteAudioStateChanged;
    event Windows::Foundation::EventHandler<VideoState>^ RemoteVideoStateChanged;
    event Windows::Foundation::EventHandler<Platform::Object^>^ VideoCaptureFailed;
    event Windows::Foundation::EventHandler<bool>^ VideoCaptureSwitchCompleted;
    event Windows::Foundation::EventHandler<int>^ VideoOutputFailed;
    event Windows::Foundation::EventHandler<Platform::String^>^ AudioDeviceReport;

private:
    AudioCallSession(AudioCallConfiguration^ configuration);
    Platform::String^ DrainSession();
    void ReportVideoCaptureSwitchResult(bool succeeded);
    void ReportVideoOutputFailure(int result);
    void* _holder;
};

public ref class Diagnostics sealed {
public:
    static Platform::String^ GetBuildInfo();
    static Windows::Foundation::Collections::IVector<Platform::String^>^ GetSupportedVersions();
    static void EnableCrashDiagnostics(Platform::String^ diagnosticsFilePath);
};

}
}
}
}
