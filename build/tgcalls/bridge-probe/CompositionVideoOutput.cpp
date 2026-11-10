#include "CompositionVideoOutput.h"

#include "third_party/libyuv/include/libyuv.h"

#include <d2d1_1.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.ui.composition.interop.h>
#include <wrl/client.h>

#include <array>
#include <limits>
#include <stdexcept>
#include <utility>

using namespace Microsoft::WRL;
using namespace Windows::Foundation;
using namespace Windows::Graphics::DirectX;
using namespace Windows::UI::Composition;

namespace Unigram {
namespace Native {
namespace Calls {
namespace Proof {
namespace {

void ThrowIfFailed(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        throw std::runtime_error(operation);
    }
}

bool IsValidFrameSize(int width, int height) {
    constexpr int kMaximumDimension = 4096;
    return width > 0 && height > 0 && width <= kMaximumDimension && height <= kMaximumDimension;
}

}

std::shared_ptr<CompositionVideoOutput> CompositionVideoOutput::Create(
    SpriteVisual^ visual,
    bool mirrored,
    FailureCallback failure) {
    if (visual == nullptr) {
        throw std::invalid_argument("A composition video host is required.");
    }

    auto output = std::shared_ptr<CompositionVideoOutput>(
        new CompositionVideoOutput(visual, mirrored, std::move(failure)));
    output->InitializeCompositionSurface();
    output->_renderThread = std::thread([weak = std::weak_ptr<CompositionVideoOutput>(output)] {
        if (const auto strong = weak.lock()) {
            strong->RenderLoop();
        }
    });
    return output;
}

CompositionVideoOutput::CompositionVideoOutput(
    SpriteVisual^ visual,
    bool mirrored,
    FailureCallback failure)
    : _mirrored(mirrored)
    , _failure(std::move(failure))
    , _visual(visual) {
}

CompositionVideoOutput::~CompositionVideoOutput() {
    Close();
}

void CompositionVideoOutput::InitializeCompositionSurface() {
    const std::array<D3D_FEATURE_LEVEL, 7> featureLevels = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0,
        D3D_FEATURE_LEVEL_9_3,
        D3D_FEATURE_LEVEL_9_2,
        D3D_FEATURE_LEVEL_9_1,
    };

    ThrowIfFailed(
        D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            featureLevels.data(),
            static_cast<UINT>(featureLevels.size()),
            D3D11_SDK_VERSION,
            &_d3dDevice,
            nullptr,
            nullptr),
        "The composition renderer could not create a D3D11 device.");

    ComPtr<IDXGIDevice> dxgiDevice;
    ThrowIfFailed(_d3dDevice.As(&dxgiDevice), "The composition renderer has no DXGI device.");

    const D2D1_CREATION_PROPERTIES d2dProperties = {
        D2D1_THREADING_MODE_MULTI_THREADED,
        D2D1_DEBUG_LEVEL_NONE,
        D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
    };
    ThrowIfFailed(
        D2D1CreateDevice(dxgiDevice.Get(), &d2dProperties, &_d2dDevice),
        "The composition renderer could not create a Direct2D device.");

    ComPtr<ABI::Windows::UI::Composition::ICompositorInterop> compositorInterop;
    ThrowIfFailed(
        reinterpret_cast<IInspectable*>(_visual->Compositor)->QueryInterface(
            IID_PPV_ARGS(&compositorInterop)),
        "The call compositor does not expose interop.");

    ComPtr<ABI::Windows::UI::Composition::ICompositionGraphicsDevice> graphicsDeviceInterop;
    ThrowIfFailed(
        compositorInterop->CreateGraphicsDevice(_d2dDevice.Get(), &graphicsDeviceInterop),
        "The call compositor could not create a graphics device.");

    _graphicsDevice = reinterpret_cast<CompositionGraphicsDevice^>(graphicsDeviceInterop.Get());
    _surface = _graphicsDevice->CreateDrawingSurface(
        Size(1.0f, 1.0f),
        DirectXPixelFormat::B8G8R8A8UIntNormalized,
        DirectXAlphaMode::Premultiplied);
    ThrowIfFailed(
        reinterpret_cast<IInspectable*>(_surface)->QueryInterface(IID_PPV_ARGS(&_surfaceInterop)),
        "The call composition surface does not expose drawing interop.");

    auto brush = _visual->Compositor->CreateSurfaceBrush(_surface);
    brush->Stretch = CompositionStretch::UniformToFill;
    brush->HorizontalAlignmentRatio = 0.5f;
    brush->VerticalAlignmentRatio = 0.5f;
    _visual->Brush = brush;
}

void CompositionVideoOutput::OnFrame(const webrtc::VideoFrame& frame) {
    if (_closed.load(std::memory_order_acquire)) {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(_frameMutex);
        if (_closed.load(std::memory_order_relaxed)) {
            return;
        }
        _latestFrame = frame;
    }
    _frameReady.notify_one();
}

void CompositionVideoOutput::Close() {
    if (_closed.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    _frameReady.notify_all();
    if (_renderThread.joinable() && _renderThread.get_id() != std::this_thread::get_id()) {
        _renderThread.join();
    }

    std::lock_guard<std::mutex> lock(_frameMutex);
    _latestFrame.reset();
    _bitmap.Reset();
    _pixels.clear();
    _surfaceInterop.Reset();
    _surface = nullptr;
    _graphicsDevice = nullptr;
    _d2dDevice.Reset();
    _d3dDevice.Reset();
    _visual = nullptr;
}

void CompositionVideoOutput::RenderLoop() {
    const auto apartmentResult = RoInitialize(RO_INIT_MULTITHREADED);
    if (FAILED(apartmentResult)) {
        ReportFailure(apartmentResult);
        return;
    }

    while (!_closed.load(std::memory_order_acquire)) {
        std::optional<webrtc::VideoFrame> frame;
        {
            std::unique_lock<std::mutex> lock(_frameMutex);
            _frameReady.wait(lock, [this] {
                return _closed.load(std::memory_order_acquire) || _latestFrame.has_value();
            });
            if (_closed.load(std::memory_order_relaxed)) {
                break;
            }
            frame = std::move(_latestFrame);
            _latestFrame.reset();
        }

        if (!frame.has_value()) {
            continue;
        }

        const auto result = RenderFrame(*frame);
        if (FAILED(result)) {
            ReportFailure(result);
        }
    }

    RoUninitialize();
}

HRESULT CompositionVideoOutput::RenderFrame(const webrtc::VideoFrame& frame) {
    if (_closed.load(std::memory_order_acquire) || !_surfaceInterop) {
        return RO_E_CLOSED;
    }

    const auto buffer = frame.video_frame_buffer()->ToI420();
    if (!buffer || !IsValidFrameSize(buffer->width(), buffer->height())) {
        return E_INVALIDARG;
    }

    const auto width = buffer->width();
    const auto height = buffer->height();
    const auto bytes = static_cast<size_t>(width) * static_cast<size_t>(height) * 4;
    if (bytes > static_cast<size_t>(std::numeric_limits<UINT>::max())) {
        return E_OUTOFMEMORY;
    }

    if (_pixels.size() != bytes) {
        _pixels.resize(bytes);
    }

    if (libyuv::I420ToARGB(
            buffer->DataY(),
            buffer->StrideY(),
            buffer->DataU(),
            buffer->StrideU(),
            buffer->DataV(),
            buffer->StrideV(),
            _pixels.data(),
            width * 4,
            width,
            height) != 0) {
        return E_FAIL;
    }

    SIZE finalSize{width, height};
    D2D1_MATRIX_3X2_F transform = D2D1::Matrix3x2F::Identity();
    switch (frame.rotation()) {
    case webrtc::kVideoRotation_90:
        finalSize = SIZE{height, width};
        transform = D2D1::Matrix3x2F::Rotation(
            90.0f,
            D2D1::Point2F(height / 2.0f, width / 2.0f));
        break;
    case webrtc::kVideoRotation_180:
        transform = D2D1::Matrix3x2F::Rotation(
            180.0f,
            D2D1::Point2F(width / 2.0f, height / 2.0f));
        break;
    case webrtc::kVideoRotation_270:
        finalSize = SIZE{height, width};
        transform = D2D1::Matrix3x2F::Rotation(
            270.0f,
            D2D1::Point2F(height / 2.0f, width / 2.0f));
        break;
    default:
        break;
    }

    if (finalSize.cx != _surfaceSize.cx || finalSize.cy != _surfaceSize.cy) {
        const auto resizeResult = _surfaceInterop->Resize(finalSize);
        if (FAILED(resizeResult)) {
            return resizeResult;
        }
        _surfaceSize = finalSize;
    }

    ComPtr<ID2D1DeviceContext> context;
    POINT offset{};
    auto result = _surfaceInterop->BeginDraw(nullptr, IID_PPV_ARGS(&context), &offset);
    if (FAILED(result)) {
        return result;
    }

    HRESULT drawResult = S_OK;
    if (!_bitmap || _bitmapSize.width != static_cast<UINT32>(width) || _bitmapSize.height != static_cast<UINT32>(height)) {
        const D2D1_BITMAP_PROPERTIES1 properties = {
            {DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED},
            96.0f,
            96.0f,
            D2D1_BITMAP_OPTIONS_NONE,
            0,
        };
        _bitmap.Reset();
        drawResult = context->CreateBitmap(
            D2D1::SizeU(static_cast<UINT32>(width), static_cast<UINT32>(height)),
            _pixels.data(),
            width * 4,
            &properties,
            &_bitmap);
        if (SUCCEEDED(drawResult)) {
            _bitmapSize = D2D1::SizeU(static_cast<UINT32>(width), static_cast<UINT32>(height));
        }
    } else {
        drawResult = _bitmap->CopyFromMemory(nullptr, _pixels.data(), width * 4);
    }

    if (SUCCEEDED(drawResult)) {
        const auto x = (finalSize.cx - width) / 2.0f;
        const auto y = (finalSize.cy - height) / 2.0f;
        if (_mirrored) {
            transform = transform * D2D1::Matrix3x2F::Scale(
                -1.0f,
                1.0f,
                D2D1::Point2F(finalSize.cx / 2.0f, finalSize.cy / 2.0f));
        }
        transform._31 += static_cast<float>(offset.x);
        transform._32 += static_cast<float>(offset.y);
        context->Clear();
        context->SetTransform(transform);
        context->DrawBitmap(
            _bitmap.Get(),
            D2D1::RectF(x, y, x + width, y + height),
            1.0f,
            D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    }

    const auto endResult = _surfaceInterop->EndDraw();
    return FAILED(drawResult) ? drawResult : endResult;
}

void CompositionVideoOutput::ReportFailure(HRESULT result) {
    if (!_failureReported.exchange(true, std::memory_order_acq_rel) && _failure) {
        try {
            _failure(result);
        } catch (...) {
        }
    }
}

}
}
}
}
