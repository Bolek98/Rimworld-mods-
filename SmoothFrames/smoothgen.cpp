// Smooth Frames – frame generation for RimWorld (Bolek98).
// A small program running next to the game: it takes the picture the game draws, works out how
// things moved between two frames and shows extra in-between frames in a click-through window
// laid exactly over the game. The game runs as before; only the picture gets smoother.
//
// Needs: Windows 10 2004+ (window hidden from capture), the game in borderless / windowed mode.
// Hotkeys: Ctrl+Alt+F on/off, Ctrl+Alt+M show motion (debug), Ctrl+Alt+Q quit.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#include <timeapi.h>
#include <cstdio>
#include <cstdint>
#include <cmath>
#include <string>
#include <cstdarg>
#include <algorithm>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif
#ifndef DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
#define DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 ((HANDLE)-4)
#endif

static const char* kVersion = "0.1.0";

template <class T> static void Rel(T*& p) { if (p) { p->Release(); p = nullptr; } }

// ------------------------------------------------------------------ shaders
static const char* kShaders = R"HLSL(
Texture2D<float4> SrcA : register(t0);
Texture2D<float4> SrcB : register(t1);
Texture2D<float2> MV   : register(t2);
SamplerState Lin : register(s0);
SamplerState Pt  : register(s1);

cbuffer P : register(b0)
{
    float  alpha;
    float  debugView;
    float2 mvScale;      // motion field units -> uv
};

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float Luma(float3 c) { return dot(saturate(c), float3(0.299, 0.587, 0.114)); }

// colour -> luma, 4x smaller (4 bilinear taps = a 4x4 box)
float PSLumaDown(VSOut i) : SV_Target
{
    float w, h; SrcA.GetDimensions(w, h);
    float2 t = 1.0 / float2(w, h);
    float l = Luma(SrcA.SampleLevel(Lin, i.uv + t * float2(-1, -1), 0).rgb)
            + Luma(SrcA.SampleLevel(Lin, i.uv + t * float2( 1, -1), 0).rgb)
            + Luma(SrcA.SampleLevel(Lin, i.uv + t * float2(-1,  1), 0).rgb)
            + Luma(SrcA.SampleLevel(Lin, i.uv + t * float2( 1,  1), 0).rgb);
    return l * 0.25;
}

// luma -> luma, 4x smaller
float PSDown(VSOut i) : SV_Target
{
    float w, h; SrcA.GetDimensions(w, h);
    float2 t = 1.0 / float2(w, h);
    float l = SrcA.SampleLevel(Lin, i.uv + t * float2(-1, -1), 0).r
            + SrcA.SampleLevel(Lin, i.uv + t * float2( 1, -1), 0).r
            + SrcA.SampleLevel(Lin, i.uv + t * float2(-1,  1), 0).r
            + SrcA.SampleLevel(Lin, i.uv + t * float2( 1,  1), 0).r;
    return l * 0.25;
}

// the in-between frame: prev moved forward, curr moved back, blended; where moving does not
// fit (UI, things that appear) the plain frames are used instead
float4 PSInterp(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float2 mv = MV.SampleLevel(Lin, uv, 0) * mvScale;          // prev -> curr, in uv
    float a = alpha;
    float3 pa = SrcA.SampleLevel(Lin, uv - mv * a, 0).rgb;
    float3 cb = SrcB.SampleLevel(Lin, uv + mv * (1 - a), 0).rgb;
    float3 s0 = SrcA.SampleLevel(Pt, uv, 0).rgb;
    float3 s1 = SrcB.SampleLevel(Pt, uv, 0).rgb;
    float dm = length(pa - cb);
    float ds = length(s0 - s1);
    float3 col = ds <= dm ? lerp(s0, s1, a) : lerp(pa, cb, a);
    if (min(dm, ds) > 0.30)
        col = a < 0.5 ? (ds <= dm ? s0 : pa) : (ds <= dm ? s1 : cb);
    if (debugView > 0.5)
    {
        float2 m = MV.SampleLevel(Pt, uv, 0);
        col = lerp(col, float3(0.5 + m.x * 0.05, 0.5 + m.y * 0.05, 0.5), 0.6);
    }
    return float4(saturate(col), 1);
}
)HLSL";

// block matching: coarse (1/16 res, wide search), then fine (1/4 res, around the coarse guess),
// then a 3x3 vector median
static const char* kCompute = R"HLSL(
Texture2D<float> Prev : register(t0);
Texture2D<float> Curr : register(t1);
Texture2D<float2> Guess : register(t2);
RWTexture2D<float2> Out : register(u0);

cbuffer C : register(b0)
{
    int  block;       // block size in pixels of this level
    int  range;       // search +- pixels
    int  guessScale;  // guess units -> this level units
    int  guessDiv;    // block index -> guess index
};

float SAD(int2 o, int2 d, int2 size)
{
    float s = 0;
    for (int y = 0; y < block; y++)
        for (int x = 0; x < block; x++)
        {
            int2 p = o + int2(x, y);
            int2 q = clamp(p - d, int2(0, 0), size - 1);
            s += abs(Curr[clamp(p, int2(0, 0), size - 1)] - Prev[q]);
        }
    return s;
}

[numthreads(8, 8, 1)]
void CSCoarse(uint3 id : SV_DispatchThreadID)
{
    uint w, h; Curr.GetDimensions(w, h);
    int2 size = int2(w, h);
    int2 o = int2(id.xy) * block;
    if (o.x >= size.x || o.y >= size.y) return;
    float best = SAD(o, int2(0, 0), size) * 0.98;   // a small bonus for "nothing moved"
    int2 bd = int2(0, 0);
    for (int dy = -range; dy <= range; dy++)
        for (int dx = -range; dx <= range; dx++)
        {
            float s = SAD(o, int2(dx, dy), size);
            if (s < best) { best = s; bd = int2(dx, dy); }
        }
    Out[id.xy] = float2(bd);
}

[numthreads(8, 8, 1)]
void CSFine(uint3 id : SV_DispatchThreadID)
{
    uint w, h; Curr.GetDimensions(w, h);
    int2 size = int2(w, h);
    int2 o = int2(id.xy) * block;
    if (o.x >= size.x || o.y >= size.y) return;
    uint gw, gh; Guess.GetDimensions(gw, gh);
    int2 gi = int2(id.xy) / guessDiv;
    int2 cand[5];
    cand[0] = int2(0, 0);
    cand[1] = int2(Guess[clamp(gi, int2(0, 0), int2(gw - 1, gh - 1))] * guessScale);
    cand[2] = int2(Guess[clamp(gi + int2(1, 0), int2(0, 0), int2(gw - 1, gh - 1))] * guessScale);
    cand[3] = int2(Guess[clamp(gi + int2(0, 1), int2(0, 0), int2(gw - 1, gh - 1))] * guessScale);
    cand[4] = int2(Guess[clamp(gi - int2(1, 1), int2(0, 0), int2(gw - 1, gh - 1))] * guessScale);
    float best = SAD(o, int2(0, 0), size) * 0.98;
    int2 bd = int2(0, 0);
    for (int c = 0; c < 5; c++)
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                int2 d = cand[c] + int2(dx, dy);
                float s = SAD(o, d, size);
                if (s < best) { best = s; bd = d; }
            }
    Out[id.xy] = float2(bd);
}

[numthreads(8, 8, 1)]
void CSMedian(uint3 id : SV_DispatchThreadID)
{
    uint w, h; Guess.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;
    float2 v[9];
    int n = 0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            v[n++] = Guess[clamp(int2(id.xy) + int2(x, y), int2(0, 0), int2(w - 1, h - 1))];
    float bestS = 1e9; float2 best = v[4];
    for (int i = 0; i < 9; i++)
    {
        float s = 0;
        for (int j = 0; j < 9; j++) s += length(v[i] - v[j]);
        if (s < bestS) { bestS = s; best = v[i]; }
    }
    Out[id.xy] = best;
}
)HLSL";

// ------------------------------------------------------------------ state
struct Tex
{
    ID3D11Texture2D* t = nullptr;
    ID3D11ShaderResourceView* srv = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11UnorderedAccessView* uav = nullptr;
    int w = 0, h = 0;
    void Free() { Rel(uav); Rel(rtv); Rel(srv); Rel(t); w = h = 0; }
};

static ID3D11Device* dev;
static ID3D11DeviceContext* ctx;
static IDXGIAdapter1* adapter;
static IDXGIOutput1* output;
static IDXGIOutputDuplication* dupl;
static RECT outRect;                    // the output's desktop rectangle
static IDXGISwapChain1* swap;
static IDCompositionDevice* dcomp;
static IDCompositionTarget* dtarget;
static IDCompositionVisual* dvisual;
static ID3D11RenderTargetView* backRtv;
static HWND overlay, game;
static int W, H;                        // game client size
static RECT gameRect;                   // client rect on the desktop
static DXGI_FORMAT capFmt = DXGI_FORMAT_UNKNOWN;

static Tex frame[2];                    // full colour: [prev, curr]
static Tex luma4[2], luma16[2];         // 1/4 and 1/16 luma
static Tex mvCoarse, mvFine, mvSmooth;
static int cur = 1;
static bool havePair = false, prevValid = false;

static ID3D11VertexShader* vs;
static ID3D11PixelShader *psLumaDown, *psDown, *psInterp;
static ID3D11ComputeShader *csCoarse, *csFine, *csMedian;
static ID3D11SamplerState *sLin, *sPt;
static ID3D11Buffer *cbP, *cbC;

static bool enabled = true, debugView = false, running = true;
static double tNew = 0, frameT = 1.0 / 30.0, tPrevNew = 0;
static int capCount = 0, outCount = 0;

static double Now()
{
    static LARGE_INTEGER f = {};
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
}

static void Say(const char* fmt, ...)
{
    char buf[1024];
    va_list a; va_start(a, fmt); vsnprintf(buf, sizeof buf, fmt, a); va_end(a);
    SYSTEMTIME st; GetLocalTime(&st);
    printf("[%02d:%02d:%02d] %s\n", st.wHour, st.wMinute, st.wSecond, buf);
    fflush(stdout);
}

// ------------------------------------------------------------------ helpers
typedef HRESULT(WINAPI* PD3DCompile)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);
static PD3DCompile pCompile;

static ID3DBlob* Compile(const char* src, const char* entry, const char* target)
{
    ID3DBlob *code = nullptr, *err = nullptr;
    HRESULT hr = pCompile(src, strlen(src), "smoothgen", nullptr, nullptr, entry, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
    if (FAILED(hr))
    {
        Say("Shader %s failed: %s", entry, err ? (const char*)err->GetBufferPointer() : "?");
        Rel(err);
        return nullptr;
    }
    Rel(err);
    return code;
}

static bool MakeTex(Tex& t, int w, int h, DXGI_FORMAT f, bool rtv, bool uav)
{
    t.Free();
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = f;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE | (rtv ? D3D11_BIND_RENDER_TARGET : 0) | (uav ? D3D11_BIND_UNORDERED_ACCESS : 0);
    if (FAILED(dev->CreateTexture2D(&d, nullptr, &t.t))) return false;
    dev->CreateShaderResourceView(t.t, nullptr, &t.srv);
    if (rtv) dev->CreateRenderTargetView(t.t, nullptr, &t.rtv);
    if (uav) dev->CreateUnorderedAccessView(t.t, nullptr, &t.uav);
    t.w = w; t.h = h;
    return true;
}

static void Viewport(int w, int h)
{
    D3D11_VIEWPORT v = { 0, 0, (float)w, (float)h, 0, 1 };
    ctx->RSSetViewports(1, &v);
}

static void Unbind()
{
    ID3D11ShaderResourceView* nul[3] = {};
    ctx->PSSetShaderResources(0, 3, nul);
    ctx->CSSetShaderResources(0, 3, nul);
    ID3D11UnorderedAccessView* nu = nullptr;
    ctx->CSSetUnorderedAccessViews(0, 1, &nu, nullptr);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
}

static void Pass(ID3D11PixelShader* ps, ID3D11RenderTargetView* rt, int w, int h, ID3D11ShaderResourceView* a, ID3D11ShaderResourceView* b = nullptr, ID3D11ShaderResourceView* c = nullptr)
{
    Unbind();
    ctx->OMSetRenderTargets(1, &rt, nullptr);
    Viewport(w, h);
    ID3D11ShaderResourceView* s[3] = { a, b, c };
    ctx->PSSetShaderResources(0, 3, s);
    ctx->PSSetShader(ps, nullptr, 0);
    ctx->Draw(3, 0);
}

struct CData { int block, range, guessScale, guessDiv; };

static void Dispatch(ID3D11ComputeShader* cs, Tex& out, ID3D11ShaderResourceView* a, ID3D11ShaderResourceView* b, ID3D11ShaderResourceView* g, CData c)
{
    Unbind();
    ctx->UpdateSubresource(cbC, 0, nullptr, &c, 0, 0);
    ctx->CSSetConstantBuffers(0, 1, &cbC);
    ID3D11ShaderResourceView* s[3] = { a, b, g };
    ctx->CSSetShaderResources(0, 3, s);
    ctx->CSSetUnorderedAccessViews(0, 1, &out.uav, nullptr);
    ctx->CSSetShader(cs, nullptr, 0);
    ctx->Dispatch((out.w + 7) / 8, (out.h + 7) / 8, 1);
}

// ------------------------------------------------------------------ setup
static bool InitShaders()
{
    HMODULE m = LoadLibraryA("d3dcompiler_47.dll");
    if (!m) { Say("d3dcompiler_47.dll is missing (Windows 10+ has it)."); return false; }
    pCompile = (PD3DCompile)GetProcAddress(m, "D3DCompile");
    if (!pCompile) return false;
    ID3DBlob* b;
    if (!(b = Compile(kShaders, "VSMain", "vs_5_0"))) return false; dev->CreateVertexShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &vs); Rel(b);
    if (!(b = Compile(kShaders, "PSLumaDown", "ps_5_0"))) return false; dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psLumaDown); Rel(b);
    if (!(b = Compile(kShaders, "PSDown", "ps_5_0"))) return false; dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psDown); Rel(b);
    if (!(b = Compile(kShaders, "PSInterp", "ps_5_0"))) return false; dev->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &psInterp); Rel(b);
    if (!(b = Compile(kCompute, "CSCoarse", "cs_5_0"))) return false; dev->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &csCoarse); Rel(b);
    if (!(b = Compile(kCompute, "CSFine", "cs_5_0"))) return false; dev->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &csFine); Rel(b);
    if (!(b = Compile(kCompute, "CSMedian", "cs_5_0"))) return false; dev->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &csMedian); Rel(b);

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    dev->CreateSamplerState(&sd, &sLin);
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    dev->CreateSamplerState(&sd, &sPt);
    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = 16; bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    dev->CreateBuffer(&bd, nullptr, &cbP);
    dev->CreateBuffer(&bd, nullptr, &cbC);
    // the same drawing state for every pass
    ctx->VSSetShader(vs, nullptr, 0);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->IASetInputLayout(nullptr);
    ID3D11SamplerState* ss[2] = { sLin, sPt };
    ctx->PSSetSamplers(0, 2, ss);
    ctx->PSSetConstantBuffers(0, 1, &cbP);
    return true;
}

// the adapter + output that show the game
static bool InitDevice(HMONITOR mon)
{
    IDXGIFactory1* f = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&f))) return false;
    IDXGIAdapter1* a = nullptr;
    for (UINT i = 0; !output && f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; i++)
    {
        IDXGIOutput* o = nullptr;
        for (UINT j = 0; a->EnumOutputs(j, &o) != DXGI_ERROR_NOT_FOUND; j++)
        {
            DXGI_OUTPUT_DESC od; o->GetDesc(&od);
            if (od.Monitor == mon)
            {
                o->QueryInterface(__uuidof(IDXGIOutput1), (void**)&output);
                outRect = od.DesktopCoordinates;
                adapter = a; a->AddRef();
            }
            o->Release();
            if (output) break;
        }
        a->Release();
    }
    f->Release();
    if (!output) { Say("Could not find the monitor of the game."); return false; }
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, fl, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    if (FAILED(hr)) { Say("Direct3D 11 device failed (0x%08lx).", hr); return false; }
    DXGI_ADAPTER_DESC1 ad; adapter->GetDesc1(&ad);
    Say("Graphics card: %ls", ad.Description);
    return InitShaders();
}

static bool StartCapture()
{
    Rel(dupl);
    HRESULT hr = output->DuplicateOutput(dev, &dupl);
    if (FAILED(hr)) { Say("Screen capture failed (0x%08lx) – is the game in exclusive fullscreen? Use borderless.", hr); return false; }
    return true;
}

static LRESULT CALLBACK OverlayProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_NCHITTEST) return HTTRANSPARENT;
    return DefWindowProcW(h, m, w, l);
}

static bool MakeOverlay()
{
    WNDCLASSW wc = {};
    wc.lpfnWndProc = OverlayProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"SmoothFramesOverlay";
    RegisterClassW(&wc);
    overlay = CreateWindowExW(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
        wc.lpszClassName, L"Smooth Frames", WS_POPUP, gameRect.left, gameRect.top, W, H, nullptr, nullptr, wc.hInstance, nullptr);
    if (!overlay) return false;
    SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA);
    if (!SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
        Say("Warning: Windows did not allow hiding the overlay from capture (needs Windows 10 2004+).");
    return true;
}

static bool MakeSwap()
{
    Rel(backRtv); Rel(dvisual); Rel(dtarget); Rel(dcomp); Rel(swap);
    IDXGIDevice* dd = nullptr; dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dd);
    IDXGIFactory2* f2 = nullptr; adapter->GetParent(__uuidof(IDXGIFactory2), (void**)&f2);
    DXGI_SWAP_CHAIN_DESC1 sd = {};
    sd.Width = W; sd.Height = H; sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count = 1; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 2;
    sd.Scaling = DXGI_SCALING_STRETCH; sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    HRESULT hr = f2->CreateSwapChainForComposition(dev, &sd, nullptr, &swap);
    f2->Release();
    if (FAILED(hr)) { dd->Release(); Say("Swap chain failed (0x%08lx).", hr); return false; }
    hr = DCompositionCreateDevice(dd, __uuidof(IDCompositionDevice), (void**)&dcomp);
    dd->Release();
    if (FAILED(hr)) { Say("DirectComposition failed (0x%08lx).", hr); return false; }
    dcomp->CreateTargetForHwnd(overlay, TRUE, &dtarget);
    dcomp->CreateVisual(&dvisual);
    dvisual->SetContent(swap);
    dtarget->SetRoot(dvisual);
    dcomp->Commit();
    ID3D11Texture2D* bb = nullptr;
    swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb);
    dev->CreateRenderTargetView(bb, nullptr, &backRtv);
    bb->Release();
    return true;
}

static void MakeBuffers(DXGI_FORMAT f)
{
    capFmt = f;
    for (int i = 0; i < 2; i++)
    {
        MakeTex(frame[i], W, H, f, false, false);
        MakeTex(luma4[i], std::max(1, W / 4), std::max(1, H / 4), DXGI_FORMAT_R16_FLOAT, true, false);
        MakeTex(luma16[i], std::max(1, W / 16), std::max(1, H / 16), DXGI_FORMAT_R16_FLOAT, true, false);
    }
    // coarse: 4x4 blocks at 1/16 (64 px); fine and smooth: 8x8 blocks at 1/4 (32 px)
    MakeTex(mvCoarse, (luma16[0].w + 3) / 4, (luma16[0].h + 3) / 4, DXGI_FORMAT_R16G16_FLOAT, false, true);
    MakeTex(mvFine, (luma4[0].w + 7) / 8, (luma4[0].h + 7) / 8, DXGI_FORMAT_R16G16_FLOAT, false, true);
    MakeTex(mvSmooth, mvFine.w, mvFine.h, DXGI_FORMAT_R16G16_FLOAT, false, true);
    havePair = false;
    prevValid = false;
}

// ------------------------------------------------------------------ game window
static HWND FindGame()
{
    HWND h = FindWindowW(L"UnityWndClass", L"RimWorld by Ludeon Studios");
    if (!h) h = FindWindowW(nullptr, L"RimWorld by Ludeon Studios");
    return h;
}

static bool GameRect(RECT& r)
{
    RECT c;
    if (!GetClientRect(game, &c)) return false;
    POINT p = { 0, 0 };
    ClientToScreen(game, &p);
    r = { p.x, p.y, p.x + c.right, p.y + c.bottom };
    return c.right > 16 && c.bottom > 16;
}

static void ReleaseAll()
{
    for (int i = 0; i < 2; i++) { frame[i].Free(); luma4[i].Free(); luma16[i].Free(); }
    mvCoarse.Free(); mvFine.Free(); mvSmooth.Free();
    Rel(backRtv); Rel(dvisual); Rel(dtarget); Rel(dcomp); Rel(swap);
    Rel(dupl);
    Rel(vs); Rel(psLumaDown); Rel(psDown); Rel(psInterp); Rel(csCoarse); Rel(csFine); Rel(csMedian);
    Rel(sLin); Rel(sPt); Rel(cbP); Rel(cbC);
    Rel(ctx); Rel(dev); Rel(output); Rel(adapter);
    if (overlay) { DestroyWindow(overlay); overlay = nullptr; }
    capFmt = DXGI_FORMAT_UNKNOWN;
}

// ------------------------------------------------------------------ per frame
static void Motion()
{
    int p = 1 - cur, c = cur;
    Pass(psLumaDown, luma4[c].rtv, luma4[c].w, luma4[c].h, frame[c].srv);
    Pass(psDown, luma16[c].rtv, luma16[c].w, luma16[c].h, luma4[c].srv);
    if (!havePair) return;
    Dispatch(csCoarse, mvCoarse, luma16[p].srv, luma16[c].srv, nullptr, { 4, 10, 1, 1 });
    // fine block (8 px at 1/4) = 2 coarse pixels at 1/16 ... coarse block 4 px at 1/16 = 16 px at 1/4 = 2 fine blocks
    Dispatch(csFine, mvFine, luma4[p].srv, luma4[c].srv, mvCoarse.srv, { 8, 3, 4, 2 });
    Dispatch(csMedian, mvSmooth, nullptr, nullptr, mvFine.srv, { 0, 0, 0, 0 });
    Unbind();
}

// a new picture from the game?
static bool Grab()
{
    DXGI_OUTDUPL_FRAME_INFO info;
    IDXGIResource* res = nullptr;
    HRESULT hr = dupl->AcquireNextFrame(0, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
    if (hr == DXGI_ERROR_ACCESS_LOST) { Say("Capture lost (screen mode changed) – starting again."); StartCapture(); return false; }
    if (FAILED(hr)) return false;
    bool got = false;
    if (info.LastPresentTime.QuadPart != 0 && info.AccumulatedFrames > 0)
    {
        ID3D11Texture2D* t = nullptr;
        res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t);
        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
        if (d.Format != capFmt) MakeBuffers(d.Format);
        D3D11_BOX box;
        box.left = std::max<LONG>(0, gameRect.left - outRect.left);
        box.top = std::max<LONG>(0, gameRect.top - outRect.top);
        box.right = std::min<LONG>((LONG)d.Width, box.left + W);
        box.bottom = std::min<LONG>((LONG)d.Height, box.top + H);
        box.front = 0; box.back = 1;
        if (box.right > box.left && box.bottom > box.top)
        {
            double now = Now();
            double gap = now - tNew;
            int next = 1 - cur;
            ctx->CopySubresourceRegion(frame[next].t, 0, 0, 0, 0, t, 0, &box);
            cur = next;
            if (gap > 0.004 && gap < 0.25) frameT = frameT * 0.85 + gap * 0.15;
            bool stale = gap > frameT * 3.0 || gap > 0.25;
            tPrevNew = tNew;
            tNew = now;
            havePair = prevValid && !stale;
            Motion();
            prevValid = true;
            capCount++;
            got = true;
        }
        t->Release();
    }
    res->Release();
    dupl->ReleaseFrame();
    return got;
}

static void Present()
{
    float a = 1.0f;
    if (havePair) a = (float)std::min(1.0, std::max(0.0, (Now() - tNew) / frameT));
    struct { float alpha, debug; float sx, sy; } pc = { a, debugView ? 1.0f : 0.0f, 4.0f / W, 4.0f / H };
    if (!havePair) pc.alpha = 1.0f;
    ctx->UpdateSubresource(cbP, 0, nullptr, &pc, 0, 0);
    ctx->PSSetConstantBuffers(0, 1, &cbP);
    ID3D11SamplerState* ss[2] = { sLin, sPt };
    ctx->PSSetSamplers(0, 2, ss);
    ctx->VSSetShader(vs, nullptr, 0);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->IASetInputLayout(nullptr);
    ID3D11ShaderResourceView* prev = havePair ? frame[1 - cur].srv : frame[cur].srv;
    Pass(psInterp, backRtv, W, H, prev, frame[cur].srv, mvSmooth.srv);
    Unbind();
    swap->Present(1, 0);
    outCount++;
}

// ------------------------------------------------------------------ main
int main()
{
    SetConsoleOutputCP(CP_UTF8);
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    SetConsoleTitleW(L"Smooth Frames");
    printf("Smooth Frames %s – frame generation for RimWorld (Bolek98)\n", kVersion);
    printf("  Ctrl+Alt+F  on / off\n  Ctrl+Alt+M  show motion (debug)\n  Ctrl+Alt+Q  quit\n");
    printf("Tip: play in borderless window and limit the game's FPS to half of your monitor's Hz.\n\n");
    RegisterHotKey(nullptr, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'F');
    RegisterHotKey(nullptr, 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'M');
    RegisterHotKey(nullptr, 3, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'Q');
    timeBeginPeriod(1);

    bool ready = false, shown = false, waitingSaid = false;
    double lastStat = Now();
    while (running)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_HOTKEY)
            {
                if (msg.wParam == 1) { enabled = !enabled; Say(enabled ? "ON" : "OFF"); }
                if (msg.wParam == 2) { debugView = !debugView; Say(debugView ? "Motion view on" : "Motion view off"); }
                if (msg.wParam == 3) running = false;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        if (!game || !IsWindow(game))
        {
            if (ready) { ReleaseAll(); ready = false; shown = false; Say("The game closed."); }
            game = FindGame();
            if (!game) { if (!waitingSaid) { Say("Waiting for RimWorld…"); waitingSaid = true; } Sleep(500); continue; }
            Say("RimWorld found.");
            waitingSaid = false;
        }

        RECT r;
        bool active = enabled && GetForegroundWindow() == game && !IsIconic(game) && GameRect(r);
        if (!active)
        {
            if (shown) { ShowWindow(overlay, SW_HIDE); shown = false; Rel(dupl); }
            Sleep(30);
            continue;
        }
        if (!ready || r.left != gameRect.left || r.top != gameRect.top || r.right - r.left != W || r.bottom - r.top != H)
        {
            ReleaseAll();
            gameRect = r; W = r.right - r.left; H = r.bottom - r.top;
            HMONITOR mon = MonitorFromWindow(game, MONITOR_DEFAULTTONEAREST);
            if (!InitDevice(mon) || !MakeOverlay() || !MakeSwap() || !StartCapture())
            {
                Say("Could not start – trying again in 5 s.");
                ReleaseAll(); ready = false; shown = false;
                Sleep(5000);
                continue;
            }
            Say("Running on %dx%d.", W, H);
            ready = true; shown = false;
        }
        if (!dupl && !StartCapture()) { Sleep(500); continue; }
        Grab();
        if (capFmt == DXGI_FORMAT_UNKNOWN) { Sleep(1); continue; }   // no picture yet
        if (!shown)
        {
            SetWindowPos(overlay, HWND_TOPMOST, gameRect.left, gameRect.top, W, H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
        }
        Present();      // waits for the monitor (vsync)

        double now = Now();
        if (now - lastStat > 5.0)
        {
            double s = now - lastStat;
            Say("game %.0f fps -> shown %.0f fps%s", capCount / s, outCount / s, debugView ? " (motion view)" : "");
            capCount = outCount = 0; lastStat = now;
        }
    }
    ReleaseAll();
    timeEndPeriod(1);
    return 0;
}
