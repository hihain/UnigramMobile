#include "TgCallsEngineFacade.h"
#include "UwpCameraCaptureControl.h"

#include "Instance.h"
#include "InstanceImpl.h"
#include "StaticThreads.h"
#include "VideoCaptureInterface.h"
#include "v2/InstanceV2Impl.h"
#include "v2/InstanceV2ReferenceImpl.h"

#include "api/task_queue/task_queue_factory.h"
#include "api/video/video_frame.h"
#include "modules/audio_device/include/audio_device.h"
#include "platform/PlatformInterface.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <functional>
#include <mutex>
#include <string>
#include <stdexcept>
#include <utility>

#include <windows.h>
#include <stdlib.h>

/// <summary>
/// Receive-path packet counters defined in tgcalls' MediaManager. They answer the one
/// question the playout buffer cannot: a silent call looks identical whether no media
/// ever arrives, it arrives malformed, or it parses but no receive stream claims it.
/// Counts only; nothing derived from packet contents crosses this boundary.
///
/// Declared at global scope on purpose. Reopening `namespace tgcalls` from inside the
/// facade's own namespaces would declare a nested namespace that shadows the real one.
/// </summary>
namespace tgcalls {
    extern std::atomic<uint32_t> g_diagIncomingAudioRtp;
    extern std::atomic<uint32_t> g_diagIncomingAudioRtcp;
    extern std::atomic<uint32_t> g_diagIncomingAudioParseFailed;
    extern std::atomic<uint32_t> g_diagIncomingAudioUndemuxed;
    extern std::atomic<uint32_t> g_diagIncomingAudioRtcpSenderReport;
    extern std::atomic<uint32_t> g_diagIncomingAudioRtcpFeedback;
    extern std::atomic<uint32_t> g_diagIncomingAudioMaxBytes;
    extern std::atomic<uint32_t> g_diagOutgoingAudioRtp;
    extern std::atomic<uint32_t> g_diagOutgoingAudioRtcp;
    extern std::atomic<int> g_diagAecEnabled;
    extern std::atomic<int> g_diagAecMobileMode;
    extern std::atomic<int> g_diagNoiseSuppressionEnabled;
    extern std::atomic<int> g_diagGainControlEnabled;
    extern std::atomic<int> g_diagAecResidualLikelihoodPermille;
    extern std::atomic<int> g_diagAecResidualLikelihoodRecentMaxPermille;
    extern std::atomic<int> g_diagAecEchoReturnLossDecibelTenths;
    extern std::atomic<int> g_diagAecEchoReturnLossEnhancementDecibelTenths;
    extern std::atomic<int> g_diagAecDelayMilliseconds;
}

namespace Unigram {
namespace Native {
namespace Calls {
namespace {

extern "C" {
PVOID WINAPI AddVectoredExceptionHandler(ULONG First, PVECTORED_EXCEPTION_HANDLER Handler);
HMODULE WINAPI GetModuleHandleW(LPCWSTR lpModuleName);
extern volatile LONG g_unigram_webrtc_capture_init_stage;
extern volatile LONG g_unigram_webrtc_capture_init_hresult;
}

// Supplied by the linker at the base of the module this code is linked into, which lets
// a fault address be classified and turned into a map-file offset without calling any
// module API from a fault context.
extern "C" IMAGE_DOS_HEADER __ImageBase;

// A fault handler cannot allocate, take a lock or call into the CRT, so the diagnostics
// path is captured once up front into a fixed buffer and the report is assembled by hand.
// It writes to its own file rather than the managed diagnostics log, because the managed
// writer tracks its own end-of-file offset and would write back over an appended record.
wchar_t g_crashDiagnosticsPath[MAX_PATH] = {};
constexpr LONG kFaultBudgetPerCall = 24;
volatile LONG g_crashDiagnosticsBudget = kFaultBudgetPerCall;
volatile LONG g_budgetExhaustedReported = 0;
// Only faults raised while the audio device module is being driven are recorded. A
// vectored handler sees every first-chance exception in the process, and .NET Native
// raises an access violation for each ordinary null dereference, so an ungated handler
// would spend its budget on benign exceptions long before the fatal one arrives.
volatile LONG g_audioDeviceLifecycleDepth = 0;
// Recorded so a report can say whether the fault landed on the thread that is driving
// the audio device module, or on a pool thread the structured exception guard cannot see.
volatile LONG g_audioDeviceLifecycleThread = 0;
const char* volatile g_audioDeviceLifecycleStep = nullptr;
// Armed for the whole duration of a call rather than just the audio device steps. The
// narrower gate above was why the fatal fault stayed invisible: the call engine runs its
// media on threads of its own, and those outlive the device calls the main thread makes,
// so a death between two steps produced no record at all. Benign managed access
// violations still land here, which is what the fault budget is for.
volatile LONG g_callActiveDepth = 0;
PVOID g_crashDiagnosticsHandle = nullptr;
INIT_ONCE g_crashDiagnosticsOnce = INIT_ONCE_STATIC_INIT;

// A vectored handler only sees failures that travel as structured exceptions. The runtime
// ends the process with a fail-fast when a managed exception escapes a reverse call, and a
// fail-fast bypasses every handler, so a death there would leave no record at all. The
// marker below closes that gap without relying on any handler running: it is a file-backed
// section holding the step currently in flight, so whatever ends the process, the memory
// manager still writes the last value back to disk and the next launch can read it.
// A mapped write costs no system call, which keeps the per-step cost low enough not to
// perturb the timing of the fault under investigation.
constexpr SIZE_T kInflightMarkerSize = 64;
// A second slot in the same section holds the most recent fault seen during a call. The
// whole-call reporting window admits the benign managed access violations this app raises
// on every call, so writing each one to the log would spend the budget long before the
// fatal fault arrives and would run blocking file I/O on WebRTC's real-time threads. A
// fixed slot has neither problem: each fault overwrites the last, costs no system call,
// and the value that survives the process is by construction the last fault before death.
constexpr SIZE_T kFaultSlotSize = 192;
constexpr SIZE_T kMarkerSectionSize = kInflightMarkerSize + kFaultSlotSize;
wchar_t g_inflightMarkerPath[MAX_PATH] = {};
HANDLE g_inflightMarkerFile = INVALID_HANDLE_VALUE;
HANDLE g_inflightMarkerMapping = nullptr;
volatile char* g_inflightMarker = nullptr;
volatile char* g_faultSlot = nullptr;
volatile LONG g_faultSlotWriter = 0;
char g_inflightRecovered[kInflightMarkerSize] = {};
char g_faultRecovered[kFaultSlotSize] = {};
volatile LONG g_inflightThread = 0;

// Captured once, away from any fault context, so classifying a fault address is pure
// arithmetic. An offset within a module names the faulting code without revealing
// anything about the install.
uintptr_t g_appModuleBase = 0;
uintptr_t g_appModuleSize = 0;

void SetInflightStep(const char* step, const char* stage) {
    volatile char* marker = g_inflightMarker;
    if (marker == nullptr) {
        return;
    }

    char buffer[kInflightMarkerSize] = {};
    SIZE_T length = 0;
    while (*step != '\0' && length < kInflightMarkerSize - 2) {
        buffer[length++] = *step++;
    }
    buffer[length++] = ':';
    while (*stage != '\0' && length < kInflightMarkerSize - 1) {
        buffer[length++] = *stage++;
    }

    InterlockedExchange(&g_inflightThread, static_cast<LONG>(GetCurrentThreadId()));
    for (SIZE_T index = 0; index < kInflightMarkerSize; ++index) {
        marker[index] = buffer[index];
    }
}

void ClearInflightStep() {
    volatile char* marker = g_inflightMarker;
    if (marker == nullptr) {
        return;
    }

    // Only the thread whose breadcrumb is still in the slot may erase it. There is one
    // marker for the process, so clearing unconditionally would let a device module call
    // completing on another thread wipe the record for the step actually in flight —
    // which is the one case this marker exists to capture.
    if (g_inflightThread != static_cast<LONG>(GetCurrentThreadId())) {
        return;
    }

    for (SIZE_T index = 0; index < kInflightMarkerSize; ++index) {
        marker[index] = '\0';
    }
}

struct InflightStepScope {
    InflightStepScope(const char* step, const char* stage) { SetInflightStep(step, stage); }
    ~InflightStepScope() { ClearInflightStep(); }
};

// Runs inside the one-time initialisation that installs the reporter, so it needs no
// guard of its own and cannot race a second call-setup thread.
void OpenInflightMarker() {
    const std::wstring path = std::wstring(g_crashDiagnosticsPath) + L".inflight";
    if (path.size() >= MAX_PATH) {
        return;
    }

    std::memcpy(g_inflightMarkerPath, path.c_str(), (path.size() + 1) * sizeof(wchar_t));

    g_inflightMarkerFile = CreateFile2(
        g_inflightMarkerPath,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        OPEN_ALWAYS,
        nullptr);
    if (g_inflightMarkerFile == INVALID_HANDLE_VALUE) {
        return;
    }

    // A mapping larger than the file extends it, which avoids needing the desktop-only
    // file sizing calls that are unavailable to an app container.
    g_inflightMarkerMapping = CreateFileMappingFromApp(
        g_inflightMarkerFile, nullptr, PAGE_READWRITE, kMarkerSectionSize, nullptr);
    if (g_inflightMarkerMapping == nullptr) {
        CloseHandle(g_inflightMarkerFile);
        g_inflightMarkerFile = INVALID_HANDLE_VALUE;
        return;
    }

    auto* view = static_cast<char*>(
        MapViewOfFileFromApp(g_inflightMarkerMapping, FILE_MAP_WRITE, 0, kMarkerSectionSize));
    if (view == nullptr) {
        CloseHandle(g_inflightMarkerMapping);
        CloseHandle(g_inflightMarkerFile);
        g_inflightMarkerMapping = nullptr;
        g_inflightMarkerFile = INVALID_HANDLE_VALUE;
        return;
    }

    std::memcpy(g_inflightRecovered, view, kInflightMarkerSize);
    g_inflightRecovered[kInflightMarkerSize - 1] = '\0';
    std::memcpy(g_faultRecovered, view + kInflightMarkerSize, kFaultSlotSize);
    g_faultRecovered[kFaultSlotSize - 1] = '\0';
    std::memset(view, 0, kMarkerSectionSize);
    g_inflightMarker = view;
    g_faultSlot = view + kInflightMarkerSize;
}

struct AudioDeviceLifecycleScope {
    explicit AudioDeviceLifecycleScope(const char* step) {
        g_audioDeviceLifecycleStep = step;
        InterlockedExchange(&g_audioDeviceLifecycleThread,
            static_cast<LONG>(GetCurrentThreadId()));
        InterlockedIncrement(&g_audioDeviceLifecycleDepth);
    }
    ~AudioDeviceLifecycleScope() { InterlockedDecrement(&g_audioDeviceLifecycleDepth); }
};

void AppendHex32(char* buffer, int& length, uint32_t value) {
    static const char digits[] = "0123456789ABCDEF";
    buffer[length++] = '0';
    buffer[length++] = 'x';
    for (int shift = 28; shift >= 0; shift -= 4) {
        buffer[length++] = digits[(value >> shift) & 0xF];
    }
}

void AppendLiteral(char* buffer, int& length, const char* text) {
    while (*text != '\0') {
        buffer[length++] = *text++;
    }
}

// Codes that are only ever raised by a deliberate, fatal abort path, so they are worth a
// blocking write wherever they occur because nothing continues after them. WebRTC's
// RTC_CHECK - which stays live even with NDEBUG - and RTC_DCHECK both reach
// webrtc_checks_impl::FatalLog, which calls DebugBreak() and then abort(). On a retail
// device with no debugger attached a breakpoint is therefore a failed check, and its
// absence from this list is why a failed check has so far killed the process leaving
// nothing behind but a log that stops.
bool IsAlwaysFatalExceptionCode(DWORD code) {
    switch (code) {
    case EXCEPTION_BREAKPOINT:  // RTC_CHECK / RTC_DCHECK via DebugBreak()
    case 0xC0000409:            // STATUS_STACK_BUFFER_OVERRUN, also __fastfail
    case 0xC0000417:            // STATUS_INVALID_CRUNTIME_PARAMETER
    case 0xC0000374:            // STATUS_HEAP_CORRUPTION
    case 0xC0000420:            // STATUS_ASSERTION_FAILURE
        return true;
    default:
        return false;
    }
}

bool IsFatalExceptionCode(DWORD code) {
    switch (code) {
    case EXCEPTION_ACCESS_VIOLATION:
    case EXCEPTION_ILLEGAL_INSTRUCTION:
    case EXCEPTION_PRIV_INSTRUCTION:
    case EXCEPTION_IN_PAGE_ERROR:
    case EXCEPTION_DATATYPE_MISALIGNMENT:
    case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
        return true;
    default:
        // C++ exceptions (0xE06D7363) are deliberately absent despite being catchable
        // here: this facade throws std::invalid_argument as ordinary argument validation,
        // so admitting them would bury a real fault under routine traffic.
        //
        // A stack overflow is deliberately absent: the handler would run on the
        // exhausted stack and fault again before writing anything.
        return IsAlwaysFatalExceptionCode(code);
    }
}

void WriteFaultLine(const char* line, int length) {
    const HANDLE file = CreateFile2(
        g_crashDiagnosticsPath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        OPEN_ALWAYS,
        nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        FlushFileBuffers(file);
        CloseHandle(file);
    }
}

bool ClaimFaultReport() {
    if (InterlockedDecrement(&g_crashDiagnosticsBudget) < 0) {
        InterlockedIncrement(&g_crashDiagnosticsBudget);
        // Says once that reports are being dropped, so an empty log is never mistaken for
        // a clean run. The notice itself is outside the budget it is reporting on, and it
        // lives here so that every caller is covered, including the abrupt termination
        // handlers whose reports would otherwise disappear without trace.
        if (InterlockedExchange(&g_budgetExhaustedReported, 1) == 0) {
            char notice[64];
            int noticeLength = 0;
            AppendLiteral(notice, noticeLength, "result=budget_exhausted\r\n");
            WriteFaultLine(notice, noticeLength);
        }
        return false;
    }
    return true;
}

// The budget is what stops a repeating fault from filling the log, but it is spent for
// the lifetime of the process, so a call late in a session could find it already empty
// and drop the very report the build exists to capture. A silent drop is indistinguishable
// from no fault at all, so each call restores the allowance and the first drop within a
// call says so explicitly.
void RenewFaultBudget() {
    InterlockedExchange(&g_crashDiagnosticsBudget, kFaultBudgetPerCall);
    InterlockedExchange(&g_budgetExhaustedReported, 0);
}

const char* CurrentLifecycleStep() {
    const char* step = g_audioDeviceLifecycleStep;
    return step == nullptr ? "unknown" : step;
}

// Two faults racing each other, or a death part way through the copy, would otherwise
// splice two records into a line that is still syntactically valid but names the wrong
// code - the worst possible outcome for a slot whose entire purpose is to name the last
// thing that happened. Admitting one writer at a time removes the splice, and writing the
// terminator before the body means a record cut short by the death reads as empty rather
// than as a different fault. The first byte is written last, so the record only becomes
// visible to the next process once all of it is there.
void PublishFaultSlot(const char* text, int length) {
    volatile char* const slot = g_faultSlot;
    if (slot == nullptr || length <= 0) {
        return;
    }
    if (InterlockedCompareExchange(&g_faultSlotWriter, 1, 0) != 0) {
        return;
    }

    slot[0] = '\0';
    const int limit = static_cast<int>(kFaultSlotSize) - 1;
    const int count = length < limit ? length : limit;
    for (int index = 1; index < count; ++index) {
        slot[index] = text[index];
    }
    slot[count] = '\0';
    slot[0] = text[0];

    InterlockedExchange(&g_faultSlotWriter, 0);
}

// Nothing else empties the slot, so without this a single out-of-step fault would be
// reported as the last fault before exit on every subsequent launch, long after the call
// that produced it ended cleanly. It takes the same writer claim as a publish: clearing
// part way through one would otherwise let the publisher's final store resurrect the
// record after the clear, which is the stale read this exists to prevent. Declining when
// a publish holds the claim is correct, because a fault in progress is newer information
// than the clean teardown asking to erase it.
void ClearFaultSlot() {
    volatile char* const slot = g_faultSlot;
    if (slot == nullptr) {
        return;
    }
    if (InterlockedCompareExchange(&g_faultSlotWriter, 1, 0) != 0) {
        return;
    }

    slot[0] = '\0';

    InterlockedExchange(&g_faultSlotWriter, 0);
}

// A fatal error that does not travel as a structured exception never reaches a vectored
// handler. WebRTC's RTC_CHECK failures end in abort(), a pure virtual call or an invalid
// CRT parameter terminate just as abruptly, and all of them would leave exactly the same
// evidence as the crash under investigation: a log that simply stops.
void ReportAbruptTermination(const char* result) {
    if (g_crashDiagnosticsPath[0] == L'\0' || !ClaimFaultReport()) {
        return;
    }

    char line[160];
    int length = 0;
    AppendLiteral(line, length, "result=");
    AppendLiteral(line, length, result);
    AppendLiteral(line, length, ";step=");
    AppendLiteral(line, length, CurrentLifecycleStep());
    AppendLiteral(line, length, ";in_lifecycle=");
    line[length++] = (g_audioDeviceLifecycleDepth > 0) ? '1' : '0';
    AppendLiteral(line, length, "\r\n");
    WriteFaultLine(line, length);
}

void AbortReporter(int) { ReportAbruptTermination("abort"); }
void TerminateReporter() { ReportAbruptTermination("terminate"); }
void PureCallReporter() { ReportAbruptTermination("purecall"); }

void InvalidParameterReporter(
    const wchar_t*, const wchar_t*, const wchar_t*, unsigned int, uintptr_t) {
    ReportAbruptTermination("invalid_parameter");
}

LONG CALLBACK FatalExceptionReporter(EXCEPTION_POINTERS* pointers) {
    if (pointers == nullptr ||
        pointers->ExceptionRecord == nullptr ||
        (g_audioDeviceLifecycleDepth <= 0 && g_callActiveDepth <= 0) ||
        !IsFatalExceptionCode(pointers->ExceptionRecord->ExceptionCode)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const bool inStep = g_audioDeviceLifecycleDepth > 0;
    const bool alwaysFatal = IsAlwaysFatalExceptionCode(pointers->ExceptionRecord->ExceptionCode);

    // The budget is claimed further down, immediately around the log write, rather than
    // here. Claiming it up front would also discard the mapped slot write, and the slot is
    // the one channel that costs no budget and survives a death before the write reaches
    // disk - so a fatal breakpoint arriving after the allowance ran out would be recorded
    // in neither place, which is precisely the report this build exists to capture.

    // The scope only tracks depth, so the step and thread it recorded outlive it. Reading
    // them when no step is live would name a device call that finished long ago and
    // compare against the thread that made it, which reads as a strong signal pointing at
    // a path that is not involved at all.
    const char* step = inStep ? CurrentLifecycleStep() : "none";

    // The exception code alone cannot name the failure, because .NET Native raises an
    // access violation for every ordinary null dereference and this app has a known
    // benign one. What separates them is where the fault is: an offset inside this
    // module is webrtc or tgcalls code and can be resolved against the build's map
    // file, while anything outside it is another module's problem. An offset within a
    // module carries no information about the install, so it stays privacy-safe.
    const auto address = reinterpret_cast<uintptr_t>(pointers->ExceptionRecord->ExceptionAddress);
    const auto base = reinterpret_cast<uintptr_t>(&__ImageBase);
    const auto* headers = reinterpret_cast<const IMAGE_NT_HEADERS*>(
        base + static_cast<uintptr_t>(__ImageBase.e_lfanew));
    const auto size = static_cast<uintptr_t>(headers->OptionalHeader.SizeOfImage);
    const bool inModule = address >= base && address < base + size;
    // The app binary is where the runtime compiles managed code to, so a fault inside it
    // is a managed null dereference rather than a failure in the platform audio stack.
    // The two need completely different fixes and are otherwise indistinguishable.
    const bool inApp = g_appModuleSize != 0 &&
        address >= g_appModuleBase &&
        address < g_appModuleBase + g_appModuleSize;

    char line[256];
    int length = 0;
    AppendLiteral(line, length, "result=exception;code=");
    AppendHex32(line, length, static_cast<uint32_t>(pointers->ExceptionRecord->ExceptionCode));
    AppendLiteral(line, length, ";step=");
    AppendLiteral(line, length, step);
    AppendLiteral(line, length, ";same_thread=");
    line[length++] =
        (inStep && static_cast<LONG>(GetCurrentThreadId()) == g_audioDeviceLifecycleThread) ? '1' : '0';
    AppendLiteral(line, length, ";in_step=");
    line[length++] = inStep ? '1' : '0';
    AppendLiteral(line, length, ";engine=");
    line[length++] = inModule ? '1' : '0';
    AppendLiteral(line, length, ";app=");
    line[length++] = inApp ? '1' : '0';
    if (inModule) {
        AppendLiteral(line, length, ";offset=");
        AppendHex32(line, length, static_cast<uint32_t>(address - base));
    } else if (inApp) {
        AppendLiteral(line, length, ";offset=");
        AppendHex32(line, length, static_cast<uint32_t>(address - g_appModuleBase));
    }

    // For an access violation the operation and whether the target was a null page
    // separate a managed null dereference from genuine memory corruption.
    if (pointers->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION &&
        pointers->ExceptionRecord->NumberParameters >= 2) {
        const auto operation = pointers->ExceptionRecord->ExceptionInformation[0];
        AppendLiteral(line, length, ";access=");
        AppendLiteral(line, length, operation == 0 ? "read" : (operation == 1 ? "write" : "execute"));
        AppendLiteral(line, length, ";null_page=");
        line[length++] = (pointers->ExceptionRecord->ExceptionInformation[1] < 0x10000) ? '1' : '0';
    }

    AppendLiteral(line, length, ";noncontinuable=");
    line[length++] = (pointers->ExceptionRecord->ExceptionFlags & EXCEPTION_NONCONTINUABLE) ? '1' : '0';

    // A code that is fatal by construction is recorded in both places: the mapped slot
    // first, because it is free and survives the death, and the log second.
    if (alwaysFatal) {
        PublishFaultSlot(line, length);
    }

    // Only a fault raised while a device step is on the stack, or one that is fatal by
    // construction, is worth the blocking write. Anything else goes to the mapped slot
    // alone, which performs no file I/O on the engine's real-time threads.
    if (inStep || alwaysFatal) {
        if (ClaimFaultReport()) {
            AppendLiteral(line, length, "\r\n");
            WriteFaultLine(line, length);
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    PublishFaultSlot(line, length);
    return EXCEPTION_CONTINUE_SEARCH;
}

BOOL CALLBACK InstallFatalExceptionReporter(PINIT_ONCE, PVOID, PVOID*) {
    OpenInflightMarker();

    // Captured here rather than in the handler, because resolving a module from a fault
    // context would take the loader lock the faulting thread may already hold.
    if (const auto app = GetModuleHandleW(nullptr)) {
        const auto appBase = reinterpret_cast<uintptr_t>(app);
        const auto* appDos = reinterpret_cast<const IMAGE_DOS_HEADER*>(appBase);
        const auto* appHeaders = reinterpret_cast<const IMAGE_NT_HEADERS*>(
            appBase + static_cast<uintptr_t>(appDos->e_lfanew));
        g_appModuleBase = appBase;
        g_appModuleSize = static_cast<uintptr_t>(appHeaders->OptionalHeader.SizeOfImage);
    }

    // A step left in flight by the previous process names what was running when it died,
    // including the fail-fast and abrupt termination cases no handler can observe.
    if (g_inflightRecovered[0] != '\0' && g_crashDiagnosticsPath[0] != L'\0') {
        char line[160];
        int length = 0;
        AppendLiteral(line, length, "result=inflight_at_exit;step=");
        for (const char* text = g_inflightRecovered;
             *text != '\0' && length < static_cast<int>(sizeof(line)) - 8;
             ++text) {
            line[length++] = *text;
        }
        AppendLiteral(line, length, "\r\n");
        WriteFaultLine(line, length);
    }

    // The last fault the previous process saw. Each fault overwrote the one before it, so
    // what survived is the closest observed event to the death.
    if (g_faultRecovered[0] != '\0' && g_crashDiagnosticsPath[0] != L'\0') {
        char line[kFaultSlotSize + 32];
        int length = 0;
        AppendLiteral(line, length, "result=last_fault_before_exit;fault=");

        // The stored record opens with its own result= key, which would make the emitted
        // line carry two of them and leave any reader to guess which one it means.
        const char* text = g_faultRecovered;
        constexpr char kResultKey[] = "result=";
        if (std::strncmp(text, kResultKey, sizeof(kResultKey) - 1) == 0) {
            text += sizeof(kResultKey) - 1;
        }

        for (; *text != '\0' && length < static_cast<int>(sizeof(line)) - 8; ++text) {
            line[length++] = *text;
        }
        AppendLiteral(line, length, "\r\n");
        WriteFaultLine(line, length);
    }

    // The header gates this out of the app partition, but the export exists and is
    // permitted for store apps; declaring it keeps the UWP surface otherwise untouched.
    g_crashDiagnosticsHandle = AddVectoredExceptionHandler(1, FatalExceptionReporter);
    signal(SIGABRT, AbortReporter);
    std::set_terminate(TerminateReporter);
    _set_purecall_handler(PureCallReporter);
    _set_invalid_parameter_handler(InvalidParameterReporter);
    return TRUE;
}

// The audio device module lifecycle calls below run once per call and are the last
// thing to execute before the process has been observed to die mid-call, so each one
// is bracketed by a breadcrumb and executed under a structured exception guard. The
// guard converts an access violation inside the platform audio stack into a reported
// failure code instead of terminating the app, which both names the faulting step and
// lets the rest of the call continue so the remaining diagnostics can still be
// collected. These helpers take no objects requiring unwinding, which is what allows
// __try to be used here.

using AdmPlainMethod = int32_t (webrtc::AudioDeviceModule::*)();
using AdmBoolOutMethod = int32_t (webrtc::AudioDeviceModule::*)(bool*);
using AdmConstBoolOutMethod = int32_t (webrtc::AudioDeviceModule::*)(bool*) const;
using AdmBoolInMethod = int32_t (webrtc::AudioDeviceModule::*)(bool);
using AdmWindowsDeviceMethod = int32_t (webrtc::AudioDeviceModule::*)(webrtc::AudioDeviceModule::WindowsDeviceType);
using AdmVolumeOutMethod = int32_t (webrtc::AudioDeviceModule::*)(uint32_t*) const;
using AdmVolumeInMethod = int32_t (webrtc::AudioDeviceModule::*)(uint32_t);
using AdmDeviceIndexMethod = int32_t (webrtc::AudioDeviceModule::*)(uint16_t);
using AdmAudioCallbackMethod = int32_t (webrtc::AudioDeviceModule::*)(webrtc::AudioTransport*);

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmPlainMethod method,
        int* faulted) {
    __try {
        return (impl->*method)();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmBoolOutMethod method,
        bool* argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        const webrtc::AudioDeviceModule* impl,
        AdmConstBoolOutMethod method,
        bool* argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmBoolInMethod method,
        bool argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmWindowsDeviceMethod method,
        webrtc::AudioDeviceModule::WindowsDeviceType argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        const webrtc::AudioDeviceModule* impl,
        AdmVolumeOutMethod method,
        uint32_t* argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmVolumeInMethod method,
        uint32_t argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmDeviceIndexMethod method,
        uint16_t argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

__declspec(noinline) int32_t InvokeGuarded(
        webrtc::AudioDeviceModule* impl,
        AdmAudioCallbackMethod method,
        webrtc::AudioTransport* argument,
        int* faulted) {
    __try {
        return (impl->*method)(argument);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *faulted = 1;
        return -1;
    }
}

// Published by the playout probe on WebRTC's render thread and read by
// AudioDeviceStatus() on a normal thread. Only one call is active at a time, so a flat
// set of atomics is sufficient and avoids any lock on the realtime path.
std::atomic<int> g_playoutPeakPermille{0};
std::atomic<uint32_t> g_playoutSampleRate{0};
std::atomic<uint64_t> g_playoutWindows{0};
std::atomic<int> g_capturePeakPermille{0};
std::atomic<uint32_t> g_captureSampleRate{0};
std::atomic<uint64_t> g_captureWindows{0};

/// <summary>
/// Wraps the real audio transport so the playout stream can be measured. Only a peak
/// amplitude and a frame count are derived; no audio is copied, retained, or reported
/// in any form that could reconstruct speech.
///
/// This is the one measurement that separates the two possible causes of a silent
/// call: if WebRTC hands the device non-silent samples and nothing is audible, the
/// fault is in the device or the routing; if the samples are silent, the fault is
/// upstream in decode, the receive stream, or the network.
/// </summary>
struct SpeakerphoneGainState {
    std::atomic<bool> speakerphoneRequested{false};
    std::atomic<uint64_t> attenuatedBuffers{0};
};

class PlayoutProbeAudioTransport : public webrtc::AudioTransport {
public:
    PlayoutProbeAudioTransport(
            webrtc::AudioTransport* inner,
            std::shared_ptr<SpeakerphoneGainState> speakerphoneGainState)
        : _inner(inner)
        , _speakerphoneGainState(std::move(speakerphoneGainState)) {
        g_playoutPeakPermille.store(0, std::memory_order_relaxed);
        g_playoutSampleRate.store(0, std::memory_order_relaxed);
        g_playoutWindows.store(0, std::memory_order_release);
        g_capturePeakPermille.store(0, std::memory_order_relaxed);
        g_captureSampleRate.store(0, std::memory_order_relaxed);
        g_captureWindows.store(0, std::memory_order_release);
    }

    int32_t RecordedDataIsAvailable(
            const void* audioSamples,
            size_t nSamples,
            size_t nBytesPerSample,
            size_t nChannels,
            uint32_t samplesPerSec,
            uint32_t totalDelayMS,
            int32_t clockDrift,
            uint32_t currentMicLevel,
            bool keyPressed,
            uint32_t& newMicLevel) override {
        MeasureCapture(
            static_cast<const int16_t*>(audioSamples),
            nSamples,
            nChannels,
            samplesPerSec,
            nBytesPerSample);
        return _inner->RecordedDataIsAvailable(
            audioSamples, nSamples, nBytesPerSample, nChannels, samplesPerSec,
            totalDelayMS, clockDrift, currentMicLevel, keyPressed, newMicLevel);
    }

    int32_t RecordedDataIsAvailable(
            const void* audioSamples,
            size_t nSamples,
            size_t nBytesPerSample,
            size_t nChannels,
            uint32_t samplesPerSec,
            uint32_t totalDelayMS,
            int32_t clockDrift,
            uint32_t currentMicLevel,
            bool keyPressed,
            uint32_t& newMicLevel,
            absl::optional<int64_t> estimatedCaptureTimeNS) override {
        MeasureCapture(
            static_cast<const int16_t*>(audioSamples),
            nSamples,
            nChannels,
            samplesPerSec,
            nBytesPerSample);
        return _inner->RecordedDataIsAvailable(
            audioSamples, nSamples, nBytesPerSample, nChannels, samplesPerSec,
            totalDelayMS, clockDrift, currentMicLevel, keyPressed, newMicLevel,
            estimatedCaptureTimeNS);
    }

    int32_t NeedMorePlayData(
            size_t nSamples,
            size_t nBytesPerSample,
            size_t nChannels,
            uint32_t samplesPerSec,
            void* audioSamples,
            size_t& nSamplesOut,
            int64_t* elapsed_time_ms,
            int64_t* ntp_time_ms) override {
        const auto result = _inner->NeedMorePlayData(
            nSamples, nBytesPerSample, nChannels, samplesPerSec, audioSamples,
            nSamplesOut, elapsed_time_ms, ntp_time_ms);

        if (result == 0 && nBytesPerSample == sizeof(int16_t) * nChannels) {
            // nSamplesOut is already the interleaved total (frames * channels), not a
            // per-channel count, so it must not be multiplied by nChannels again.
            ApplySpeakerphoneGain(static_cast<int16_t*>(audioSamples), nSamplesOut);
            Measure(
                static_cast<const int16_t*>(audioSamples),
                nSamplesOut,
                nChannels,
                samplesPerSec);
        }

        return result;
    }

    void PullRenderData(
            int bits_per_sample,
            int sample_rate,
            size_t number_of_channels,
            size_t number_of_frames,
            void* audio_data,
            int64_t* elapsed_time_ms,
            int64_t* ntp_time_ms) override {
        _inner->PullRenderData(
            bits_per_sample, sample_rate, number_of_channels, number_of_frames,
            audio_data, elapsed_time_ms, ntp_time_ms);

        if (bits_per_sample == 16) {
            // Here number_of_frames really is per-channel, so the interleaved total is
            // the product.
            ApplySpeakerphoneGain(
                static_cast<int16_t*>(audio_data),
                number_of_frames * number_of_channels);
            Measure(
                static_cast<const int16_t*>(audio_data),
                number_of_frames * number_of_channels,
                number_of_channels,
                static_cast<uint32_t>(sample_rate));
        }
    }

    /// <summary>
    /// Reads the most recent completed window. Safe to call from any thread; returns
    /// false until the first window has closed.
    /// </summary>
    static bool TryReadLevel(int* permille, uint32_t* sampleRate, uint64_t* windows) {
        const auto completed = g_playoutWindows.load(std::memory_order_acquire);
        if (completed == 0) {
            return false;
        }

        *permille = g_playoutPeakPermille.load(std::memory_order_relaxed);
        *sampleRate = g_playoutSampleRate.load(std::memory_order_relaxed);
        *windows = completed;
        return true;
    }

private:
    void ApplySpeakerphoneGain(int16_t* samples, size_t count) {
        if (samples == nullptr || !_speakerphoneGainState ||
                !_speakerphoneGainState->speakerphoneRequested.load(std::memory_order_acquire)) {
            return;
        }

        for (size_t index = 0; index < count; ++index) {
            samples[index] = static_cast<int16_t>(samples[index] / 2);
        }
        _speakerphoneGainState->attenuatedBuffers.fetch_add(1, std::memory_order_relaxed);
    }

    void MeasureCapture(
            const int16_t* samples,
            size_t frames,
            size_t channels,
            uint32_t sampleRate,
            size_t bytesPerSample) {
        if (channels == 0 || bytesPerSample != sizeof(int16_t) * channels ||
            samples == nullptr || frames == 0 || sampleRate == 0) {
            return;
        }

        const size_t count = frames * channels;
        int32_t peak = 0;
        for (size_t i = 0; i < count; ++i) {
            const int32_t value = samples[i] < 0 ? -static_cast<int32_t>(samples[i]) : samples[i];
            if (value > peak) {
                peak = value;
            }
        }

        if (peak > _captureWindowPeak) {
            _captureWindowPeak = peak;
        }

        _captureWindowFrames += frames;
        if (_captureWindowFrames < static_cast<uint64_t>(sampleRate) * 2) {
            return;
        }

        g_capturePeakPermille.store(
            static_cast<int>((static_cast<int64_t>(_captureWindowPeak) * 1000) / 32768),
            std::memory_order_relaxed);
        g_captureSampleRate.store(sampleRate, std::memory_order_relaxed);
        g_captureWindows.fetch_add(1, std::memory_order_release);

        _captureWindowFrames = 0;
        _captureWindowPeak = 0;
    }

    /// <summary>
    /// Accumulates a peak over roughly two seconds of playout and publishes it as a
    /// permille of full scale, so a silent stream is distinguishable from a quiet one
    /// without revealing anything about the content.
    ///
    /// This runs on WebRTC's render thread, inside the device module's critical section
    /// and against a 10 ms deadline, so it only ever touches atomics. Emitting the
    /// diagnostic would mean file I/O here, which would stall playout and could itself
    /// cause the glitching the probe exists to characterise; the value is read out
    /// instead by AudioDeviceStatus() on a normal thread.
    /// </summary>
    void Measure(const int16_t* samples, size_t count, size_t channels, uint32_t sampleRate) {
        if (samples == nullptr || count == 0 || sampleRate == 0 || channels == 0) {
            return;
        }

        int32_t peak = 0;
        for (size_t i = 0; i < count; ++i) {
            const int32_t value = samples[i] < 0 ? -static_cast<int32_t>(samples[i]) : samples[i];
            if (value > peak) {
                peak = value;
            }
        }

        if (peak > _windowPeak) {
            _windowPeak = peak;
        }

        _windowFrames += count / channels;
        if (_windowFrames < static_cast<uint64_t>(sampleRate) * 2) {
            return;
        }

        g_playoutPeakPermille.store(
            static_cast<int>((static_cast<int64_t>(_windowPeak) * 1000) / 32768),
            std::memory_order_relaxed);
        g_playoutSampleRate.store(sampleRate, std::memory_order_relaxed);
        g_playoutWindows.fetch_add(1, std::memory_order_release);

        _windowFrames = 0;
        _windowPeak = 0;
    }

    webrtc::AudioTransport* _inner;
    std::shared_ptr<SpeakerphoneGainState> _speakerphoneGainState;

    // Only touched on the render thread.
    uint64_t _windowFrames = 0;
    int32_t _windowPeak = 0;

    // Only touched on the recording thread.
    uint64_t _captureWindowFrames = 0;
    int32_t _captureWindowPeak = 0;
};

/// <summary>
/// Snapshots and formats the tgcalls receive-path counters. Zeroed per call because the
/// counters are process-global; otherwise a previous call's totals read as this one's.
/// </summary>
void ResetIncomingAudioCounters() {
    ::tgcalls::g_diagIncomingAudioRtp.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioRtcp.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioParseFailed.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioUndemuxed.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioRtcpSenderReport.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioRtcpFeedback.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagIncomingAudioMaxBytes.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagOutgoingAudioRtp.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagOutgoingAudioRtcp.store(0, std::memory_order_relaxed);
    ::tgcalls::g_diagAecEnabled.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagAecMobileMode.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagNoiseSuppressionEnabled.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagGainControlEnabled.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagAecResidualLikelihoodPermille.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagAecResidualLikelihoodRecentMaxPermille.store(-1, std::memory_order_relaxed);
    ::tgcalls::g_diagAecEchoReturnLossDecibelTenths.store(-10000, std::memory_order_relaxed);
    ::tgcalls::g_diagAecEchoReturnLossEnhancementDecibelTenths.store(-10000, std::memory_order_relaxed);
    ::tgcalls::g_diagAecDelayMilliseconds.store(-1, std::memory_order_relaxed);
}

std::string DescribeAudioProcessingDiagnostics() {
    const auto aecEnabled = ::tgcalls::g_diagAecEnabled.load(std::memory_order_relaxed);
    const auto mobileMode = ::tgcalls::g_diagAecMobileMode.load(std::memory_order_relaxed);
    const auto noiseSuppression = ::tgcalls::g_diagNoiseSuppressionEnabled.load(std::memory_order_relaxed);
    const auto gainControl = ::tgcalls::g_diagGainControlEnabled.load(std::memory_order_relaxed);

    if (aecEnabled < 0 || mobileMode < 0 || noiseSuppression < 0 || gainControl < 0) {
        return ";apm=pending";
    }

    const auto mode = aecEnabled == 0
        ? "off"
        : (mobileMode == 0 ? "aec3" : "aecm");
    std::string result = ";apm=" + std::string(mode) +
        ";apm_ns=" + (noiseSuppression == 0 ? "0" : "1") +
        ";apm_agc=" + (gainControl == 0 ? "0" : "1");

    const auto residual = ::tgcalls::g_diagAecResidualLikelihoodPermille.load(std::memory_order_relaxed);
    const auto residualMax =
        ::tgcalls::g_diagAecResidualLikelihoodRecentMaxPermille.load(std::memory_order_relaxed);
    const auto echoReturnLoss =
        ::tgcalls::g_diagAecEchoReturnLossDecibelTenths.load(std::memory_order_relaxed);
    const auto echoReturnLossEnhancement =
        ::tgcalls::g_diagAecEchoReturnLossEnhancementDecibelTenths.load(std::memory_order_relaxed);
    const auto delay = ::tgcalls::g_diagAecDelayMilliseconds.load(std::memory_order_relaxed);

    if (residual >= 0) {
        result += ";aec_residual_pm=" + std::to_string(residual);
    }
    if (residualMax >= 0) {
        result += ";aec_residual_max_pm=" + std::to_string(residualMax);
    }
    if (echoReturnLoss > -10000) {
        result += ";aec_erl_db10=" + std::to_string(echoReturnLoss);
    }
    if (echoReturnLossEnhancement > -10000) {
        result += ";aec_erle_db10=" + std::to_string(echoReturnLossEnhancement);
    }
    if (delay >= 0) {
        result += ";aec_delay_ms=" + std::to_string(delay);
    }

    return result;
}

std::string DescribeIncomingAudioCounters() {
    // The managed diagnostics sanitizer replaces any run of six or more digits with
    // "[redacted_number]", so an unbounded counter would erase itself from the log after
    // roughly half an hour of call. Saturating below that threshold keeps the field
    // readable; the diagnostic question is whether media arrives at all and how fast, not
    // the exact total, and a saturated value still reads unambiguously as "a great many".
    const auto clamp = [](uint32_t value) {
        constexpr uint32_t kMax = 99999;
        return std::to_string(value < kMax ? value : kMax);
    };

    return "rtp_in=" + clamp(::tgcalls::g_diagIncomingAudioRtp.load(std::memory_order_relaxed)) +
        ";rtcp_in=" + clamp(::tgcalls::g_diagIncomingAudioRtcp.load(std::memory_order_relaxed)) +
        ";rtcp_sr=" + clamp(::tgcalls::g_diagIncomingAudioRtcpSenderReport.load(std::memory_order_relaxed)) +
        ";rtcp_fb=" + clamp(::tgcalls::g_diagIncomingAudioRtcpFeedback.load(std::memory_order_relaxed)) +
        ";rtp_out=" + clamp(::tgcalls::g_diagOutgoingAudioRtp.load(std::memory_order_relaxed)) +
        ";rtcp_out=" + clamp(::tgcalls::g_diagOutgoingAudioRtcp.load(std::memory_order_relaxed)) +
        ";msg_max=" + clamp(::tgcalls::g_diagIncomingAudioMaxBytes.load(std::memory_order_relaxed)) +
        ";rtp_bad=" + clamp(::tgcalls::g_diagIncomingAudioParseFailed.load(std::memory_order_relaxed)) +
        ";rtp_undemux=" + clamp(::tgcalls::g_diagIncomingAudioUndemuxed.load(std::memory_order_relaxed)) +
        DescribeAudioProcessingDiagnostics();
}

enum class PlayoutDeviceKind {
    Other,
    Earpiece,
    Speaker,
    Headset,
};

PlayoutDeviceKind ClassifyPlayoutDeviceName(const char* name) {
    std::string lowered;
    for (const auto* cursor = name; cursor != nullptr && *cursor != '\0'; ++cursor) {
        const auto value = static_cast<unsigned char>(*cursor);
        if (std::isalnum(value) != 0) {
            lowered += static_cast<char>(std::tolower(value));
        } else if (*cursor == ' ' || *cursor == '-' || *cursor == '_') {
            lowered += ' ';
        }
    }

    const auto contains = [&lowered](const char* needle) {
        return lowered.find(needle) != std::string::npos;
    };

    if (contains("earpiece") || contains("handset") || contains("receiver")) {
        return PlayoutDeviceKind::Earpiece;
    }
    if (contains("speaker") || contains("loud")) {
        return PlayoutDeviceKind::Speaker;
    }
    if (contains("head") || contains("bluetooth")) {
        return PlayoutDeviceKind::Headset;
    }
    return PlayoutDeviceKind::Other;
}

const char* ToDiagnosticDeviceKind(PlayoutDeviceKind kind) {
    switch (kind) {
    case PlayoutDeviceKind::Earpiece:
        return "ear";
    case PlayoutDeviceKind::Speaker:
        return "spk";
    case PlayoutDeviceKind::Headset:
        return "hs";
    default:
        return "oth";
    }
}

struct PlayoutDeviceIndices {
    int earpiece = -1;
    int speaker = -1;
};

/// <summary>
/// Renders the render-endpoint names as a fixed-shape list so the endpoint a silent call
/// is playing into can be identified from a diagnostics file. Device names describe
/// hardware, not the user, and are sanitised anyway: every character outside
/// [A-Za-z0-9 _-] is dropped and each name is truncated, so a renamed device cannot
/// smuggle free text into the log.
///
/// Naming a device is not a cheap lookup in this WebRTC fork: every call re-enumerates
/// the whole render device class and blocks on the async result, so the number of names
/// read is capped to keep call setup bounded on a handset that reports many endpoints.
/// </summary>
std::string DescribePlayoutDevices(
        webrtc::AudioDeviceModule* module,
        int16_t count,
        PlayoutDeviceIndices* indices = nullptr) {
    if (module == nullptr || count <= 0) {
        return "none";
    }

    // Device names are long enough that the managed sanitizer replaces each one wholesale
    // with an opaque-token placeholder, which made the list unreadable in practice. A short
    // classification survives because ':' breaks the token run, and it carries what the
    // diagnostic actually needs: which index is the earpiece and which the loudspeaker, so
    // a routing complaint can be checked against the endpoint that was really selected.
    constexpr int16_t kMaxNamedDevices = 4;
    const int16_t named = count < kMaxNamedDevices ? count : kMaxNamedDevices;

    std::string list;
    for (int16_t index = 0; index < named; ++index) {
        char name[webrtc::kAdmMaxDeviceNameSize] = {};
        char guid[webrtc::kAdmMaxGuidSize] = {};
        const auto result = module->PlayoutDeviceName(static_cast<uint16_t>(index), name, guid);

        if (!list.empty()) {
            list += "|";
        }
        list += std::to_string(index) + ":";

        if (result != 0) {
            list += "unavailable";
            continue;
        }

        name[webrtc::kAdmMaxDeviceNameSize - 1] = '\0';
        const auto kind = ClassifyPlayoutDeviceName(name);
        if (indices != nullptr) {
            if (kind == PlayoutDeviceKind::Earpiece && indices->earpiece < 0) {
                indices->earpiece = index;
            } else if (kind == PlayoutDeviceKind::Speaker && indices->speaker < 0) {
                indices->speaker = index;
            }
        }

        std::string sanitised;
        for (const char* cursor = name; *cursor != '\0' && sanitised.size() < 48; ++cursor) {
            const auto value = static_cast<unsigned char>(*cursor);
            if (std::isalnum(value) != 0) {
                sanitised += *cursor;
            } else if (*cursor == ' ' || *cursor == '-' || *cursor == '_') {
                sanitised += '_';
            }
        }

        list += ToDiagnosticDeviceKind(kind);
        list += ":";
        list += sanitised.empty() ? "unnamed" : sanitised;
    }

    if (named < count) {
        list += "|+" + std::to_string(count - named);
    }

    return list;
}

/// <summary>
/// Forwards every audio device module call to the real platform module, reporting only
/// fixed step names and numeric result codes. No device names, identifiers, or audio
/// content are read or reported.
/// </summary>
class DiagnosticAudioDeviceModule : public tgcalls::DefaultWrappedAudioDeviceModule {
public:
    DiagnosticAudioDeviceModule(
        webrtc::scoped_refptr<webrtc::AudioDeviceModule> impl,
        std::function<void(const std::string&)> report,
        std::function<void()> released,
        std::shared_ptr<SpeakerphoneGainState> speakerphoneGainState)
        : tgcalls::DefaultWrappedAudioDeviceModule(impl)
        , _raw(impl.get())
        , _report(std::move(report))
        , _released(std::move(released))
        , _speakerphoneGainState(std::move(speakerphoneGainState)) {
    }

    ~DiagnosticAudioDeviceModule() {
        // This runs only once every tgcalls owner has released the wrapper, which on the
        // normal path is MediaManager's destructor. That makes it the first moment the
        // platform module is provably idle, so terminating here hands the capture
        // endpoint back deterministically instead of leaving it to a later refcount drop.
        if (_raw != nullptr) {
            // The platform module still holds a raw pointer to the playout probe, and the
            // probe is a member that is destroyed before the base class releases the
            // module. Detach it first so that ordering can never leave a dangling callback.
            if (_playoutProbe) {
                const auto cleared = Guarded(
                    "unregister_audio_callback",
                    static_cast<AdmAudioCallbackMethod>(&webrtc::AudioDeviceModule::RegisterAudioCallback),
                    nullptr);
                ReleaseProbe(cleared == 0);
            }

            Guarded("terminate_release", &webrtc::AudioDeviceModule::Terminate);
        }

        if (_released) {
            _released();
        }
    }

    int32_t Init() override {
        std::lock_guard<std::mutex> lock(_initializationMutex);
        if (_initialized) {
            Report("step=init;phase=already_initialized;faulted=0;code=0");
            return 0;
        }

        const auto result = Guarded("init", &webrtc::AudioDeviceModule::Init);
        if (result == 0) {
            _initialized = true;
        }
        return result;
    }

    int32_t Terminate() override {
        std::lock_guard<std::mutex> lock(_initializationMutex);
        const auto result = Guarded("terminate", &webrtc::AudioDeviceModule::Terminate);
        if (result == 0) {
            _initialized = false;
        }
        return result;
    }
    int32_t InitSpeaker() override { return Guarded("init_speaker", &webrtc::AudioDeviceModule::InitSpeaker); }
    int32_t InitMicrophone() override { return Guarded("init_microphone", &webrtc::AudioDeviceModule::InitMicrophone); }
    int32_t InitPlayout() override { return Guarded("init_playout", &webrtc::AudioDeviceModule::InitPlayout); }
    int32_t InitRecording() override {
        std::lock_guard<std::mutex> lock(_recordingInitializationMutex);
        if (_recordingInitializationFailed) {
            // On the Idol 4S the platform module consistently reports that recording
            // initialization is unavailable. Retrying the same native path later can
            // fault asynchronously, while it cannot make recording available without
            // a device change, so preserve the prior failure for this call.
            Report("step=init_recording;phase=skipped_after_failure;faulted=0;code=-1");
            return -1;
        }

        const auto result = Guarded("init_recording", &webrtc::AudioDeviceModule::InitRecording);
        if (result != 0) {
            const auto platformStage =
                InterlockedCompareExchange(&g_unigram_webrtc_capture_init_stage, 0, 0);
            const auto platformHresult =
                InterlockedCompareExchange(&g_unigram_webrtc_capture_init_hresult, 0, 0);
            char hresult[11] = {};
            sprintf_s(
                hresult,
                "0x%08lX",
                static_cast<unsigned long>(platformHresult));
            Report(
                "step=init_recording;phase=platform_failure;stage=" +
                std::to_string(platformStage) +
                ";hresult=" + hresult);
            _recordingInitializationFailed = true;
        }
        return result;
    }
    int32_t StartPlayout() override {
        return Guarded("start_playout", &webrtc::AudioDeviceModule::StartPlayout);
    }

    /// <summary>
    /// Installs the playout probe between WebRTC and the device. The probe outlives the
    /// registration because the device module keeps calling it until a later
    /// registration replaces it, so it is owned here rather than by the caller.
    /// </summary>
    int32_t RegisterAudioCallback(webrtc::AudioTransport* audioCallback) override {
        if (audioCallback == nullptr) {
            const auto cleared = Guarded(
                "register_audio_callback",
                static_cast<AdmAudioCallbackMethod>(&webrtc::AudioDeviceModule::RegisterAudioCallback),
                nullptr);
            ReleaseProbe(cleared == 0);
            return cleared;
        }

        auto probe = std::make_unique<PlayoutProbeAudioTransport>(
            audioCallback,
            _speakerphoneGainState);
        const auto result = Guarded(
            "register_audio_callback",
            static_cast<AdmAudioCallbackMethod>(&webrtc::AudioDeviceModule::RegisterAudioCallback),
            probe.get());

        if (result == 0) {
            // The module only drops the previous pointer once it accepts a new one, so
            // the old probe is safe to free exactly here.
            ReleaseProbe(true);
            _playoutProbe = std::move(probe);
        }

        return result;
    }
    int32_t StopPlayout() override { return Guarded("stop_playout", &webrtc::AudioDeviceModule::StopPlayout); }
    int32_t StartRecording() override {
        return Guarded("start_recording", &webrtc::AudioDeviceModule::StartRecording);
    }
    int32_t StopRecording() override {
        return Guarded("stop_recording", &webrtc::AudioDeviceModule::StopRecording);
    }

    int32_t PlayoutIsAvailable(bool* available) override {
        return Guarded("playout_available", &webrtc::AudioDeviceModule::PlayoutIsAvailable, available);
    }

    int32_t RecordingIsAvailable(bool* available) override {
        return Guarded("recording_available", &webrtc::AudioDeviceModule::RecordingIsAvailable, available);
    }

    // Both overloads are declared so the selected endpoint is visible in diagnostics;
    // playout can run successfully against the wrong endpoint and simply be inaudible.
    int32_t SetPlayoutDevice(uint16_t index) override {
        Report("step=set_playout_device;phase=select;index=" + std::to_string(index));
        return Guarded(
            "set_playout_device",
            static_cast<AdmDeviceIndexMethod>(&webrtc::AudioDeviceModule::SetPlayoutDevice),
            index);
    }

    int32_t SetRecordingDevice(uint16_t index) override {
        Report("step=set_recording_device;phase=select;index=" + std::to_string(index));
        return Guarded(
            "set_recording_device",
            static_cast<AdmDeviceIndexMethod>(&webrtc::AudioDeviceModule::SetRecordingDevice),
            index);
    }

    int32_t SetPlayoutDevice(WindowsDeviceType device) override {
        Report("step=set_playout_device;phase=select;type=" + std::to_string(static_cast<int>(device)));
        return Guarded(
            "set_playout_device",
            static_cast<AdmWindowsDeviceMethod>(&webrtc::AudioDeviceModule::SetPlayoutDevice),
            device);
    }

    int32_t SetRecordingDevice(WindowsDeviceType device) override {
        Report("step=set_recording_device;phase=select;type=" + std::to_string(static_cast<int>(device)));
        return Guarded(
            "set_recording_device",
            static_cast<AdmWindowsDeviceMethod>(&webrtc::AudioDeviceModule::SetRecordingDevice),
            device);
    }

    int32_t StereoPlayoutIsAvailable(bool* available) const override {
        return GuardedConst("stereo_playout_available", &webrtc::AudioDeviceModule::StereoPlayoutIsAvailable, available);
    }

    int32_t StereoRecordingIsAvailable(bool* available) const override {
        return GuardedConst("stereo_recording_available", &webrtc::AudioDeviceModule::StereoRecordingIsAvailable, available);
    }

    int32_t SetStereoPlayout(bool enable) override {
        return Guarded("set_stereo_playout", &webrtc::AudioDeviceModule::SetStereoPlayout, enable);
    }

    int32_t SetStereoRecording(bool enable) override {
        return Guarded("set_stereo_recording", &webrtc::AudioDeviceModule::SetStereoRecording, enable);
    }

    int32_t SetSpeakerVolume(uint32_t volume) override {
        Report("step=set_speaker_volume;phase=select;level=" + std::to_string(volume));
        return Guarded(
            "set_speaker_volume",
            static_cast<AdmVolumeInMethod>(&webrtc::AudioDeviceModule::SetSpeakerVolume),
            volume);
    }

private:
    /// <summary>
    /// Frees the probe only when the device module has provably stopped pointing at it.
    /// A refused or faulted unregister leaves the module holding the address, so the
    /// object is deliberately leaked rather than freed under a live render thread. The
    /// leak is bounded by one small object per abnormal teardown.
    /// </summary>
    void ReleaseProbe(bool detached) {
        if (!_playoutProbe) {
            return;
        }

        if (detached) {
            _playoutProbe.reset();
            return;
        }

        _report("step=release_audio_probe;phase=leaked");
        _playoutProbe.release();
    }

    template <typename Method, typename... Args>
    int32_t Guarded(const char* step, Method method, Args... arguments) const {
        AudioDeviceLifecycleScope scope(step);
        InflightStepScope inflight(step, "report_begin");
        Report(std::string("step=") + step + ";phase=begin");
        int faulted = 0;
        SetInflightStep(step, "native");
        const auto result = InvokeGuarded(_raw, method, arguments..., &faulted);
        SetInflightStep(step, "report_end");
        Report(std::string("step=") + step + ";phase=end;faulted=" + std::to_string(faulted) +
            ";code=" + std::to_string(result));
        return result;
    }

    template <typename Method, typename... Args>
    int32_t GuardedConst(const char* step, Method method, Args... arguments) const {
        AudioDeviceLifecycleScope scope(step);
        InflightStepScope inflight(step, "report_begin");
        Report(std::string("step=") + step + ";phase=begin");
        int faulted = 0;
        SetInflightStep(step, "native");
        const auto result = InvokeGuarded(
            static_cast<const webrtc::AudioDeviceModule*>(_raw), method, arguments..., &faulted);
        SetInflightStep(step, "report_end");
        Report(std::string("step=") + step + ";phase=end;faulted=" + std::to_string(faulted) +
            ";code=" + std::to_string(result));
        return result;
    }

    void Report(const std::string& value) const {
        if (_report) {
            _report(value);
        }
    }

    webrtc::AudioDeviceModule* _raw;
    std::function<void(const std::string&)> _report;
    std::function<void()> _released;
    std::shared_ptr<SpeakerphoneGainState> _speakerphoneGainState;
    std::unique_ptr<PlayoutProbeAudioTransport> _playoutProbe;
    std::mutex _initializationMutex;
    std::mutex _recordingInitializationMutex;
    bool _initialized = false;
    bool _recordingInitializationFailed = false;
};
std::string ToUtf8(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }

    const auto length = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (length == 0) {
        throw std::invalid_argument("The call configuration contains invalid UTF-16 text.");
    }

    std::string result(length, '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            length,
            nullptr,
            nullptr) == 0) {
        throw std::invalid_argument("The call configuration contains invalid UTF-16 text.");
    }

    return result;
}

tgcalls::NetworkType ToTgCallsNetworkType(NetworkType value) {
    return static_cast<tgcalls::NetworkType>(value);
}

CallState ToFacadeState(tgcalls::State value) {
    return static_cast<CallState>(value);
}

RemoteAudioState ToFacadeAudioState(tgcalls::AudioState value) {
    return value == tgcalls::AudioState::Active
        ? RemoteAudioState::Active
        : RemoteAudioState::Muted;
}

}

void SetCameraCaptureInflightStage(const char* stage) {
    SetInflightStep("camera_capture", stage);
    if (g_inflightMarker != nullptr) {
        FlushViewOfFile(const_cast<char*>(g_inflightMarker), kInflightMarkerSize);
    }
}

void ClearCameraCaptureInflightStage() {
    ClearInflightStep();
    if (g_inflightMarker != nullptr) {
        FlushViewOfFile(const_cast<char*>(g_inflightMarker), kInflightMarkerSize);
    }
}

enum class StopWaitResult {
    Drained,
    Timeout,
    Reentrant,
};

class CallSession final : public std::enable_shared_from_this<CallSession> {
public:
    static CallSessionPtr Create(
        const CallConfiguration& configuration,
        CallCallbacks callbacks) {
        if (configuration.version.empty()) {
            throw std::invalid_argument("A TgCalls protocol version is required.");
        }
        if (configuration.encryptionKey.size() != tgcalls::EncryptionKey::kSize) {
            throw std::invalid_argument("The TgCalls encryption key must contain exactly 256 bytes.");
        }

        return CallSessionPtr(new CallSession(configuration, std::move(callbacks)));
    }

    ~CallSession() {
        // Backstop for a session abandoned without a stop ever being requested. The
        // normal paths disarm in Stop()/ForceRelease(), because tgcalls holds a strong
        // reference in its stop completion and destruction can therefore happen an
        // unspecified time later on one of its own threads.
        DisarmFaultReporting();
    }

    void Start() {
        std::lock_guard<std::mutex> lock(_mutex);
        if (_started) {
            throw std::logic_error("The TgCalls session has already started.");
        }

        tgcalls::Register<tgcalls::InstanceImpl>();
        tgcalls::Register<tgcalls::InstanceV2Impl>();
        tgcalls::Register<tgcalls::InstanceV2ReferenceImpl>();
        if (_configuration.isVideo && !_configuration.cameraDeviceId.empty()) {
            SetCameraDeviceOrientationHint(
                ToUtf8(_configuration.cameraDeviceId),
                _configuration.cameraIsFront);
        }

        // Counters are global to the process, so they are zeroed per call; otherwise a
        // previous call's totals would be read as this one's.
        ResetIncomingAudioCounters();
        _earpiecePlayoutDeviceIndex.store(-1, std::memory_order_relaxed);
        _speakerPlayoutDeviceIndex.store(-1, std::memory_order_relaxed);

        auto encryptionKey = std::make_shared<std::array<uint8_t, tgcalls::EncryptionKey::kSize>>();
        std::copy(
            _configuration.encryptionKey.begin(),
            _configuration.encryptionKey.end(),
            encryptionKey->begin());
        auto endpoints = std::vector<tgcalls::Endpoint>{};
        endpoints.reserve(_configuration.endpoints.size());
        for (const auto& endpoint : _configuration.endpoints) {
            if (endpoint.peerTag.size() != 16) {
                throw std::invalid_argument("Every reflector endpoint peer tag must contain exactly 16 bytes.");
            }

            auto target = tgcalls::Endpoint{};
            target.endpointId = endpoint.id;
            target.host.ipv4 = ToUtf8(endpoint.ipv4);
            target.host.ipv6 = ToUtf8(endpoint.ipv6);
            target.port = endpoint.port;
            target.type = endpoint.isTcp
                ? tgcalls::EndpointType::TcpRelay
                : tgcalls::EndpointType::UdpRelay;
            std::copy(endpoint.peerTag.begin(), endpoint.peerTag.end(), target.peerTag);
            endpoints.push_back(std::move(target));
        }

        auto rtcServers = std::vector<tgcalls::RtcServer>{};
        rtcServers.reserve(_configuration.rtcServers.size());
        for (const auto& server : _configuration.rtcServers) {
            auto target = tgcalls::RtcServer{};
            target.id = server.id;
            target.host = ToUtf8(server.host);
            target.port = server.port;
            target.login = ToUtf8(server.username);
            target.password = ToUtf8(server.password);
            target.isTurn = server.isTurn;
            target.isTcp = server.isTcp;
            rtcServers.push_back(std::move(target));
        }

        const auto weak = std::weak_ptr<CallSession>(shared_from_this());
        if (_configuration.isVideo) {
            _videoCapture = tgcalls::VideoCaptureInterface::Create(
                tgcalls::StaticThreads::getThreads(),
                ToUtf8(_configuration.cameraDeviceId));
            if (!_videoCapture) {
                throw std::runtime_error("The UWP camera capture could not be created.");
            }
            _videoCapture->setOnFatalError([weak] {
                if (const auto strong = weak.lock()) {
                    strong->VideoCaptureFailed();
                }
            });
        }

        auto descriptor = tgcalls::Descriptor{
            .version = ToUtf8(_configuration.version),
            .config = {
                .initializationTimeout = _configuration.initializationTimeout,
                .receiveTimeout = _configuration.receiveTimeout,
                .enableP2P = _configuration.enableP2P,
                .allowTCP = _configuration.allowTcp,
                .enableAEC = true,
                .enableNS = true,
                .enableAGC = true,
                .enableVolumeControl = false,
                .maxApiLayer = _configuration.maxApiLayer,
            },
            .endpoints = std::move(endpoints),
            .rtcServers = std::move(rtcServers),
            .initialNetworkType = ToTgCallsNetworkType(_configuration.initialNetworkType),
            .encryptionKey = tgcalls::EncryptionKey(encryptionKey, _configuration.isOutgoing),
            .videoCapture = _videoCapture,
            .stateUpdated = [weak](tgcalls::State state) {
                if (const auto strong = weak.lock()) {
                    strong->StateChanged(ToFacadeState(state));
                }
            },
            .signalBarsUpdated = [weak](int bars) {
                if (const auto strong = weak.lock()) {
                    strong->SignalBarsChanged(bars);
                }
            },
            .audioLevelUpdated = [weak](float level) {
                if (const auto strong = weak.lock()) {
                    strong->AudioLevelChanged(level);
                }
            },
            .remoteMediaStateUpdated = [weak](tgcalls::AudioState audio, tgcalls::VideoState video) {
                if (const auto strong = weak.lock()) {
                    strong->RemoteAudioStateChanged(ToFacadeAudioState(audio));
                    strong->RemoteVideoStateChanged(static_cast<VideoState>(video));
                }
            },
            .signalingDataEmitted = [weak](const std::vector<uint8_t>& data) {
                if (const auto strong = weak.lock()) {
                    strong->SignalingData(data);
                }
            },
            .createAudioDeviceModule = [weak](webrtc::TaskQueueFactory* factory)
                    -> webrtc::scoped_refptr<webrtc::AudioDeviceModule> {
                // MediaManager falls back to the same platform default when this returns
                // null, so creating it here changes no behaviour; it exists purely to make
                // an audio device failure observable instead of silently muting the call.
                auto report = std::string("created=0");
                auto playoutDeviceIndices = PlayoutDeviceIndices{};
                auto speakerphoneGainState = std::shared_ptr<SpeakerphoneGainState>{};
                if (const auto strong = weak.lock()) {
                    speakerphoneGainState = strong->_speakerphoneGainState;
                }
                auto module = webrtc::AudioDeviceModule::Create(
                    webrtc::AudioDeviceModule::kPlatformDefaultAudio,
                    factory);
                webrtc::scoped_refptr<webrtc::AudioDeviceModule> result = module;
                if (module) {
                    result = rtc::make_ref_counted<DiagnosticAudioDeviceModule>(
                        module,
                        [weak](const std::string& step) {
                            if (const auto strong = weak.lock()) {
                                strong->AudioDeviceReport(step);
                            }
                        },
                        [weak]() {
                            if (const auto strong = weak.lock()) {
                                strong->NotifyAudioDeviceReleased();
                            }
                        },
                        std::move(speakerphoneGainState));

                    // TgCalls calls Init more than once during media setup. Initializing
                    // through the idempotent wrapper keeps the platform capture module to
                    // one recording-thread initialization instead of pre-initializing the
                    // raw module and then repeating it in each TgCalls phase.
                    const auto initialized = result->Init();
                    report = "created=1;init=" + std::to_string(initialized);
                    if (initialized == 0) {
                        const auto playoutDevices = result->PlayoutDevices();
                        report += ";playout_devices=" + std::to_string(playoutDevices);
                        report += ";recording_devices=" + std::to_string(result->RecordingDevices());
                        report += ";playout_names=" + DescribePlayoutDevices(
                            result.get(),
                            playoutDevices,
                            &playoutDeviceIndices);
                    }
                }

                if (const auto strong = weak.lock()) {
                    // Retain the wrapper, not the platform module underneath it: the
                    // wrapper is the reference tgcalls holds too, so dropping ours lets
                    // MediaManager's destructor own the last one, and the wrapper's own
                    // destruction is what signals that the capture endpoint is free.
                    strong->RetainAudioDeviceModule(result);
                    strong->SetPlayoutDeviceIndices(
                        playoutDeviceIndices.earpiece,
                        playoutDeviceIndices.speaker);
                    strong->AudioDeviceReport(report);
                }
                return result;
            },
        };

        _instance = tgcalls::Meta::Create(descriptor.version, std::move(descriptor));
        if (!_instance) {
            throw std::invalid_argument("The requested TgCalls protocol version is not registered.");
        }

        _started = true;
        // From here the engine owns live media threads, so the fault reporter stays armed
        // until teardown rather than only while an audio device call is on the stack.
        RenewFaultBudget();
        InterlockedIncrement(&g_callActiveDepth);
        _faultReportingArmed = true;
    }

    void ReceiveSignalingData(std::vector<uint8_t> data) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        _instance->receiveSignalingData(data);
    }

    void SetMuted(bool value) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        _instance->setMuteMicrophone(value);
    }

    bool SupportsVideo() {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        return _instance->supportsVideo();
    }

    void SetVideoState(VideoState state) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        if (!_videoCapture) {
            throw std::logic_error("The TgCalls session has no video capture.");
        }
        _videoCapture->setState(static_cast<tgcalls::VideoState>(state));
    }

    void SwitchVideoCaptureDevice(const std::wstring& deviceId, bool isFrontCamera) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        if (!_videoCapture) {
            throw std::logic_error("The TgCalls session has no video capture.");
        }

        const auto selector = ToUtf8(deviceId);
        const auto weak = std::weak_ptr<CallSession>(shared_from_this());
        _videoCapture->withNativeImplementation([weak, selector, isFrontCamera](void* implementation) {
            const auto capture = static_cast<UwpCameraCaptureControl*>(implementation);
            const auto succeeded =
                capture != nullptr && capture->SwitchToDevice(selector, isFrontCamera);
            if (const auto strong = weak.lock()) {
                strong->VideoCaptureSwitchCompleted(succeeded);
            }
        });
    }

    void SetVideoOutput(bool local, VideoOutputPtr output) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        auto& installed = local ? _localVideoOutput : _remoteVideoOutput;
        if (installed == output) {
            return;
        }

        if (local) {
            if (!_videoCapture) {
                throw std::logic_error("The TgCalls session has no video capture.");
            }
            _videoCapture->setOutput(output);
        } else {
            _instance->setIncomingVideoOutput(output);
        }

        installed = std::move(output);
    }

    std::string SetAudioOutputEndpoint(bool speakerphone) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();

        const auto index = speakerphone
            ? _speakerPlayoutDeviceIndex.load(std::memory_order_acquire)
            : _earpiecePlayoutDeviceIndex.load(std::memory_order_acquire);
        const auto target = speakerphone ? "speakerphone" : "earpiece";
        _speakerphoneGainState->speakerphoneRequested.store(speakerphone, std::memory_order_release);
        if (index < 0) {
            return std::string("unavailable;target=") + target;
        }

        // This is the same API path current upstream Unigram uses for a selected
        // AudioOutputId. The '#<index>' selector is resolved by TgCalls on its media
        // queue, which stops and restarts playout around the device switch.
        _instance->setAudioOutputDevice("#" + std::to_string(index));
        return std::string("queued;target=") + target + ";index=" + std::to_string(index) +
            (speakerphone ? ";gain=software_cap_pending" : ";gain=software_restore");
    }

    void SetNetworkType(NetworkType value) {
        std::lock_guard<std::mutex> lock(_mutex);
        EnsureActive();
        _instance->setNetworkType(ToTgCallsNetworkType(value));
    }

    /// <summary>
    /// Reads the live recording and playout state of the retained audio device module.
    /// Reported as fixed keys with boolean values only.
    /// </summary>
    std::string AudioDeviceStatus() {
        webrtc::scoped_refptr<webrtc::AudioDeviceModule> module;
        {
            std::lock_guard<std::mutex> lock(_audioDeviceMutex);
            module = _audioDeviceModule;
        }

        // Emitted first, and on both exits. The managed sanitizer caps the status string,
        // and these counters are the evidence the rest of the line exists to contextualise,
        // so they must not be the fields that truncation drops. A missing audio device is
        // also exactly when it matters whether RTP is arriving at all, because that is what
        // separates "the device never came up" from "it came up and nothing feeds it".
        const std::string counters = DescribeIncomingAudioCounters();

        if (!module) {
            return counters + ";created=0";
        }

        std::string status = counters + ";created=1;recording=" + (module->Recording() ? "1" : "0") +
            ";playing=" + (module->Playing() ? "1" : "0");

        if (_speakerphoneGainState->speakerphoneRequested.load(std::memory_order_acquire)) {
            status += ";speaker_gain=software_cap;buffers=" + std::to_string(
                _speakerphoneGainState->attenuatedBuffers.load(std::memory_order_relaxed));
        } else {
            status += ";speaker_gain=software_restore";
        }

        // The decisive field: whether WebRTC is handing the device non-silent samples.
        // A zero peak with a healthy device points upstream, at decode or the network;
        // a non-zero peak with nothing audible points at the device or the routing.
        int permille = 0;
        uint32_t rate = 0;
        uint64_t windows = 0;
        if (PlayoutProbeAudioTransport::TryReadLevel(&permille, &rate, &windows)) {
            status += ";playout_peak_permille=" + std::to_string(permille) +
                ";playout_rate=" + std::to_string(rate) +
                ";playout_windows=" + std::to_string(windows);
        } else {
            status += ";playout_peak_permille=pending";
        }

        const auto captureWindows = g_captureWindows.load(std::memory_order_acquire);
        if (captureWindows != 0) {
            status += ";capture_peak_permille=" +
                std::to_string(g_capturePeakPermille.load(std::memory_order_relaxed)) +
                ";capture_rate=" +
                std::to_string(g_captureSampleRate.load(std::memory_order_relaxed)) +
                ";capture_windows=" + std::to_string(captureWindows);
        } else {
            status += ";capture_peak_permille=pending";
        }

        return status;
    }

    void Stop() {
        tgcalls::Instance* instance = nullptr;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            if (_stopping) {
                return;
            }
            _stopping = true;
            instance = _started ? _instance.get() : nullptr;
            if (instance == nullptr) {
                _stopped = true;
            }
        }

        if (instance == nullptr) {
            DropAudioDeviceModule();
            DisarmFaultReporting();
            _stoppedCondition.notify_all();
            return;
        }

        const auto self = shared_from_this();
        instance->stop([self](tgcalls::FinalState) {
            self->_completionThreadId.store(::GetCurrentThreadId());

            CallCallbacks callbacks;
            {
                std::lock_guard<std::mutex> completionLock(self->_mutex);
                // This only queues the tgcalls teardown. ~Manager and then ~MediaManager
                // run later on their own threads, and ~MediaManager is what actually
                // stops the audio channels and releases the device.
                self->_instance.reset();
                callbacks = self->_callbacks;
            }

            // Drop this session's reference so tgcalls' own teardown holds the last one.
            // Stopping or terminating the device here would race MediaManager, which is
            // still streaming into it from inside this very callback.
            self->DropAudioDeviceModule();

            // Raised before the stop is published so a handler cannot wake the disposing
            // thread and have it destroy the owning object while this callback is still
            // on its stack.
            if (callbacks.stopped) {
                callbacks.stopped(true);
            }

            {
                std::lock_guard<std::mutex> completionLock(self->_mutex);
                self->_stopped = true;
            }
            self->_completionThreadId.store(0);
            // Closes the fault reporting window at a defined point. Waiting for the
            // session to be destroyed would leave it open until tgcalls released the
            // strong reference captured here, which happens at an unspecified later
            // time on one of its own threads.
            self->DisarmFaultReporting();
            self->_stoppedCondition.notify_all();
        });
    }

    /// <summary>
    /// Waits until the session has stopped and, when one was created, the audio device
    /// has actually been destroyed. The device is the resource the next call contends
    /// for, so waiting on the stop alone would still let the two overlap.
    /// </summary>
    StopWaitResult WaitForStop(int timeoutMilliseconds) {
        // The stop completion raises the stopped callback, so a handler that disposes the
        // call re-enters here on the completion thread. Waiting there would block the one
        // thread that still has to publish the result.
        if (_completionThreadId.load() == ::GetCurrentThreadId()) {
            return StopWaitResult::Reentrant;
        }

        std::unique_lock<std::mutex> lock(_mutex);
        const auto settled = [this] {
            return _stopped && (!_audioDeviceCreated.load() || _audioDeviceReleased.load());
        };

        if (settled()) {
            return StopWaitResult::Drained;
        }
        if (timeoutMilliseconds <= 0) {
            return StopWaitResult::Timeout;
        }

        const auto drained = _stoppedCondition.wait_for(
            lock,
            std::chrono::milliseconds(timeoutMilliseconds),
            settled);
        return drained ? StopWaitResult::Drained : StopWaitResult::Timeout;
    }

    /// <summary>
    /// Last resort for a stop completion that never arrives: tgcalls drops it silently
    /// when the manager is already gone. Resetting the instance here starts the same
    /// deferred teardown the completion would have started, so the audio device is not
    /// pinned for the lifetime of the process.
    /// </summary>
    void ForceRelease() {
        {
            std::lock_guard<std::mutex> lock(_mutex);
            _instance.reset();
            _stopped = true;
        }

        DropAudioDeviceModule();
        DisarmFaultReporting();
        _stoppedCondition.notify_all();
    }

    void NotifyAudioDeviceReleased() {
        _audioDeviceReleased.store(true);
        {
            std::lock_guard<std::mutex> lock(_mutex);
        }
        _stoppedCondition.notify_all();
    }

private:
    explicit CallSession(
        const CallConfiguration& configuration,
        CallCallbacks callbacks)
        : _configuration(configuration), _callbacks(std::move(callbacks)) {
    }

    void EnsureActive() const {
        if (!_started || !_instance || _stopping) {
            throw std::logic_error("The TgCalls session is not active.");
        }
    }

    void StateChanged(CallState state) {
        if (_callbacks.stateChanged) {
            _callbacks.stateChanged(state);
        }
    }

    void SignalingData(const std::vector<uint8_t>& data) {
        if (_callbacks.signalingData) {
            _callbacks.signalingData(data);
        }
    }

    void SignalBarsChanged(int bars) {
        if (_callbacks.signalBarsChanged) {
            _callbacks.signalBarsChanged(bars);
        }
    }

    void AudioLevelChanged(float level) {
        if (_callbacks.audioLevelChanged) {
            _callbacks.audioLevelChanged(level);
        }
    }

    void RemoteAudioStateChanged(RemoteAudioState state) {
        if (_callbacks.remoteAudioStateChanged) {
            _callbacks.remoteAudioStateChanged(state);
        }
    }

    void RemoteVideoStateChanged(VideoState state) {
        if (_callbacks.remoteVideoStateChanged) {
            _callbacks.remoteVideoStateChanged(state);
        }
    }

    void VideoCaptureFailed() {
        if (_callbacks.videoCaptureFailed) {
            _callbacks.videoCaptureFailed();
        }
    }

    void VideoCaptureSwitchCompleted(bool succeeded) {
        if (_callbacks.videoCaptureSwitchCompleted) {
            _callbacks.videoCaptureSwitchCompleted(succeeded);
        }
    }

    void AudioDeviceReport(const std::string& report) {
        if (_callbacks.audioDeviceReport) {
            _callbacks.audioDeviceReport(report);
        }
    }

    void RetainAudioDeviceModule(webrtc::scoped_refptr<webrtc::AudioDeviceModule> module) {
        _audioDeviceCreated.store(module != nullptr);
        std::lock_guard<std::mutex> lock(_audioDeviceMutex);
        _audioDeviceModule = std::move(module);
    }

    void SetPlayoutDeviceIndices(int earpiece, int speaker) {
        _earpiecePlayoutDeviceIndex.store(earpiece, std::memory_order_release);
        _speakerPlayoutDeviceIndex.store(speaker, std::memory_order_release);
    }

    /// <summary>
    /// Releases only this session's reference. The device is deliberately not stopped or
    /// terminated here: tgcalls still owns a reference and is streaming into it, so the
    /// shutdown has to stay with MediaManager's destructor.
    /// </summary>
    void DropAudioDeviceModule() {
        webrtc::scoped_refptr<webrtc::AudioDeviceModule> module;
        {
            std::lock_guard<std::mutex> lock(_audioDeviceMutex);
            module = std::move(_audioDeviceModule);
            _audioDeviceModule = nullptr;
        }
    }

    // Closes the whole-call fault reporting window exactly once, whichever teardown path
    // reaches it first.
    void DisarmFaultReporting() {
        if (_faultReportingArmed.exchange(false)) {
            InterlockedDecrement(&g_callActiveDepth);
            ClearFaultSlot();
        }
    }

    std::mutex _mutex;
    std::mutex _audioDeviceMutex;
    std::condition_variable _stoppedCondition;
    std::atomic<unsigned long> _completionThreadId{0};
    std::atomic<bool> _audioDeviceCreated{false};
    std::atomic<bool> _audioDeviceReleased{false};
    std::atomic<bool> _faultReportingArmed{false};
    std::atomic<int> _earpiecePlayoutDeviceIndex{-1};
    std::atomic<int> _speakerPlayoutDeviceIndex{-1};
    webrtc::scoped_refptr<webrtc::AudioDeviceModule> _audioDeviceModule;
    std::shared_ptr<SpeakerphoneGainState> _speakerphoneGainState = std::make_shared<SpeakerphoneGainState>();
    CallConfiguration _configuration;
    std::shared_ptr<tgcalls::VideoCaptureInterface> _videoCapture;
    VideoOutputPtr _localVideoOutput;
    VideoOutputPtr _remoteVideoOutput;
    std::unique_ptr<tgcalls::Instance> _instance;
    CallCallbacks _callbacks;
    bool _started = false;
    bool _stopping = false;
    bool _stopped = false;
};

const wchar_t* GetFirstSupportedVersion() {
    static const std::wstring version = GetSupportedVersions().front();
    return version.c_str();
}

void EnableCrashDiagnostics(const std::wstring& diagnosticsFilePath) {
    if (diagnosticsFilePath.empty() || diagnosticsFilePath.size() >= MAX_PATH) {
        return;
    }

    std::memcpy(
        g_crashDiagnosticsPath,
        diagnosticsFilePath.c_str(),
        (diagnosticsFilePath.size() + 1) * sizeof(wchar_t));
    InitOnceExecuteOnce(&g_crashDiagnosticsOnce, InstallFatalExceptionReporter, nullptr, nullptr);
}

std::vector<std::wstring> GetSupportedVersions() {
    static const std::vector<std::wstring> versions = [] {
        tgcalls::Register<tgcalls::InstanceImpl>();
        const auto registeredVersions = tgcalls::Meta::Versions();
        auto result = std::vector<std::wstring>{};
        result.reserve(registeredVersions.size());
        for (const auto& version : registeredVersions) {
            result.emplace_back(version.begin(), version.end());
        }
        return result;
    }();

    return versions;
}

CallSessionPtr CreateCallSession(
    const CallConfiguration& configuration,
    CallCallbacks callbacks) {
    return CallSession::Create(configuration, std::move(callbacks));
}

void StartCallSession(const CallSessionPtr& session) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->Start();
}

void ReceiveSignalingData(const CallSessionPtr& session, std::vector<uint8_t> data) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->ReceiveSignalingData(std::move(data));
}

void SetMuted(const CallSessionPtr& session, bool value) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->SetMuted(value);
}

bool SupportsVideo(const CallSessionPtr& session) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    return session->SupportsVideo();
}

void SetVideoState(const CallSessionPtr& session, VideoState state) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->SetVideoState(state);
}

void SwitchVideoCaptureDevice(
    const CallSessionPtr& session,
    const std::wstring& deviceId,
    bool isFrontCamera) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->SwitchVideoCaptureDevice(deviceId, isFrontCamera);
}

void SetVideoOutput(const CallSessionPtr& session, bool local, VideoOutputPtr output) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->SetVideoOutput(local, std::move(output));
}

std::string SetAudioOutputEndpoint(const CallSessionPtr& session, bool speakerphone) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    return session->SetAudioOutputEndpoint(speakerphone);
}

void SetNetworkType(const CallSessionPtr& session, NetworkType value) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->SetNetworkType(value);
}

std::string GetAudioDeviceStatus(const CallSessionPtr& session) {
    if (!session) {
        return "created=0";
    }
    return session->AudioDeviceStatus();
}

void StopCallSession(const CallSessionPtr& session) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }
    session->Stop();
}

std::string StopCallSessionAndWait(const CallSessionPtr& session, int timeoutMilliseconds) {
    if (!session) {
        throw std::invalid_argument("The TgCalls session is unavailable.");
    }

    session->Stop();
    auto result = session->WaitForStop(timeoutMilliseconds);
    if (result == StopWaitResult::Timeout) {
        // tgcalls drops its stop completion silently when the manager has already gone,
        // which would otherwise pin the audio device for the lifetime of the process and
        // hand the next call a microphone this session still owns.
        session->ForceRelease();

        // Only a short grace period: the teardown has already overrun its budget, so the
        // point here is to confirm the forced release landed, not to wait again.
        constexpr int kForcedReleaseGraceMilliseconds = 500;
        result = session->WaitForStop(kForcedReleaseGraceMilliseconds);
    }

    switch (result) {
    case StopWaitResult::Drained:
        return "drained";
    case StopWaitResult::Reentrant:
        return "reentrant";
    default:
        return "timeout";
    }
}

}
}
}
