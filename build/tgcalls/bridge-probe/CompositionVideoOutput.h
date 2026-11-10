#pragma once

#include "TgCallsEngineFacade.h"

#include "api/video/video_frame.h"
#include "api/video/video_sink_interface.h"

#include <d2d1_1.h>
#include <d3d11_1.h>
#include <windows.ui.composition.interop.h>
#include <wrl/client.h>

#include <atomic>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>
#include <vector>

namespace Unigram {
namespace Native {
namespace Calls {
namespace Proof {

/// <summary>
/// A native composition-surface video sink. It deliberately retains WebRTC frames and
/// renders them off the media callback; no video pixels are projected through C++/CX.
/// </summary>
class CompositionVideoOutput final
    : public rtc::VideoSinkInterface<webrtc::VideoFrame>
    , public std::enable_shared_from_this<CompositionVideoOutput> {
public:
    using FailureCallback = std::function<void(HRESULT)>;

    static std::shared_ptr<CompositionVideoOutput> Create(
        Windows::UI::Composition::SpriteVisual^ visual,
        bool mirrored,
        FailureCallback failure);

    ~CompositionVideoOutput();

    void OnFrame(const webrtc::VideoFrame& frame) override;
    void Close();

private:
    CompositionVideoOutput(
        Windows::UI::Composition::SpriteVisual^ visual,
        bool mirrored,
        FailureCallback failure);

    void InitializeCompositionSurface();
    void RenderLoop();
    HRESULT RenderFrame(const webrtc::VideoFrame& frame);
    void ReportFailure(HRESULT result);

    std::atomic<bool> _closed{false};
    std::atomic<bool> _failureReported{false};
    const bool _mirrored;
    FailureCallback _failure;

    Windows::UI::Composition::SpriteVisual^ _visual;
    Windows::UI::Composition::CompositionGraphicsDevice^ _graphicsDevice;
    Windows::UI::Composition::CompositionDrawingSurface^ _surface;
    Microsoft::WRL::ComPtr<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop> _surfaceInterop;
    Microsoft::WRL::ComPtr<ID3D11Device> _d3dDevice;
    Microsoft::WRL::ComPtr<ID2D1Device> _d2dDevice;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> _bitmap;
    D2D1_SIZE_U _bitmapSize{0, 0};
    SIZE _surfaceSize{0, 0};
    std::vector<uint8_t> _pixels;

    std::mutex _frameMutex;
    std::condition_variable _frameReady;
    std::optional<webrtc::VideoFrame> _latestFrame;
    std::thread _renderThread;
};

}
}
}
}
