#include "platform/PlatformInterface.h"
#include "Instance.h"
#include "VideoCaptureInterface.h"
#include "VideoCapturerInterface.h"
#include "UwpCameraCaptureControl.h"

#include "api/media_stream_interface.h"
#include "api/video/i420_buffer.h"
#include "api/video/video_frame.h"
#include "api/video/video_sink_interface.h"
#include "api/video_codecs/sdp_video_format.h"
#include "api/video_codecs/video_decoder_factory.h"
#include "api/video_codecs/video_encoder_factory.h"
#include "media/base/video_broadcaster.h"
#include "modules/video_capture/video_capture_factory.h"
#include "modules/video_coding/codecs/h264/win/decoder/h264_decoder_mf_impl.h"
#include "modules/video_coding/codecs/h264/win/encoder/h264_encoder_mf_impl.h"
#include "pc/video_track_source.h"
#include "pc/video_track_source_proxy.h"
#include "rtc_base/logging.h"
#include "rtc_base/ref_counted_object.h"

#include <cmath>
#include <atomic>
#include <memory>
#include <limits>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>

#include <windows.h>

namespace Unigram {
namespace Native {
namespace Calls {
namespace {

std::mutex g_cameraOrientationHintsMutex;
std::unordered_map<std::string, bool> g_cameraOrientationHints;

}

void SetCameraDeviceOrientationHint(
        const std::string& deviceSelector,
        bool isFrontCamera) {
    if (deviceSelector.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(g_cameraOrientationHintsMutex);
    g_cameraOrientationHints[deviceSelector] = isFrontCamera;
}

bool IsFrontCameraDevice(const std::string& deviceSelector) {
    std::lock_guard<std::mutex> lock(g_cameraOrientationHintsMutex);
    const auto hint = g_cameraOrientationHints.find(deviceSelector);
    return hint == g_cameraOrientationHints.end() || hint->second;
}

}
}
}

namespace tgcalls {
namespace {

constexpr int kPreferredWidth = 1280;
constexpr int kPreferredHeight = 720;
constexpr int kPreferredFps = 30;
// Mobile camera buffers lack orientation metadata. The front and rear sensors are
// mounted in opposite directions, so their portrait corrections differ by 180 degrees.
constexpr auto kFrontPortraitRotation = webrtc::kVideoRotation_270;
constexpr auto kBackPortraitRotation = webrtc::kVideoRotation_90;

webrtc::VideoRotation RotationForDevice(const std::string& deviceId) {
    return Unigram::Native::Calls::IsFrontCameraDevice(deviceId)
        ? kFrontPortraitRotation
        : kBackPortraitRotation;
}

class CameraCaptureOperationScope {
public:
    void SetStage(const char* stage) {
        Unigram::Native::Calls::SetCameraCaptureInflightStage(stage);
    }

    ~CameraCaptureOperationScope() {
        Unigram::Native::Calls::ClearCameraCaptureInflightStage();
    }
};

bool IsH264(const webrtc::SdpVideoFormat& format) {
    return format.name == "H264";
}

bool SetFallbackDeviceId(
        webrtc::VideoCaptureCapability* capability,
        const std::string& deviceId) {
    if (deviceId.empty() ||
        deviceId.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
        return false;
    }

    const auto length = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        deviceId.data(),
        static_cast<int>(deviceId.size()),
        nullptr,
        0);
    if (length <= 0) {
        return false;
    }

    std::wstring wideId(static_cast<size_t>(length), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            deviceId.data(),
            static_cast<int>(deviceId.size()),
            wideId.data(),
            length) != length) {
        return false;
    }

    capability->profile_id = std::move(wideId);
    capability->media_capture_video_profile.Reset();
    capability->record_media_description.Reset();
    return true;
}

std::vector<webrtc::SdpVideoFormat> H264Formats() {
    return {
        webrtc::SdpVideoFormat("H264", {
            { "level-asymmetry-allowed", "1" },
            { "packetization-mode", "1" },
            { "profile-level-id", "42e01f" },
        }),
    };
}

class H264OnlyEncoderFactory final : public webrtc::VideoEncoderFactory {
public:
    std::vector<webrtc::SdpVideoFormat> GetSupportedFormats() const override {
        return H264Formats();
    }

    std::unique_ptr<webrtc::VideoEncoder> CreateVideoEncoder(
            const webrtc::SdpVideoFormat& format) override {
        return IsH264(format)
            ? std::make_unique<webrtc::H264EncoderMFImpl>()
            : nullptr;
    }
};

class H264OnlyDecoderFactory final : public webrtc::VideoDecoderFactory {
public:
    std::vector<webrtc::SdpVideoFormat> GetSupportedFormats() const override {
        return H264Formats();
    }

    std::unique_ptr<webrtc::VideoDecoder> Create(
            const webrtc::Environment&,
            const webrtc::SdpVideoFormat& format) override {
        return CreateVideoDecoder(format);
    }

    std::unique_ptr<webrtc::VideoDecoder> CreateVideoDecoder(
            const webrtc::SdpVideoFormat& format) override {
        return IsH264(format)
            ? std::make_unique<webrtc::H264DecoderMFImpl>()
            : nullptr;
    }
};

class UwpCameraTrackSource : public webrtc::VideoTrackSource {
public:
    UwpCameraTrackSource()
        : VideoTrackSource(/*remote=*/false)
        , _broadcaster(std::make_shared<rtc::VideoBroadcaster>()) {
    }

    std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> sink() const {
        return _broadcaster;
    }

private:
    rtc::VideoSourceInterface<webrtc::VideoFrame>* source() override {
        return _broadcaster.get();
    }

    std::shared_ptr<rtc::VideoBroadcaster> _broadcaster;
};

std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> GetSourceSink(
        const rtc::scoped_refptr<webrtc::VideoTrackSourceInterface>& source) {
    const auto proxy = static_cast<webrtc::VideoTrackSourceProxy*>(source.get());
    const auto internal = static_cast<UwpCameraTrackSource*>(proxy->internal());
    return internal->sink();
}

class UwpCameraCapturer final : public rtc::VideoSinkInterface<webrtc::VideoFrame> {
public:
    explicit UwpCameraCapturer(std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> sink)
        : _sink(std::move(sink)) {
    }

    ~UwpCameraCapturer() override {
        Stop();
    }

    void SetState(VideoState state) {
        if (_state.load(std::memory_order_acquire) == state) {
            return;
        }

        _state.store(state, std::memory_order_release);
        if (state == VideoState::Active) {
            Start();
        } else {
            Stop();
        }
    }

    bool SetDeviceId(std::string deviceId) {
        const auto rotation = static_cast<int>(RotationForDevice(deviceId));
        if (_requestedDeviceId == deviceId) {
            _rotation.store(rotation, std::memory_order_release);
            return _state.load(std::memory_order_acquire) != VideoState::Active || _module != nullptr;
        }

        CameraCaptureOperationScope operation;

        // A WinRT reader that has not finished stopping must not be followed immediately
        // by another MediaCapture initialization. On Windows 10 Mobile that can leave
        // two camera lifecycles overlapping and terminate the app.
        operation.SetStage("stop");
        if (!Stop()) {
            return false;
        }

        _requestedDeviceId = std::move(deviceId);
        _rotation.store(rotation, std::memory_order_release);
        if (_state.load(std::memory_order_acquire) == VideoState::Active) {
            operation.SetStage("start");
            return Start();
        }

        return true;
    }

    void SetPreferredCaptureAspectRatio(float aspectRatio) {
        _aspectRatio.store(aspectRatio, std::memory_order_release);
    }

    void SetOnFatalError(std::function<void()> error) {
        _error = std::move(error);
        if (_failed && _error) {
            _error();
        }
    }

    std::pair<int, int> resolution() const {
        return _dimensions;
    }

    int rotation() const {
        return _rotation.load(std::memory_order_acquire);
    }

    void OnFrame(const webrtc::VideoFrame& frame) override {
        if (_state.load(std::memory_order_acquire) != VideoState::Active || _sink == nullptr) {
            return;
        }

        const auto aspectRatio = _aspectRatio.load(std::memory_order_acquire);
        if (aspectRatio <= 0.001f) {
            ForwardPortraitFrame(frame);
            return;
        }

        const auto originalWidth = frame.width();
        const auto originalHeight = frame.height();
        auto width = originalWidth > aspectRatio * originalHeight
            ? static_cast<int>(aspectRatio * originalHeight)
            : originalWidth;
        auto height = originalWidth > aspectRatio * originalHeight
            ? originalHeight
            : static_cast<int>(originalWidth / aspectRatio);

        if ((width >= originalWidth && height >= originalHeight) || width <= 0 || height <= 0) {
            ForwardPortraitFrame(frame);
            return;
        }

        width &= ~1;
        height &= ~1;
        const auto left = (originalWidth - width) / 2;
        const auto top = (originalHeight - height) / 2;
        auto buffer = webrtc::I420Buffer::Create(width, height);
        buffer->CropAndScaleFrom(
            *frame.video_frame_buffer()->ToI420(), left, top, width, height);

        _sink->OnFrame(
            webrtc::VideoFrame::Builder()
                .set_video_frame_buffer(buffer)
                .set_rotation(static_cast<webrtc::VideoRotation>(rotation()))
                .set_timestamp_us(frame.timestamp_us())
                .set_id(frame.id())
                .build());
    }

private:
    void ForwardPortraitFrame(const webrtc::VideoFrame& frame) {
        auto builder = webrtc::VideoFrame::Builder()
            .set_video_frame_buffer(frame.video_frame_buffer())
            .set_rotation(static_cast<webrtc::VideoRotation>(rotation()))
            .set_timestamp_us(frame.timestamp_us())
            .set_id(frame.id());
        if (frame.has_update_rect()) {
            builder.set_update_rect(frame.update_rect());
        }
        _sink->OnFrame(builder.build());
    }

    bool Start() {
        _failed = false;
        const auto info = std::unique_ptr<webrtc::VideoCaptureModule::DeviceInfo>(
            webrtc::VideoCaptureFactory::CreateDeviceInfo());
        if (!info) {
            Fail();
            return false;
        }

        const auto count = info->NumberOfDevices();
        if (count <= 0) {
            Fail();
            return false;
        }

        // DeviceInformation.Id is the device identifier expected by WebRTC's WinRT
        // backend. Re-enumerating and matching it here can select a different sensor
        // when the collection changes between calls, so preserve it verbatim.
        if (!_requestedDeviceId.empty() && _requestedDeviceId != "default") {
            if (Start(info.get(), _requestedDeviceId)) {
                return true;
            }

            Fail();
            return false;
        }

        for (auto index = 0; index != count; ++index) {
            char name[webrtc::kVideoCaptureDeviceNameLength] = {};
            char id[webrtc::kVideoCaptureUniqueNameLength] = {};
            if (info->GetDeviceName(
                    index,
                    name,
                    sizeof(name),
                    id,
                    sizeof(id)) == 0 &&
                Start(info.get(), id)) {
                return true;
            }
        }

        Fail();
        return false;
    }

    bool Start(webrtc::VideoCaptureModule::DeviceInfo* info, const std::string& deviceId) {
        if (deviceId.empty()) {
            return false;
        }

        _module = webrtc::VideoCaptureFactory::Create(deviceId.c_str());
        if (!_module) {
            RTC_LOG(LS_ERROR) << "Failed to create UWP camera capture.";
            return false;
        }

        _module->RegisterCaptureDataCallback(this);
        auto requested = webrtc::VideoCaptureCapability();
        requested.videoType = webrtc::VideoType::kI420;
        requested.width = kPreferredWidth;
        requested.height = kPreferredHeight;
        requested.maxFPS = kPreferredFps;
        _capability = webrtc::VideoCaptureCapability();
        const auto capabilityIndex = info->GetBestMatchedCapability(
            _module->CurrentDeviceName(),
            requested,
            _capability);
        if (capabilityIndex < 0 ||
            _capability.width == 0 ||
            _capability.height == 0 ||
            _capability.maxFPS == 0) {
            _capability.width = kPreferredWidth;
            _capability.height = kPreferredHeight;
            _capability.maxFPS = kPreferredFps;
            _capability.videoType = webrtc::VideoType::kI420;
        }

        if (_capability.profile_id.empty() && !SetFallbackDeviceId(&_capability, deviceId)) {
            RTC_LOG(LS_ERROR) << "Failed to preserve the UWP camera device identifier.";
            _module->DeRegisterCaptureDataCallback();
            _module = nullptr;
            return false;
        }

        if (_module->StartCapture(_capability) != 0) {
            RTC_LOG(LS_ERROR) << "Failed to start UWP camera capture.";
            Stop();
            return false;
        }

        _dimensions = { _capability.width, _capability.height };
        return true;
    }

    bool Stop() {
        _failed = false;
        if (!_module) {
            return true;
        }

        const auto stopped = _module->StopCapture() == 0;
        _module->DeRegisterCaptureDataCallback();
        _module = nullptr;
        return stopped;
    }

    void Fail() {
        _failed = true;
        if (_error) {
            _error();
        }
    }

    std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> _sink;
    rtc::scoped_refptr<webrtc::VideoCaptureModule> _module;
    webrtc::VideoCaptureCapability _capability;
    std::atomic<VideoState> _state{VideoState::Inactive};
    std::string _requestedDeviceId;
    std::pair<int, int> _dimensions{ kPreferredWidth, kPreferredHeight };
    std::function<void()> _error;
    std::atomic<float> _aspectRatio{0.0f};
    std::atomic<int> _rotation{static_cast<int>(kFrontPortraitRotation)};
    bool _failed = false;
};

class UwpCameraCapturerInterface final
    : public VideoCapturerInterface
    , public Unigram::Native::Calls::UwpCameraCaptureControl {
public:
    UwpCameraCapturerInterface(
            rtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source,
            std::string deviceId,
            std::function<void(VideoState)> stateUpdated,
            std::pair<int, int>& outResolution)
        : _source(std::move(source))
        , _sink(GetSourceSink(_source))
        , _stateUpdated(std::move(stateUpdated)) {
        _capturer = std::make_unique<UwpCameraCapturer>(_sink);
        _capturer->SetDeviceId(std::move(deviceId));
        outResolution = _capturer->resolution();
    }

    void setState(VideoState state) override {
        _capturer->SetState(state);
        if (_stateUpdated) {
            _stateUpdated(state);
        }
    }

    void setPreferredCaptureAspectRatio(float aspectRatio) override {
        _capturer->SetPreferredCaptureAspectRatio(aspectRatio);
    }

    void setUncroppedOutput(
            std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> sink) override {
        if (_uncroppedSink) {
            _source->RemoveSink(_uncroppedSink.get());
        }
        _uncroppedSink = std::move(sink);
        if (_uncroppedSink) {
            _source->AddOrUpdateSink(_uncroppedSink.get(), rtc::VideoSinkWants());
        }
    }

    int getRotation() override {
        return _capturer->rotation();
    }

    void setOnFatalError(std::function<void()> error) override {
        _capturer->SetOnFatalError(std::move(error));
    }

    bool SwitchToDevice(std::string deviceSelector, bool isFrontCamera) override {
        Unigram::Native::Calls::SetCameraDeviceOrientationHint(
            deviceSelector,
            isFrontCamera);
        return _capturer->SetDeviceId(std::move(deviceSelector));
    }

    void withNativeImplementation(std::function<void(void*)> completion) override {
        completion(static_cast<Unigram::Native::Calls::UwpCameraCaptureControl*>(this));
    }

private:
    rtc::scoped_refptr<webrtc::VideoTrackSourceInterface> _source;
    std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> _sink;
    std::unique_ptr<UwpCameraCapturer> _capturer;
    std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> _uncroppedSink;
    std::function<void(VideoState)> _stateUpdated;
};

class UwpCameraPlatform final : public PlatformInterface {
public:
    std::unique_ptr<webrtc::VideoEncoderFactory> makeVideoEncoderFactory(
            bool,
            bool) override {
        return std::make_unique<H264OnlyEncoderFactory>();
    }

    std::unique_ptr<webrtc::VideoDecoderFactory> makeVideoDecoderFactory() override {
        return std::make_unique<H264OnlyDecoderFactory>();
    }

    bool supportsEncoding(const std::string& codecName) override {
        return codecName == cricket::kH264CodecName;
    }

    rtc::scoped_refptr<webrtc::VideoTrackSourceInterface> makeVideoSource(
            rtc::Thread* signalingThread,
            rtc::Thread* workerThread) override {
        const auto source = rtc::scoped_refptr<UwpCameraTrackSource>(
            new rtc::RefCountedObject<UwpCameraTrackSource>());
        return source
            ? webrtc::VideoTrackSourceProxy::Create(signalingThread, workerThread, source)
            : nullptr;
    }

    void adaptVideoSource(
            rtc::scoped_refptr<webrtc::VideoTrackSourceInterface>,
            int,
            int,
            int) override {
    }

    std::unique_ptr<VideoCapturerInterface> makeVideoCapturer(
            rtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source,
            std::string deviceId,
            std::function<void(VideoState)> stateUpdated,
            std::function<void(PlatformCaptureInfo)>,
            std::shared_ptr<PlatformContext>,
            std::pair<int, int>& outResolution) override {
        return std::make_unique<UwpCameraCapturerInterface>(
            std::move(source),
            std::move(deviceId),
            std::move(stateUpdated),
            outResolution);
    }
};

}

std::unique_ptr<PlatformInterface> CreatePlatformInterface() {
    return std::make_unique<UwpCameraPlatform>();
}

}
