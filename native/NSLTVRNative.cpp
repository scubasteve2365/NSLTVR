// NSLTVRNative: the OpenXR side of NSLTVR (New Super Lucky's Tale VR). 64-bit, loaded into the game by the C# plugin.
// Start-up, add-on checks and HDR logging copied from CupheadVRNative (CupheadVR, MIT, scubasteve2365), itself from
// OriVRNative. The frame path is new for NSLT build 1 (standards TECHNIQUES.md "Never block the game on the headset"):
//
//   Main thread   NslXR_GetViews: the eye poses for the next display time (xrLocateViews), NslXR_CapWait: the mod's
//                 own frame cap at the headset's real rate. The game never calls xrWaitFrame.
//   Render thread render events from GL.IssuePluginEvent: start (creates everything), submit (copies this frame's eye,
//                 HUD and menu images into a free slot of a small ring of shared textures; no free slot = skipped and
//                 counted, never waited on), GPU timestamps.
//   XR thread     our own thread at highest priority, on our own D3D11 device: xrWaitFrame, xrBeginFrame, takes the
//                 newest finished slot, copies it into the runtime's swapchains, xrEndFrame. Without a new slot it
//                 submits the last images again.
//
// The OpenXR session uses the mod's own D3D11 device on the game's GPU, so nothing on the XR thread touches Unity's
// device context. Shared textures with keyed mutexes carry the images between the two devices.
#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <mmsystem.h>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#define API extern "C" __declspec(dllexport)

enum State { StateOff = 0, StateStarting = 1, StateReady = 2, StateFailed = 3 };
enum Event { EventInit = 1, EventSubmit = 2, EventShutdown = 3, EventEyeBegin = 5 };
enum Tex { TexL = 0, TexR = 1, TexHud = 2, TexMenu = 3, TexCount = 4 };
enum Flags { FlagHud = 1, FlagMenu = 2, FlagSteady = 4, FlagViewsValid = 8 };

#pragma pack(push, 4)
struct EyeView { float px, py, pz, qx, qy, qz, qw, left, right, up, down; };
struct ViewInfo {
    int running;        // the session runs
    int viewsValid;     // positions and orientations are usable
    int sessionState;   // XrSessionState
    float periodMs;     // the runtime's frame period
    long long displayTime;
    EyeView eye[2];
};
// Everything the XR thread needs to show one finished frame. Poses: centre x, y, z, rotation x, y, z, w, width, height.
struct FrameMeta {
    int flags;
    int frame;          // the game's frame number, for the log
    long long displayTime;
    EyeView eye[2];     // the poses the eyes were rendered with
    float hudPose[9];
    float menuPose[9];
};
struct Stats {
    unsigned int submitted, repeats, skipped, replaced;
    float waitAvg, waitMax, endAvg, endMax, copyAvg, copyMax, gpuEyeAvg, gpuEyeMax, gpuFrameAvg, gpuFrameMax;
};
#pragma pack(pop)

static std::mutex g_logLock;
static std::deque<std::string> g_log;
static void Log(const char* fmt, ...) {
    char buf[512];
    va_list a;
    va_start(a, fmt);
    vsnprintf(buf, sizeof(buf), fmt, a);
    va_end(a);
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.size() < 2000) g_log.push_back(buf);
}

static HMODULE g_loader;
static PFN_xrGetInstanceProcAddr g_gipa;
static XrInstance g_inst = XR_NULL_HANDLE;
static XrSystemId g_sys = 0;
static XrSession g_sess = XR_NULL_HANDLE;
static XrSpace g_space = XR_NULL_HANDLE;
static volatile LONG g_state = StateOff;
static volatile LONG g_running = 0;
static volatile LONG g_xrState = 0;
static ID3D11Device* g_dev;            // Unity's device: render thread only
static ID3D11DeviceContext* g_ctx;
static ID3D11Device* g_xdev;           // the mod's own device: XR thread (and start-up)
static ID3D11DeviceContext* g_xctx;
static ID3D11Texture2D* g_any;         // any Unity texture, to find Unity's device
static int g_recW, g_recH, g_maxW, g_maxH, g_eyeW, g_eyeH, g_panelW[TexCount], g_panelH[TexCount];
static float g_scale = 1.0f;
static int64_t g_format;
static char g_runtimeName[128], g_systemName[256];
static volatile LONG g_recenterPending = 0;
static volatile LONG g_lastXrError = 0;
static bool g_canAlphaBlend;

static PFN_xrCreateSwapchain p_createSwap;
static PFN_xrEnumerateSwapchainImages p_enumImages;
static PFN_xrDestroySwapchain p_destroySwap;
static PFN_xrPollEvent p_poll;
static PFN_xrBeginSession p_beginSession;
static PFN_xrEndSession p_endSession;
static PFN_xrWaitFrame p_waitFrame;
static PFN_xrBeginFrame p_beginFrame;
static PFN_xrEndFrame p_endFrame;
static PFN_xrLocateViews p_locate;
static PFN_xrAcquireSwapchainImage p_acquire;
static PFN_xrWaitSwapchainImage p_waitImage;
static PFN_xrReleaseSwapchainImage p_release;

template <class T> static bool GetFn(const char* name, T& out) {
    PFN_xrVoidFunction f = nullptr;
    XrResult r = g_gipa(g_inst, name, &f);
    if (XR_FAILED(r) || !f) { Log("missing OpenXR function %s (%d)", name, (int)r); return false; }
    out = (T)f;
    return true;
}

#define XRCHECK(call) do { XrResult r_ = (call); if (XR_FAILED(r_)) { g_lastXrError = (LONG)r_; Log("%s failed: %d", #call, (int)r_); return false; } } while (0)

static void HereDir(char* path, DWORD size) {
    HMODULE self = nullptr;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&HereDir, &self);
    GetModuleFileNameA(self, path, size);
    char* slash = strrchr(path, '\\');
    if (slash) slash[1] = 0;
}

static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER x; QueryPerformanceFrequency(&x); return x; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return c.QuadPart * 1000.0 / f.QuadPart;
}

// OpenXR add-ons (implicit API layers) load into every OpenXR program. One whose DLL does not match this process
// (Ori, 32-bit, met one with only a 64-bit DLL) fails the whole start with XR_ERROR_FILE_ACCESS_ERROR (-32). Before the
// loader starts, each implicit layer is checked, and any whose DLL is not 64-bit is switched off for this process only,
// with the variable the layer itself declares for that.
static std::string JsonString(const std::string& text, const char* key) {
    std::string k = std::string("\"") + key + "\"";
    size_t at = text.find(k);
    if (at == std::string::npos) return "";
    at = text.find(':', at + k.size());
    if (at == std::string::npos) return "";
    at = text.find('"', at);
    if (at == std::string::npos) return "";
    std::string out;
    for (size_t i = at + 1; i < text.size(); i++) {
        char c = text[i];
        if (c == '\\' && i + 1 < text.size()) { out += text[++i]; continue; }
        if (c == '"') return out;
        out += c;
    }
    return "";
}

// 0x14c for a 32-bit DLL, 0x8664 for 64-bit, 0 when the file cannot be read.
static unsigned short DllMachine(const std::string& path) {
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "rb") != 0 || !f) return 0;
    unsigned char dos[64];
    unsigned short machine = 0;
    if (fread(dos, 1, 64, f) == 64 && dos[0] == 'M' && dos[1] == 'Z') {
        long pe = *(long*)(dos + 60);
        unsigned char sig[6];
        if (fseek(f, pe, SEEK_SET) == 0 && fread(sig, 1, 6, f) == 6 && sig[0] == 'P' && sig[1] == 'E')
            machine = (unsigned short)(sig[4] | (sig[5] << 8));
    }
    fclose(f);
    return machine;
}

// Add-ons kept for this game, in case OpenXR fails with -32 and they have to be switched off after all (from OriVR 1.2.2:
// an add-on of the right bitness can still refuse to load, ReShade's did for an Ori player, LESSONS_LEARNED.md).
static std::vector<std::string> g_keptOffSwitches;  // their disable_environment variables
static std::string g_keptNames;                     // names with an off switch
static std::string g_stuckNames;                    // names without one
static char g_failedLayers[512];                    // for the player message when VR did not start

static void CheckLayers(HKEY root, const char* rootName) {
    HKEY key;
    if (RegOpenKeyExA(root, "SOFTWARE\\Khronos\\OpenXR\\1\\ApiLayers\\Implicit", 0, KEY_READ, &key) != ERROR_SUCCESS) return;
    for (DWORD i = 0;; i++) {
        char manifest[1024];
        DWORD nameLen = sizeof(manifest), type = 0, data = 0, dataLen = sizeof(data);
        LONG r = RegEnumValueA(key, i, manifest, &nameLen, nullptr, &type, (BYTE*)&data, &dataLen);
        if (r == ERROR_NO_MORE_ITEMS) break;
        if (r != ERROR_SUCCESS) continue;
        if (type == REG_DWORD && data != 0) { Log("OpenXR layer %s (%s): switched off in Windows, skipped", manifest, rootName); continue; }
        std::string text;
        FILE* f = nullptr;
        if (fopen_s(&f, manifest, "rb") == 0 && f) {
            char buf[4096];
            size_t n;
            while ((n = fread(buf, 1, sizeof(buf), f)) > 0) text.append(buf, n);
            fclose(f);
        }
        std::string name = JsonString(text, "name");
        std::string lib = JsonString(text, "library_path");
        std::string off = JsonString(text, "disable_environment");
        std::string full = lib;
        bool relative = !lib.empty() && !(lib.size() > 1 && lib[1] == ':') && lib[0] != '\\' && lib[0] != '/';
        if (relative) {
            std::string dir = manifest;
            size_t slash = dir.find_last_of("\\/");
            full = (slash == std::string::npos ? std::string() : dir.substr(0, slash + 1)) + lib;
        }
        unsigned short machine = full.empty() ? 0 : DllMachine(full);
        const char* kind = machine == 0x14c ? "32-bit" : machine == 0x8664 ? "64-bit" : machine == 0 ? "unreadable" : "other";
        if (machine == 0x8664) {
            Log("OpenXR layer %s (%s): 64-bit, kept", name.c_str(), rootName);
            std::string shown = name.empty() ? std::string(manifest) : name;
            if (!off.empty()) {
                g_keptOffSwitches.push_back(off);
                g_keptNames += (g_keptNames.empty() ? "" : ", ") + shown;
            } else {
                g_stuckNames += (g_stuckNames.empty() ? "" : ", ") + shown;
            }
        } else if (!off.empty()) {
            SetEnvironmentVariableA(off.c_str(), "1");
            Log("OpenXR layer %s (%s): DLL is %s, switched off for this game (%s=1). %s", name.c_str(), rootName, kind, off.c_str(), full.c_str());
        } else {
            Log("OpenXR layer %s (%s): DLL is %s and the layer has no off switch, OpenXR may fail. %s", name.c_str(), rootName, kind, full.c_str());
        }
    }
    RegCloseKey(key);
}

static HWND g_gameWindow;
static BOOL CALLBACK FindGameWindow(HWND h, LPARAM) {
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == GetCurrentProcessId() && IsWindowVisible(h) && !GetWindow(h, GW_OWNER)) { g_gameWindow = h; return FALSE; }
    return TRUE;
}

// Windows HDR for the display the game window is on. The game itself has no HDR option and the mod renders every eye,
// the menu and the HUD into its own 8-bit textures, so this is for player reports only.
static void LogDisplayHdr() {
    g_gameWindow = nullptr;
    EnumWindows(FindGameWindow, 0);
    HMONITOR mon = g_gameWindow ? MonitorFromWindow(g_gameWindow, MONITOR_DEFAULTTONEAREST) : nullptr;
    IDXGIDevice* dxgi = nullptr;
    IDXGIAdapter* adapter = nullptr;
    if (FAILED(g_dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)) || FAILED(dxgi->GetAdapter(&adapter))) {
        if (dxgi) dxgi->Release();
        Log("display HDR: unknown (no adapter)");
        return;
    }
    bool found = false;
    IDXGIOutput* out = nullptr;
    for (UINT i = 0; adapter->EnumOutputs(i, &out) == S_OK; i++) {
        IDXGIOutput6* out6 = nullptr;
        DXGI_OUTPUT_DESC1 d{};
        if (SUCCEEDED(out->QueryInterface(__uuidof(IDXGIOutput6), (void**)&out6)) && SUCCEEDED(out6->GetDesc1(&d))) {
            bool hdr = d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            bool mine = mon && d.Monitor == mon;
            Log("display %ls: Windows HDR %s (colour space %d, %u bits per colour)%s", d.DeviceName, hdr ? "on" : "off", (int)d.ColorSpace, d.BitsPerColor,
                mine ? ", the game's display" : "");
            found |= mine;
        }
        if (out6) out6->Release();
        out->Release();
    }
    if (!found) Log("display HDR: the game's display was not found among this GPU's outputs");
    Log("game HDR: NSLT has no HDR option (GraphicsSettings fields); eye, menu and HUD images are the mod's own 8-bit textures");
    adapter->Release();
    dxgi->Release();
}

// ---------------------------------------------------------------------------------------------------------------------
// Start-up

static bool StartXr() {
    char path[MAX_PATH];
    HereDir(path, MAX_PATH);
    strcat_s(path, "openxr_loader.dll");
    g_keptOffSwitches.clear();
    g_keptNames.clear();
    g_stuckNames.clear();
    g_failedLayers[0] = 0;
    g_lastXrError = 0;
    CheckLayers(HKEY_LOCAL_MACHINE, "HKLM");
    CheckLayers(HKEY_CURRENT_USER, "HKCU");
    if (!g_loader) g_loader = LoadLibraryA(path);
    if (!g_loader) { g_lastXrError = -1000; Log("could not load %s, error %lu", path, GetLastError()); return false; }
    g_gipa = (PFN_xrGetInstanceProcAddr)GetProcAddress(g_loader, "xrGetInstanceProcAddr");
    if (!g_gipa) { Log("loader has no xrGetInstanceProcAddr"); return false; }

    PFN_xrCreateInstance createInstance;
    if (!GetFn("xrCreateInstance", createInstance)) return false;
    const char* exts[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "NSLTVR");
    ici.applicationInfo.applicationVersion = 1;
    strcpy_s(ici.applicationInfo.engineName, "Unity 2017.4");
    ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = exts;
    XrResult created = createInstance(&ici, &g_inst);
    if (created == XR_ERROR_FILE_ACCESS_ERROR) {
        // -32: an add-on (API layer) could not load. Try again with every add-on that has an off switch switched off,
        // for this game only (CupheadVR 1.05, from OriVR 1.2.2).
        if (!g_keptOffSwitches.empty()) {
            Log("OpenXR could not load an add-on (-32). Trying again with these add-ons switched off for this game only: %s", g_keptNames.c_str());
            for (auto& v : g_keptOffSwitches) SetEnvironmentVariableA(v.c_str(), "1");
            created = createInstance(&ici, &g_inst);
            if (XR_SUCCEEDED(created)) Log("OpenXR started with those add-ons switched off");
            else Log("still failing with those add-ons switched off: %d", (int)created);
        }
        if (XR_FAILED(created)) {
            std::string names = g_keptNames + (g_keptNames.empty() || g_stuckNames.empty() ? "" : ", ") + g_stuckNames;
            strncpy_s(g_failedLayers, names.c_str(), _TRUNCATE);
        }
    }
    XRCHECK(created);

    PFN_xrGetInstanceProperties props;
    if (GetFn("xrGetInstanceProperties", props)) {
        XrInstanceProperties ip{XR_TYPE_INSTANCE_PROPERTIES};
        if (XR_SUCCEEDED(props(g_inst, &ip))) {
            Log("runtime %s %u.%u.%u", ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion));
            strcpy_s(g_runtimeName, ip.runtimeName);
        }
    }
    PFN_xrGetSystem getSystem;
    if (!GetFn("xrGetSystem", getSystem)) return false;
    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XRCHECK(getSystem(g_inst, &sgi, &g_sys));
    PFN_xrGetSystemProperties sysProps;
    if (GetFn("xrGetSystemProperties", sysProps)) {
        XrSystemProperties sp{XR_TYPE_SYSTEM_PROPERTIES};
        if (XR_SUCCEEDED(sysProps(g_inst, g_sys, &sp))) {
            Log("headset %s, vendor %u, largest image %ux%u", sp.systemName, sp.vendorId, sp.graphicsProperties.maxSwapchainImageWidth, sp.graphicsProperties.maxSwapchainImageHeight);
            strcpy_s(g_systemName, sp.systemName);
            g_maxW = (int)sp.graphicsProperties.maxSwapchainImageWidth;
            g_maxH = (int)sp.graphicsProperties.maxSwapchainImageHeight;
        }
    }

    // The mod's own device, on the GPU the game uses.
    PFN_xrGetD3D11GraphicsRequirementsKHR req11;
    if (!GetFn("xrGetD3D11GraphicsRequirementsKHR", req11)) return false;
    XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    XRCHECK(req11(g_inst, g_sys, &req));
    IDXGIDevice* dxgi = nullptr;
    IDXGIAdapter* adapter = nullptr;
    if (FAILED(g_dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)) || FAILED(dxgi->GetAdapter(&adapter))) { Log("no adapter for the game's device"); return false; }
    DXGI_ADAPTER_DESC ad;
    adapter->GetDesc(&ad);
    Log("game adapter %ls LUID %08lx:%08lx, runtime wants %08lx:%08lx", ad.Description, (unsigned long)ad.AdapterLuid.HighPart, ad.AdapterLuid.LowPart,
        (unsigned long)req.adapterLuid.HighPart, req.adapterLuid.LowPart);
    D3D_FEATURE_LEVEL want[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, want, 2, D3D11_SDK_VERSION, &g_xdev, nullptr, &g_xctx);
    adapter->Release();
    dxgi->Release();
    if (FAILED(hr)) { Log("could not create the mod's D3D11 device: %08lx", (unsigned long)hr); return false; }
    ID3D11Multithread* mt = nullptr;
    if (SUCCEEDED(g_xctx->QueryInterface(__uuidof(ID3D11Multithread), (void**)&mt))) { mt->SetMultithreadProtected(TRUE); mt->Release(); }

    PFN_xrCreateSession createSession;
    if (!GetFn("xrCreateSession", createSession)) return false;
    XrGraphicsBindingD3D11KHR bind{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    bind.device = g_xdev;
    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = &bind;
    sci.systemId = g_sys;
    XRCHECK(createSession(g_inst, &sci, &g_sess));
    PFN_xrCreateReferenceSpace createSpace;
    if (!GetFn("xrCreateReferenceSpace", createSpace)) return false;
    XrReferenceSpaceCreateInfo rsci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rsci.poseInReferenceSpace.orientation.w = 1.0f;
    XRCHECK(createSpace(g_sess, &rsci, &g_space));

    PFN_xrEnumerateSwapchainFormats enumFormats;
    if (!GetFn("xrEnumerateSwapchainFormats", enumFormats)) return false;
    uint32_t n = 0;
    XRCHECK(enumFormats(g_sess, 0, &n, nullptr));
    std::vector<int64_t> formats(n);
    XRCHECK(enumFormats(g_sess, n, &n, formats.data()));
    // The game draws in linear colour into sRGB textures, so an sRGB swapchain of the same typeless family takes the bytes as they are.
    for (auto f : formats) if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) g_format = f;
    if (!g_format) { Log("the runtime offers no sRGB RGBA8 swapchain format"); return false; }
    Log("swapchain format %d", (int)g_format);
    if (!GetFn("xrCreateSwapchain", p_createSwap) || !GetFn("xrEnumerateSwapchainImages", p_enumImages) || !GetFn("xrDestroySwapchain", p_destroySwap)) return false;

    PFN_xrEnumerateViewConfigurationViews enumViews;
    if (!GetFn("xrEnumerateViewConfigurationViews", enumViews)) return false;
    XrViewConfigurationView v[2] = {XrViewConfigurationView{XR_TYPE_VIEW_CONFIGURATION_VIEW}, XrViewConfigurationView{XR_TYPE_VIEW_CONFIGURATION_VIEW}};
    uint32_t vc = 0;
    XRCHECK(enumViews(g_inst, g_sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &vc, v));
    g_recW = (int)v[0].recommendedImageRectWidth;
    g_recH = (int)v[0].recommendedImageRectHeight;
    int maxW = (int)v[0].maxImageRectWidth, maxH = (int)v[0].maxImageRectHeight;
    // Render exactly the recommended size times Resolution, even, clamped to the runtime's largest image and 8192.
    g_eyeW = (int)lroundf(g_recW * g_scale) & ~1;
    g_eyeH = (int)lroundf(g_recH * g_scale) & ~1;
    int capW = maxW > 0 ? maxW : 8192, capH = maxH > 0 ? maxH : 8192;
    if (capW > 8192) capW = 8192;
    if (capH > 8192) capH = 8192;
    if (g_eyeW > capW || g_eyeH > capH) {
        float k = fminf((float)capW / g_eyeW, (float)capH / g_eyeH);
        g_eyeW = (int)(g_eyeW * k) & ~1;
        g_eyeH = (int)(g_eyeH * k) & ~1;
    }
    Log("EYESIZE runtime recommends %dx%d (largest %dx%d), Resolution %.0f%%, eye images %dx%d (%.3f of the recommended pixels)",
        g_recW, g_recH, maxW, maxH, g_scale * 100.0f, g_eyeW, g_eyeH, (double)g_eyeW * g_eyeH / ((double)g_recW * g_recH));

    return GetFn("xrPollEvent", p_poll) && GetFn("xrBeginSession", p_beginSession) && GetFn("xrEndSession", p_endSession) &&
           GetFn("xrWaitFrame", p_waitFrame) && GetFn("xrBeginFrame", p_beginFrame) && GetFn("xrEndFrame", p_endFrame) &&
           GetFn("xrLocateViews", p_locate) && GetFn("xrAcquireSwapchainImage", p_acquire) &&
           GetFn("xrWaitSwapchainImage", p_waitImage) && GetFn("xrReleaseSwapchainImage", p_release);
}

// ---------------------------------------------------------------------------------------------------------------------
// The ring of shared textures

static const int RingSize = 3;
enum SlotState { SlotFree = 0, SlotWriting = 1, SlotReady = 2, SlotReading = 3 };
struct Slot {
    std::atomic<int> state{SlotFree};
    unsigned long long seq = 0;
    ID3D11Texture2D* u[TexCount] = {};      // opened on Unity's device
    ID3D11Texture2D* x[TexCount] = {};      // created on the mod's device
    IDXGIKeyedMutex* ku[TexCount] = {};
    IDXGIKeyedMutex* kx[TexCount] = {};
    ID3D11ShaderResourceView* srv[TexCount] = {};  // on the mod's device, for drawing panels in Steady
    FrameMeta meta{};
};
static Slot g_ring[RingSize];
static unsigned long long g_seq;

static bool CreateShared(int w, int h, ID3D11Texture2D*& x, ID3D11Texture2D*& u, IDXGIKeyedMutex*& kx, IDXGIKeyedMutex*& ku, ID3D11ShaderResourceView*& srv) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w;
    d.Height = h;
    d.MipLevels = d.ArraySize = 1;
    d.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
    d.SampleDesc.Count = 1;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    d.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    HRESULT hr = g_xdev->CreateTexture2D(&d, nullptr, &x);
    if (FAILED(hr)) { Log("shared texture %dx%d failed: %08lx", w, h, (unsigned long)hr); return false; }
    IDXGIResource* res = nullptr;
    HANDLE handle = nullptr;
    if (FAILED(x->QueryInterface(__uuidof(IDXGIResource), (void**)&res)) || FAILED(res->GetSharedHandle(&handle))) { if (res) res->Release(); Log("no shared handle"); return false; }
    res->Release();
    hr = g_dev->OpenSharedResource(handle, __uuidof(ID3D11Texture2D), (void**)&u);
    if (FAILED(hr)) { Log("the game's device could not open the shared texture: %08lx", (unsigned long)hr); return false; }
    x->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&kx);
    u->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&ku);
    D3D11_SHADER_RESOURCE_VIEW_DESC sv{};
    sv.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    sv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sv.Texture2D.MipLevels = 1;
    g_xdev->CreateShaderResourceView(x, &sv, &srv);
    return kx && ku && srv;
}

static bool CreateRing() {
    g_panelW[TexL] = g_panelW[TexR] = g_eyeW;
    g_panelH[TexL] = g_panelH[TexR] = g_eyeH;
    for (auto& s : g_ring)
        for (int t = 0; t < TexCount; t++)
            if (!CreateShared(g_panelW[t], g_panelH[t], s.x[t], s.u[t], s.kx[t], s.ku[t], s.srv[t])) return false;
    Log("ring: %d slots of eyes %dx%d, HUD %dx%d, menu %dx%d", RingSize, g_eyeW, g_eyeH, g_panelW[TexHud], g_panelH[TexHud], g_panelW[TexMenu], g_panelH[TexMenu]);
    return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// Swapchains and the panel pass (Steady draws the HUD and menu into the eye views)

struct Swap { XrSwapchain handle = XR_NULL_HANDLE; std::vector<XrSwapchainImageD3D11KHR> images; std::vector<ID3D11RenderTargetView*> rtv; int w = 0, h = 0; };
static Swap g_eyeSwap[2], g_hudSwap, g_menuSwap;

static bool CreateSwap(Swap& s, int w, int h) {
    XrSwapchainCreateInfo c{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    c.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    c.format = g_format;
    c.sampleCount = 1;
    c.width = w;
    c.height = h;
    c.faceCount = c.arraySize = c.mipCount = 1;
    XRCHECK(p_createSwap(g_sess, &c, &s.handle));
    uint32_t ic = 0;
    XRCHECK(p_enumImages(s.handle, 0, &ic, nullptr));
    s.images.assign(ic, XrSwapchainImageD3D11KHR{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    XRCHECK(p_enumImages(s.handle, ic, &ic, (XrSwapchainImageBaseHeader*)s.images.data()));
    s.rtv.assign(ic, nullptr);
    s.w = w;
    s.h = h;
    return true;
}

static const char* g_panelShader = R"(
Texture2D src : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Panel : register(b0) { float4 corner[4]; };
void vs(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0) {
    uv = float2(id & 1, id >> 1);
    pos = corner[id];
}
float4 ps(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    float2 edge = min(uv, 1 - uv) / max(fwidth(uv), 0.000001);
    float4 c = src.Sample(linearClamp, uv);
    return float4(c.rgb, c.a * saturate(min(edge.x, edge.y)));
}
)";
static ID3D11VertexShader* g_panelVs;
static ID3D11PixelShader* g_panelPs;
static ID3D11Buffer* g_panelCb;
static ID3D11SamplerState* g_linear;
static ID3D11RasterizerState* g_noCull;
static ID3D11BlendState* g_blend;

static bool CreatePanelPass() {
    HMODULE comp = LoadLibraryA("d3dcompiler_47.dll");
    pD3DCompile compile = comp ? (pD3DCompile)GetProcAddress(comp, "D3DCompile") : nullptr;
    if (!compile) { Log("no shader compiler: Steady panels unavailable"); return false; }
    ID3DBlob *vsb = nullptr, *psb = nullptr, *err = nullptr;
    size_t len = strlen(g_panelShader);
    if (FAILED(compile(g_panelShader, len, "panel", nullptr, nullptr, "vs", "vs_5_0", 0, 0, &vsb, &err)) ||
        FAILED(compile(g_panelShader, len, "panel", nullptr, nullptr, "ps", "ps_5_0", 0, 0, &psb, &err))) {
        Log("panel shader error %s", err ? (const char*)err->GetBufferPointer() : "unknown");
        return false;
    }
    g_xdev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &g_panelVs);
    g_xdev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &g_panelPs);
    vsb->Release();
    psb->Release();
    D3D11_BUFFER_DESC cb{};
    cb.ByteWidth = 64;
    cb.Usage = D3D11_USAGE_DEFAULT;
    cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    g_xdev->CreateBuffer(&cb, nullptr, &g_panelCb);
    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    g_xdev->CreateSamplerState(&sd, &g_linear);
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    g_xdev->CreateRasterizerState(&rd, &g_noCull);
    D3D11_BLEND_DESC bd{};
    bd.RenderTarget[0].BlendEnable = TRUE;
    bd.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ZERO;
    bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    g_xdev->CreateBlendState(&bd, &g_blend);
    return g_panelVs && g_panelPs && g_panelCb && g_linear && g_noCull && g_blend;
}
static bool g_panelPassOk;

struct V3 { float x, y, z; };
static V3 Rotate(const XrQuaternionf& q, V3 v) {
    V3 t{2 * (q.y * v.z - q.z * v.y), 2 * (q.z * v.x - q.x * v.z), 2 * (q.x * v.y - q.y * v.x)};
    return {v.x + q.w * t.x + (q.y * t.z - q.z * t.y), v.y + q.w * t.y + (q.z * t.x - q.x * t.z), v.z + q.w * t.z + (q.x * t.y - q.y * t.x)};
}

// A panel's four corners (top left, top right, bottom left, bottom right) in the eye's clip space.
static void PanelCorners(const float* pose, const EyeView& e, float out[16]) {
    XrQuaternionf turn{pose[3], pose[4], pose[5], pose[6]};
    XrQuaternionf inv{-e.qx, -e.qy, -e.qz, e.qw};
    float hw = pose[7] * 0.5f, hh = pose[8] * 0.5f;
    const V3 local[4] = {{-hw, hh, 0}, {hw, hh, 0}, {-hw, -hh, 0}, {hw, -hh, 0}};
    float l = tanf(e.left), r = tanf(e.right), u = tanf(e.up), d = tanf(e.down);
    const float n = 0.05f, f = 100.0f;
    for (int i = 0; i < 4; i++) {
        V3 w = Rotate(turn, local[i]);
        V3 rel{pose[0] + w.x - e.px, pose[1] + w.y - e.py, pose[2] + w.z - e.pz};
        V3 v = Rotate(inv, rel);
        out[i * 4 + 0] = (2 * v.x + (l + r) * v.z) / (r - l);
        out[i * 4 + 1] = (2 * v.y + (u + d) * v.z) / (u - d);
        out[i * 4 + 2] = (f / (f - n)) * -v.z - f * n / (f - n);
        out[i * 4 + 3] = -v.z;
    }
}

static void DrawPanels(ID3D11RenderTargetView* rtv, int w, int h, const Slot& s, int eye) {
    D3D11_VIEWPORT vp{0, 0, (float)w, (float)h, 0, 1};
    g_xctx->RSSetViewports(1, &vp);
    g_xctx->RSSetState(g_noCull);
    g_xctx->OMSetBlendState(g_blend, nullptr, 0xffffffff);
    g_xctx->OMSetDepthStencilState(nullptr, 0);
    g_xctx->OMSetRenderTargets(1, &rtv, nullptr);
    g_xctx->IASetInputLayout(nullptr);
    g_xctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    g_xctx->VSSetShader(g_panelVs, nullptr, 0);
    g_xctx->VSSetConstantBuffers(0, 1, &g_panelCb);
    g_xctx->PSSetShader(g_panelPs, nullptr, 0);
    g_xctx->PSSetSamplers(0, 1, &g_linear);
    const int texs[2] = {TexHud, TexMenu};
    const float* poses[2] = {s.meta.hudPose, s.meta.menuPose};
    const int flags[2] = {FlagHud, FlagMenu};
    for (int i = 0; i < 2; i++) {
        if (!(s.meta.flags & flags[i])) continue;
        float corners[16];
        PanelCorners(poses[i], s.meta.eye[eye], corners);
        g_xctx->UpdateSubresource(g_panelCb, 0, nullptr, corners, 0, 0);
        g_xctx->PSSetShaderResources(0, 1, &s.srv[texs[i]]);
        g_xctx->Draw(4, 0);
    }
    ID3D11ShaderResourceView* none = nullptr;
    g_xctx->PSSetShaderResources(0, 1, &none);
    ID3D11RenderTargetView* noRtv = nullptr;
    g_xctx->OMSetRenderTargets(1, &noRtv, nullptr);
}

// Copies a slot texture into the swapchain's next image; Steady draws the panels on top of the eye images.
static bool CopyInto(Swap& sw, ID3D11Texture2D* src, const Slot* panels, int eye) {
    uint32_t idx = 0;
    XrResult r = p_acquire(sw.handle, nullptr, &idx);
    if (XR_FAILED(r)) { Log("acquire failed: %d", (int)r); return false; }
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = 100000000;
    r = p_waitImage(sw.handle, &wi);
    if (r != XR_SUCCESS) { Log("wait image: %d", (int)r); if (XR_FAILED(r)) return false; }
    g_xctx->CopySubresourceRegion(sw.images[idx].texture, 0, 0, 0, 0, src, 0, nullptr);
    if (panels && g_panelPassOk) {
        ID3D11RenderTargetView*& rtv = sw.rtv[idx];
        if (!rtv) {
            D3D11_RENDER_TARGET_VIEW_DESC rv{};
            rv.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            g_xdev->CreateRenderTargetView(sw.images[idx].texture, &rv, &rtv);
        }
        if (rtv) DrawPanels(rtv, sw.w, sw.h, *panels, eye);
    }
    p_release(sw.handle, nullptr);
    return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// The XR thread

static std::thread g_xrThread;
static std::atomic<bool> g_xrStop{false};
static std::mutex g_timeLock;
static XrTime g_lastDisplayTime;
static XrDuration g_lastPeriod;
static std::mutex g_statLock;
static unsigned int s_submitted, s_repeats, s_skipped, s_replaced;
static double s_wait, s_waitMax, s_end, s_endMax, s_copy, s_copyMax, s_gpuEye, s_gpuEyeMax, s_gpuFrame, s_gpuFrameMax;
static unsigned int s_waitN, s_copyN, s_gpuEyeN, s_gpuFrameN;
static FrameMeta g_lastMeta;
static bool g_haveLast, g_lastHudQuad, g_lastMenuQuad;
static LONG g_loggedLayer = -1;

static void PollEvents() {
    XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
    while (p_poll(g_inst, &ev) == XR_SUCCESS) {
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* s = (XrEventDataSessionStateChanged*)&ev;
            g_xrState = (LONG)s->state;
            Log("session state %d", (int)s->state);
            if (s->state == XR_SESSION_STATE_READY) {
                XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                XrResult r = p_beginSession(g_sess, &bi);
                Log("xrBeginSession: %d", (int)r);
                if (XR_SUCCEEDED(r)) g_running = 1;
            } else if (s->state == XR_SESSION_STATE_STOPPING) {
                g_running = 0;
                p_endSession(g_sess);
            }
        } else if (ev.type == XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING) {
            Log("the runtime recentred");
            g_recenterPending = 1;
        } else if (ev.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) {
            Log("the runtime is going away");
        }
        ev = XrEventDataBuffer{XR_TYPE_EVENT_DATA_BUFFER};
    }
}

static XrPosef PoseOf(const float* p) {
    XrPosef x;
    x.position = {p[0], p[1], p[2]};
    x.orientation = {p[3], p[4], p[5], p[6]};
    return x;
}

static void XrLoop() {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    Log("XR thread started");
    while (!g_xrStop) {
        PollEvents();
        if (!g_running) { Sleep(5); continue; }
        double t0 = NowMs();
        XrFrameState fs{XR_TYPE_FRAME_STATE};
        XrResult r = p_waitFrame(g_sess, nullptr, &fs);
        double t1 = NowMs();
        if (XR_FAILED(r)) { Log("xrWaitFrame failed: %d", (int)r); Sleep(5); continue; }
        {
            std::lock_guard<std::mutex> l(g_timeLock);
            g_lastDisplayTime = fs.predictedDisplayTime;
            g_lastPeriod = fs.predictedDisplayPeriod;
        }
        r = p_beginFrame(g_sess, nullptr);
        if (XR_FAILED(r)) { Log("xrBeginFrame failed: %d", (int)r); continue; }

        // The newest finished slot; older finished ones are dropped (the game made frames faster than the headset took them).
        Slot* pick = nullptr;
        for (auto& s : g_ring) {
            if (s.state.load() != SlotReady) continue;
            if (!pick || s.seq > pick->seq) {
                if (pick) { pick->state = SlotFree; std::lock_guard<std::mutex> l(g_statLock); s_replaced++; }
                pick = &s;
            } else {
                s.state = SlotFree;
                std::lock_guard<std::mutex> l(g_statLock);
                s_replaced++;
            }
        }
        double c0 = NowMs();
        if (pick) {
            pick->state = SlotReading;
            bool steady = (pick->meta.flags & FlagSteady) != 0;
            bool ok = true;
            int need[TexCount] = {1, 1, (pick->meta.flags & FlagHud) ? 1 : 0, (pick->meta.flags & FlagMenu) ? 1 : 0};
            for (int t = 0; t < TexCount && ok; t++) if (need[t] && pick->kx[t]->AcquireSync(1, 100) != S_OK) { Log("slot texture %d not handed over in time", t); ok = false; }
            if (ok && (pick->meta.flags & FlagViewsValid)) {
                ok = CopyInto(g_eyeSwap[0], pick->x[TexL], steady ? pick : nullptr, 0) && CopyInto(g_eyeSwap[1], pick->x[TexR], steady ? pick : nullptr, 1);
                if (ok && !steady && need[TexHud]) ok = CopyInto(g_hudSwap, pick->x[TexHud], nullptr, 0);
                if (ok && !steady && need[TexMenu]) ok = CopyInto(g_menuSwap, pick->x[TexMenu], nullptr, 0);
                if (ok) {
                    g_lastMeta = pick->meta;
                    g_haveLast = true;
                    g_lastHudQuad = !steady && need[TexHud];
                    g_lastMenuQuad = !steady && need[TexMenu];
                }
            }
            for (int t = 0; t < TexCount; t++) if (need[t]) pick->kx[t]->ReleaseSync(0);
            pick->state = SlotFree;
            g_xctx->Flush();
        }
        double c1 = NowMs();

        XrCompositionLayerProjectionView views[2] = {{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}, {XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}};
        XrCompositionLayerProjection proj{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
        XrCompositionLayerQuad quads[2] = {{XR_TYPE_COMPOSITION_LAYER_QUAD}, {XR_TYPE_COMPOSITION_LAYER_QUAD}};
        const XrCompositionLayerBaseHeader* layers[3];
        uint32_t count = 0;
        if (fs.shouldRender && g_haveLast) {
            for (int e = 0; e < 2; e++) {
                const EyeView& ev = g_lastMeta.eye[e];
                views[e].pose.position = {ev.px, ev.py, ev.pz};
                views[e].pose.orientation = {ev.qx, ev.qy, ev.qz, ev.qw};
                views[e].fov = {ev.left, ev.right, ev.up, ev.down};
                views[e].subImage.swapchain = g_eyeSwap[e].handle;
                views[e].subImage.imageRect.extent = {g_eyeSwap[e].w, g_eyeSwap[e].h};
            }
            proj.space = g_space;
            proj.viewCount = 2;
            proj.views = views;
            layers[count++] = (XrCompositionLayerBaseHeader*)&proj;
            const bool use[2] = {g_lastHudQuad, g_lastMenuQuad};
            Swap* sw[2] = {&g_hudSwap, &g_menuSwap};
            const float* poses[2] = {g_lastMeta.hudPose, g_lastMeta.menuPose};
            for (int i = 0; i < 2; i++) {
                if (!use[i]) continue;
                XrCompositionLayerQuad& q = quads[i];
                q.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
                q.space = g_space;
                q.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
                q.subImage.swapchain = sw[i]->handle;
                q.subImage.imageRect.extent = {sw[i]->w, sw[i]->h};
                q.pose = PoseOf(poses[i]);
                q.size = {poses[i][7], poses[i][8]};
                layers[count++] = (XrCompositionLayerBaseHeader*)&q;
            }
            LONG kind = (g_lastMeta.flags & FlagSteady) ? 1 : 0;
            if (kind != g_loggedLayer) {
                g_loggedLayer = kind;
                Log("LAYER world as projection, HUD and menu %s", kind ? "drawn into the eye views (Steady)" : "as quads (Panel)");
            }
        }
        XrFrameEndInfo fei{XR_TYPE_FRAME_END_INFO};
        fei.displayTime = fs.predictedDisplayTime;
        fei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        fei.layerCount = count;
        fei.layers = layers;
        double e0 = NowMs();
        r = p_endFrame(g_sess, &fei);
        double e1 = NowMs();
        if (XR_FAILED(r)) Log("xrEndFrame failed: %d", (int)r);
        std::lock_guard<std::mutex> l(g_statLock);
        s_submitted++;
        if (!pick) s_repeats++;
        double w = t1 - t0, en = e1 - e0, cp = c1 - c0;
        s_wait += w; s_end += en; s_waitN++;
        if (w > s_waitMax) s_waitMax = w;
        if (en > s_endMax) s_endMax = en;
        if (pick) { s_copy += cp; s_copyN++; if (cp > s_copyMax) s_copyMax = cp; }
    }
    Log("XR thread stopped");
}

// ---------------------------------------------------------------------------------------------------------------------
// Render thread

struct PendingSubmit { ID3D11Texture2D* src[TexCount]; FrameMeta meta; };
static std::mutex g_pendLock;
static std::deque<PendingSubmit> g_pending;

// GPU timestamps on the game's device: from EyeBegin (before the eyes are drawn) to Submit, and Submit to Submit.
struct GpuQ { ID3D11Query *disjoint = nullptr, *begin = nullptr, *end = nullptr; bool open = false, issued = false; };
static GpuQ g_gq[8];
static int g_gqIndex;
static UINT64 g_lastEndTs, g_lastEndFreq;

static void GpuRead() {
    for (auto& q : g_gq) {
        if (!q.issued) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj;
        if (g_ctx->GetData(q.disjoint, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        UINT64 b = 0, e = 0;
        if (g_ctx->GetData(q.begin, &b, sizeof(b), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        if (g_ctx->GetData(q.end, &e, sizeof(e), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        q.issued = false;
        if (dj.Disjoint || !dj.Frequency) continue;
        double eye = (double)(e - b) * 1000.0 / dj.Frequency;
        std::lock_guard<std::mutex> l(g_statLock);
        s_gpuEye += eye; s_gpuEyeN++; if (eye > s_gpuEyeMax) s_gpuEyeMax = eye;
        if (g_lastEndTs && g_lastEndFreq == dj.Frequency && e > g_lastEndTs) {
            double fr = (double)(e - g_lastEndTs) * 1000.0 / dj.Frequency;
            if (fr < 1000.0) { s_gpuFrame += fr; s_gpuFrameN++; if (fr > s_gpuFrameMax) s_gpuFrameMax = fr; }
        }
        g_lastEndTs = e;
        g_lastEndFreq = dj.Frequency;
    }
}

static void DoEyeBegin() {
    if (!g_dev) return;
    GpuQ& q = g_gq[g_gqIndex];
    if (q.issued || q.open) return;  // still waiting for an old reading: skip this frame's measurement
    if (!q.disjoint) {
        D3D11_QUERY_DESC d{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        g_dev->CreateQuery(&d, &q.disjoint);
        d.Query = D3D11_QUERY_TIMESTAMP;
        g_dev->CreateQuery(&d, &q.begin);
        g_dev->CreateQuery(&d, &q.end);
    }
    g_ctx->Begin(q.disjoint);
    g_ctx->End(q.begin);
    q.open = true;
}

static void DoSubmit() {
    PendingSubmit p;
    {
        std::lock_guard<std::mutex> l(g_pendLock);
        if (g_pending.empty()) return;
        p = g_pending.front();
        g_pending.pop_front();
    }
    GpuQ& q = g_gq[g_gqIndex];
    if (q.open) {
        g_ctx->End(q.end);
        g_ctx->End(q.disjoint);
        q.open = false;
        q.issued = true;
        g_gqIndex = (g_gqIndex + 1) % 8;
    }
    GpuRead();
    Slot* slot = nullptr;
    if (g_state == StateReady)
        for (auto& s : g_ring) { int f = SlotFree; if (s.state.compare_exchange_strong(f, SlotWriting)) { slot = &s; break; } }
    if (!slot) {
        if (g_state == StateReady) { std::lock_guard<std::mutex> l(g_statLock); s_skipped++; }
    } else {
        bool ok = true;
        for (int t = 0; t < TexCount; t++) {
            if (!p.src[t]) continue;
            if (slot->ku[t]->AcquireSync(0, 0) != S_OK) { ok = false; break; }
            D3D11_TEXTURE2D_DESC d;
            p.src[t]->GetDesc(&d);
            if ((int)d.Width == g_panelW[t] && (int)d.Height == g_panelH[t]) g_ctx->CopyResource(slot->u[t], p.src[t]);
            else ok = false;
            slot->ku[t]->ReleaseSync(1);
        }
        if (ok) {
            slot->meta = p.meta;
            slot->seq = ++g_seq;
            slot->state = SlotReady;
        } else {
            // Hand the textures back so the slot can be used again.
            for (int t = 0; t < TexCount; t++) if (p.src[t] && slot->ku[t]->AcquireSync(1, 0) == S_OK) slot->ku[t]->ReleaseSync(0);
            slot->state = SlotFree;
            std::lock_guard<std::mutex> l(g_statLock);
            s_skipped++;
        }
    }
    for (auto t : p.src) if (t) t->Release();
}

static void DoInit() {
    if (!g_any) { Log("no texture to find the game's device"); g_state = StateFailed; return; }
    g_any->GetDevice(&g_dev);
    g_dev->GetImmediateContext(&g_ctx);
    LogDisplayHdr();
    bool ok = StartXr();
    if (ok) ok = CreateRing();
    if (ok) ok = CreateSwap(g_eyeSwap[0], g_eyeW, g_eyeH) && CreateSwap(g_eyeSwap[1], g_eyeW, g_eyeH) &&
                 CreateSwap(g_hudSwap, g_panelW[TexHud], g_panelH[TexHud]) && CreateSwap(g_menuSwap, g_panelW[TexMenu], g_panelH[TexMenu]);
    if (ok) {
        g_panelPassOk = CreatePanelPass();
        Log("swapchains: eyes %dx%d, HUD %dx%d, menu %dx%d, %zu images each; Steady panels %s", g_eyeW, g_eyeH, g_panelW[TexHud], g_panelH[TexHud],
            g_panelW[TexMenu], g_panelH[TexMenu], g_eyeSwap[0].images.size(), g_panelPassOk ? "ready" : "unavailable");
        g_xrStop = false;
        g_xrThread = std::thread(XrLoop);
    }
    g_state = ok ? StateReady : StateFailed;
    Log(ok ? "OpenXR ready" : "OpenXR start failed, the game stays flat");
}

static void DoShutdown() {
    g_xrStop = true;
    if (g_xrThread.joinable()) g_xrThread.join();
    unsigned int sub;
    { std::lock_guard<std::mutex> l(g_statLock); sub = s_submitted; }
    Log("shutting down after %u headset frames", sub);
    if (g_inst) {
        PFN_xrDestroyInstance destroy;
        if (GetFn("xrDestroyInstance", destroy)) destroy(g_inst);
        g_inst = XR_NULL_HANDLE;
        g_sess = XR_NULL_HANDLE;
    }
    g_running = 0;
    g_state = StateOff;
}

static void __stdcall RenderEvent(int id) {
    if (id == EventInit) DoInit();
    else if (id == EventSubmit) DoSubmit();
    else if (id == EventEyeBegin) DoEyeBegin();
    else if (id == EventShutdown) DoShutdown();
}

// ---------------------------------------------------------------------------------------------------------------------
// The mod's frame cap, on the main thread (TECHNIQUES.md "The mod's frame cap and the refresh rate")

static double g_deadline;
static double g_fullRate;           // the highest rate this headset has reported
static double g_lowRate, g_lowSince; // a lower report, and since when it has held
static double g_capRate;

static double CapRate(double reported) {
    if (reported < 30.0 || reported > 250.0) return g_capRate > 0 ? g_capRate : 0;
    double now = NowMs();
    if (reported > g_fullRate + 0.5) {
        if (g_fullRate > 0) Log("CAP headset rate %.1f Hz (was %.1f)", reported, g_fullRate);
        else Log("CAP headset rate %.1f Hz, the mod caps the game there", reported);
        g_fullRate = reported;
        g_lowRate = 0;
    } else if (reported < g_fullRate - 0.5) {
        // A lower report is the runtime smoothing, unless it holds 20 s and is not a simple fraction of the full rate.
        if (fabs(reported - g_lowRate) > 0.5) { g_lowRate = reported; g_lowSince = now; }
        double k = reported / g_fullRate;
        bool fraction = fabs(k - 0.5) < 0.02 || fabs(k - 2.0 / 3.0) < 0.02 || fabs(k - 1.0 / 3.0) < 0.02;
        if (!fraction && now - g_lowSince > 20000.0) {
            Log("CAP the headset now runs at %.1f Hz (steady for 20 s), cap follows", reported);
            g_fullRate = reported;
            g_lowRate = 0;
        }
    }
    return g_fullRate;
}

// ---------------------------------------------------------------------------------------------------------------------
// API for the C# plugin

API void* NslXR_GetRenderEvent() { return (void*)RenderEvent; }
API int NslXR_State() { return (int)g_state; }
API int NslXR_RecommendedWidth() { return g_recW; }
API int NslXR_RecommendedHeight() { return g_recH; }
API int NslXR_EyeWidth() { return g_eyeW; }
API int NslXR_EyeHeight() { return g_eyeH; }
API int NslXR_LastError() { return (int)g_lastXrError; }
API void NslXR_FailedLayers(char* out, int size) { strncpy_s(out, size, g_failedLayers, _TRUNCATE); }
API int NslXR_TakeRecenter() { return InterlockedExchange(&g_recenterPending, 0) ? 1 : 0; }
API void NslXR_RuntimeInfo(char* runtime, int runtimeSize, char* system, int systemSize) {
    strncpy_s(runtime, runtimeSize, g_runtimeName, _TRUNCATE);
    strncpy_s(system, systemSize, g_systemName, _TRUNCATE);
}

// Main thread, before the init event: any Unity texture (to find the device), the Resolution scale, the panel sizes.
API void NslXR_Start(void* anyTexture, float scale, int hudW, int hudH, int menuW, int menuH) {
    if (g_state != StateOff && g_state != StateFailed) return;
    g_any = (ID3D11Texture2D*)anyTexture;
    g_scale = scale;
    g_panelW[TexHud] = hudW; g_panelH[TexHud] = hudH;
    g_panelW[TexMenu] = menuW; g_panelH[TexMenu] = menuH;
    g_state = StateStarting;
}

// Main thread: eye poses for the frame the game is about to draw, one headset period after the last one the XR thread waited for.
API int NslXR_GetViews(ViewInfo* out) {
    memset(out, 0, sizeof(*out));
    out->sessionState = (int)g_xrState;
    if (g_state != StateReady || !g_running) return 0;
    out->running = 1;
    XrTime t;
    XrDuration p;
    {
        std::lock_guard<std::mutex> l(g_timeLock);
        t = g_lastDisplayTime;
        p = g_lastPeriod;
    }
    if (!t) return 0;
    out->periodMs = (float)(p / 1e6);
    out->displayTime = t + p;
    XrViewLocateInfo li{XR_TYPE_VIEW_LOCATE_INFO};
    li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    li.displayTime = t + p;
    li.space = g_space;
    XrViewState vs{XR_TYPE_VIEW_STATE};
    XrView views[2] = {XrView{XR_TYPE_VIEW}, XrView{XR_TYPE_VIEW}};
    uint32_t vc = 0;
    XrResult r = p_locate(g_sess, &li, &vs, 2, &vc, views);
    if (XR_FAILED(r) || vc != 2 || !(vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) || !(vs.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT)) return 1;
    out->viewsValid = 1;
    for (int e = 0; e < 2; e++) {
        auto& ps = views[e].pose;
        out->eye[e] = {ps.position.x, ps.position.y, ps.position.z, ps.orientation.x, ps.orientation.y, ps.orientation.z, ps.orientation.w,
                       views[e].fov.angleLeft, views[e].fov.angleRight, views[e].fov.angleUp, views[e].fov.angleDown};
    }
    return 1;
}

// Main thread, before the submit event: this frame's textures (null for a panel not shown) and what goes with them.
API void NslXR_QueueFrame(void* left, void* right, void* hud, void* menu, const FrameMeta* meta) {
    PendingSubmit p{{(ID3D11Texture2D*)left, (ID3D11Texture2D*)right, (ID3D11Texture2D*)hud, (ID3D11Texture2D*)menu}, *meta};
    for (auto t : p.src) if (t) t->AddRef();
    std::lock_guard<std::mutex> l(g_pendLock);
    while (g_pending.size() > 4) {  // the render thread fell behind: drop the oldest
        for (auto t : g_pending.front().src) if (t) t->Release();
        g_pending.pop_front();
    }
    g_pending.push_back(p);
}

// Main thread, once per frame: waits for the mod's own deadline at the headset's full rate. Returns the ms waited.
API float NslXR_CapWait(float reportedHz) {
    static bool once = [] { timeBeginPeriod(1); return true; }();
    (void)once;
    g_capRate = CapRate(reportedHz);
    if (g_capRate <= 0) return 0;
    double period = 1000.0 / g_capRate, now = NowMs();
    if (g_deadline == 0 || now - g_deadline > period) g_deadline = now;  // re-anchor when more than a period late
    g_deadline += period;
    double start = now;
    while (true) {
        now = NowMs();
        double left = g_deadline - now;
        if (left <= 0) break;
        if (left > 1.5) Sleep(1); else YieldProcessor();
    }
    return (float)(now - start);
}

API void NslXR_Stats(Stats* out) {
    std::lock_guard<std::mutex> l(g_statLock);
    out->submitted = s_submitted; out->repeats = s_repeats; out->skipped = s_skipped; out->replaced = s_replaced;
    out->waitAvg = s_waitN ? (float)(s_wait / s_waitN) : 0; out->waitMax = (float)s_waitMax;
    out->endAvg = s_waitN ? (float)(s_end / s_waitN) : 0; out->endMax = (float)s_endMax;
    out->copyAvg = s_copyN ? (float)(s_copy / s_copyN) : 0; out->copyMax = (float)s_copyMax;
    out->gpuEyeAvg = s_gpuEyeN ? (float)(s_gpuEye / s_gpuEyeN) : 0; out->gpuEyeMax = (float)s_gpuEyeMax;
    out->gpuFrameAvg = s_gpuFrameN ? (float)(s_gpuFrame / s_gpuFrameN) : 0; out->gpuFrameMax = (float)s_gpuFrameMax;
    s_submitted = s_repeats = s_skipped = s_replaced = 0;
    s_wait = s_waitMax = s_end = s_endMax = s_copy = s_copyMax = s_gpuEye = s_gpuEyeMax = s_gpuFrame = s_gpuFrameMax = 0;
    s_waitN = s_copyN = s_gpuEyeN = s_gpuFrameN = 0;
}

API int NslXR_PopLog(char* buffer, int size) {
    std::lock_guard<std::mutex> l(g_logLock);
    if (g_log.empty()) return 0;
    strncpy_s(buffer, size, g_log.front().c_str(), _TRUNCATE);
    g_log.pop_front();
    return 1;
}
