#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace rtc {
template <typename Frame>
class VideoSinkInterface;
}

namespace webrtc {
class VideoFrame;
}

namespace Unigram {
namespace Native {
namespace Calls {

using VideoOutputPtr = std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>>;

enum class CallState {
    WaitInit,
    WaitInitAck,
    Established,
    Failed,
    Reconnecting,
};

enum class NetworkType {
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

struct EndpointConfiguration {
    int64_t id = 0;
    std::wstring ipv4;
    std::wstring ipv6;
    uint16_t port = 0;
    bool isTcp = false;
    std::vector<uint8_t> peerTag;
};

struct RtcServerConfiguration {
    uint8_t id = 0;
    std::wstring host;
    uint16_t port = 0;
    std::wstring username;
    std::wstring password;
    bool isTurn = false;
    bool isTcp = false;
};

struct CallConfiguration {
    std::wstring version;
    double initializationTimeout = 0.;
    double receiveTimeout = 0.;
    bool enableP2P = false;
    bool allowTcp = false;
    int maxApiLayer = 0;
    bool isOutgoing = false;
    bool isVideo = false;
    std::wstring cameraDeviceId;
    bool cameraIsFront = true;
    NetworkType initialNetworkType = NetworkType::Unknown;
    std::vector<uint8_t> encryptionKey;
    std::vector<EndpointConfiguration> endpoints;
    std::vector<RtcServerConfiguration> rtcServers;
};

class CallSession;
using CallSessionPtr = std::shared_ptr<CallSession>;

enum class RemoteAudioState {
    Muted,
    Active,
};

enum class VideoState {
    Inactive,
    Paused,
    Active,
};

struct CallCallbacks {
    std::function<void(CallState)> stateChanged;
    std::function<void(std::vector<uint8_t>)> signalingData;
    std::function<void(bool)> stopped;
    std::function<void(int)> signalBarsChanged;
    std::function<void(float)> audioLevelChanged;
    std::function<void(RemoteAudioState)> remoteAudioStateChanged;
    std::function<void(VideoState)> remoteVideoStateChanged;
    std::function<void()> videoCaptureFailed;
    std::function<void(bool)> videoCaptureSwitchCompleted;
    // Reports the outcome of creating the platform audio device module as a short
    // diagnostic string built only from fixed keys, result codes and device counts.
    // It never carries a device name, identifier or path.
    std::function<void(std::string)> audioDeviceReport;
};

const wchar_t* GetFirstSupportedVersion();
std::vector<std::wstring> GetSupportedVersions();
/// <summary>
/// Installs a process-wide handler that appends the code of a fatal hardware exception to
/// the given diagnostics file. The per-call guards around the audio device only cover the
/// thread that calls into it, but the platform completes capture activation on a pool
/// thread, so a fault there kills the process with nothing recorded. The handler writes
/// with raw file APIs and no allocation or locking because it runs in a fault context, and
/// it records only an exception code -- never an address, a module or a path.
/// </summary>
void EnableCrashDiagnostics(const std::wstring& diagnosticsFilePath);
/// <summary>
/// Returns the live state of the platform audio device module as a short diagnostic
/// string built only from fixed keys and boolean or count values. Whether the module is
/// actually recording and playing is the one unambiguous answer to "the transport is
/// established but nobody can hear anything"; the engine's own level callback reports the
/// larger of the local and remote levels and so cannot distinguish the two directions.
/// </summary>
std::string GetAudioDeviceStatus(const CallSessionPtr& session);
CallSessionPtr CreateCallSession(
    const CallConfiguration& configuration,
    CallCallbacks callbacks);
void StartCallSession(const CallSessionPtr& session);
void ReceiveSignalingData(const CallSessionPtr& session, std::vector<uint8_t> data);
void SetMuted(const CallSessionPtr& session, bool value);
bool SupportsVideo(const CallSessionPtr& session);
void SetVideoState(const CallSessionPtr& session, VideoState state);
void SwitchVideoCaptureDevice(
    const CallSessionPtr& session,
    const std::wstring& deviceId,
    bool isFrontCamera);
/// <summary>
/// Attaches a native video sink directly to TgCalls. The bridge remains the strong owner
/// of the sink; TgCalls keeps only weak references and receives nullptr on teardown.
/// </summary>
void SetVideoOutput(const CallSessionPtr& session, bool local, VideoOutputPtr output);
/// <summary>
/// Queues the selected physical output endpoint through TgCalls' own media-device path.
/// Returns a fixed diagnostic token describing whether an earpiece/speaker index was
/// available when the platform audio device module was created.
/// </summary>
std::string SetAudioOutputEndpoint(const CallSessionPtr& session, bool speakerphone);
void SetNetworkType(const CallSessionPtr& session, NetworkType value);
void StopCallSession(const CallSessionPtr& session);
/// <summary>
/// Stops the session and waits until tgcalls has destroyed the audio device, returning a
/// fixed outcome token: <c>drained</c>, <c>timeout</c>, <c>reentrant</c> when called from
/// the stop completion itself, or <c>inactive</c> when the session never started. The
/// capture endpoint has a single owner on Windows 10 Mobile, so the next call must not
/// activate it until this reports <c>drained</c>.
/// </summary>
std::string StopCallSessionAndWait(const CallSessionPtr& session, int timeoutMilliseconds);

}
}
}
