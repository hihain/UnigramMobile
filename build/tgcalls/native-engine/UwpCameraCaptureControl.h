#pragma once

#include <string>

namespace Unigram {
namespace Native {
namespace Calls {

void SetCameraDeviceOrientationHint(const std::string& deviceSelector, bool isFrontCamera);
bool IsFrontCameraDevice(const std::string& deviceSelector);
void SetCameraCaptureInflightStage(const char* stage);
void ClearCameraCaptureInflightStage();

class UwpCameraCaptureControl {
public:
    virtual ~UwpCameraCaptureControl() = default;

    virtual bool SwitchToDevice(std::string deviceSelector, bool isFrontCamera) = 0;
};

}
}
}
