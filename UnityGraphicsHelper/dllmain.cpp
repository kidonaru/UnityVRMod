#include "pch.h"
#include "framework.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <fstream>
#include <string>
#include <chrono>
#include <iomanip>
#include <sstream>
#include <windows.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include <d3d12.h>
#include "IUnityGraphicsD3D12.h"
#include <dxgi.h>
#include <dxgi1_4.h>
#pragma comment(lib, "dxgi.lib")

#ifndef E_FAIL
#define E_FAIL 0x80004005
#endif

static ID3D11Device* g_D3D11Device = nullptr;
static ID3D11DeviceContext* s_ImmediateContext = nullptr;

// ===== D3D12 backend（render-thread 駆動 copy。設計 = bg2-vr-d3d12-migration-phase1-design.md §4） =====
static IUnityGraphicsD3D12v7* s_D3D12 = nullptr;     // D3D12 時のみ非 null

// copy 要求リング: main thread が EnqueueCopyD3D12 で積み、render thread の OnRenderEvent が drain する。
// チケット = 1 始まり連番。completed は「ExecuteCommandList まで終えた」最大チケット。
struct PendingCopyD3D12 { ID3D12Resource* src; ID3D12Resource* dst; };
static const int kCopyRingSize = 64;
static PendingCopyD3D12 s_copyRing[kCopyRingSize];
static volatile LONG s_enqueuedTicket = 0;
static volatile LONG s_completedTicket = 0;
static CRITICAL_SECTION s_ringLock;
static bool s_ringLockInit = false;

// allocator/CL プール。再利用判定はフレーム fence（ExecuteCommandList の戻り値 = 現フレーム完了時に
// セットされる値・CL 単位の完了値ではない＝同一フレーム内の複数 event は同じ値を持ち再利用不可）。
// 16 = 3 event/frame × 5 フレーム分の余裕（設計 §4「fence 意味論の前提」）。
struct CopyCmdSlot { ID3D12CommandAllocator* alloc; ID3D12GraphicsCommandList* cl; UINT64 fenceValue; };
static const int kCopySlotCount = 16;
static CopyCmdSlot s_copySlots[kCopySlotCount] = {};

// init 一発 event で render thread から取得して cache する device/queue
static void* volatile s_cachedD3D12Device = nullptr;
static void* volatile s_cachedD3D12Queue = nullptr;

// DXGI memory info 用 adapter cache（QueryVideoMemoryInfo 経路。診断用）。
// 初回呼び出しで device→LUID→Factory→Adapter3 を解決し以降は使い回す。
static IDXGIAdapter3* s_cachedDxgiAdapter3 = nullptr;

enum { kEventCacheDeviceObjects = 1, kEventExecutePendingCopies = 2 };

// UnityPluginLoad で受領した IUnityInterfaces。
// 注意: 本 dll が UnityPluginLoad を受け取るには Unity の plugin manager 経由でロードされる必要がある
// ＝配置先は BepInEx 配下ではなく <Game>_Data/Plugins/x86_64/（素の LoadLibrary では呼ばれない。実害 2026-06-12:
// BepInEx 配下配置で UnityPluginLoad 不発 → s_D3D12 null → backend 判定が常に D3D11 に倒れた）。
static IUnityInterfaces* s_UnityInterfaces = nullptr;

// graphics backend の確定（D3D11 / D3D12）。UnityPluginLoad と IsD3D12Active（lazy 再試行）から呼ぶ。
// eager load 等で UnityPluginLoad が graphics device 初期化前に来ると GetRenderer() が確定しないため、
// 確定するまで何度呼んでも安全な形にしている（確定後は no-op）。
static void TryInitGraphicsBackend()
{
    if (!s_UnityInterfaces) return;
    if (g_D3D11Device || s_D3D12) return;   // 確定済み

    IUnityGraphics* graphics = s_UnityInterfaces->Get<IUnityGraphics>();
    if (graphics && graphics->GetRenderer() == kUnityGfxRendererD3D11)
    {
        IUnityGraphicsD3D11* d3d11 = s_UnityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11)
        {
            g_D3D11Device = d3d11->GetDevice();
            if (g_D3D11Device)
            {
                g_D3D11Device->GetImmediateContext(&s_ImmediateContext);
            }
        }
    }
    else if (graphics && graphics->GetRenderer() == kUnityGfxRendererD3D12)
    {
        s_D3D12 = s_UnityInterfaces->Get<IUnityGraphicsD3D12v7>();
        if (s_D3D12 && !s_ringLockInit)
        {
            InitializeCriticalSection(&s_ringLock);
            s_ringLockInit = true;

            // event の precondition 宣言（設計 §4・plan-review 🔴#3）。
            // FlushCommandBuffers = event 実行前に Unity の記録済みコマンドバッファを queue へ submit させる
            // ＝自前 CL が「src への描画（camera.Render/Blit/GL.Clear）より後」に載る順序保証の本体。
            UnityD3D12PluginEventConfig copyCfg = {};
            copyCfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_Allow;
            copyCfg.flags = kUnityD3D12EventConfigFlag_FlushCommandBuffers;
            copyCfg.ensureActiveRenderTextureIsBound = false;
            s_D3D12->ConfigureEvent(kEventExecutePendingCopies, &copyCfg);

            UnityD3D12PluginEventConfig cacheCfg = {};
            cacheCfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_Allow;  // GetCommandQueue を呼ぶ
            cacheCfg.flags = 0;
            cacheCfg.ensureActiveRenderTextureIsBound = false;
            s_D3D12->ConfigureEvent(kEventCacheDeviceObjects, &cacheCfg);
        }
    }
}

extern "C" __declspec(dllexport) void UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    s_UnityInterfaces = unityInterfaces;
    TryInitGraphicsBackend();
}

extern "C" __declspec(dllexport) void UnityPluginUnload()
{
    if (s_ImmediateContext)
    {
        s_ImmediateContext->Release();
        s_ImmediateContext = nullptr;
    }
    g_D3D11Device = nullptr;

    if (s_cachedDxgiAdapter3)
    {
        s_cachedDxgiAdapter3->Release();
        s_cachedDxgiAdapter3 = nullptr;
    }
}

extern "C" __declspec(dllexport) void SetDevicePointerFromCSharp(void* deviceFromCSharp)
{
    ID3D11Device* newDevice = static_cast<ID3D11Device*>(deviceFromCSharp);
    if (newDevice != g_D3D11Device)
    {
        if (s_ImmediateContext)
        {
            s_ImmediateContext->Release();
            s_ImmediateContext = nullptr;
        }
        g_D3D11Device = newDevice;
        if (g_D3D11Device)
        {
            g_D3D11Device->GetImmediateContext(&s_ImmediateContext);
        }
    }
}

extern "C" __declspec(dllexport) void* GetD3D11Device()
{
    return g_D3D11Device;
}

extern "C" __declspec(dllexport) void* GetDeviceFromResource(void* pResource)
{
    if (!pResource) return nullptr;
    ID3D11Resource* d3d11Resource = static_cast<ID3D11Resource*>(pResource);
    ID3D11Device* d3d11Device = nullptr;
    d3d11Resource->GetDevice(&d3d11Device);
    return d3d11Device;
}

extern "C" __declspec(dllexport) void DirectCopyResource(void* pDest, void* pSrc)
{
    if (!s_ImmediateContext || !pDest || !pSrc) return;
    ID3D11Resource* pDestResource = static_cast<ID3D11Resource*>(pDest);
    ID3D11Resource* pSrcResource = static_cast<ID3D11Resource*>(pSrc);
    s_ImmediateContext->CopyResource(pDestResource, pSrcResource);
}

extern "C" __declspec(dllexport) HRESULT CreateAndRegisterSRV(void* pTextureResource, int srvFormatDXGI, void** ppSRV)
{
    if (!g_D3D11Device || !pTextureResource) 
    {
        if (ppSRV) *ppSRV = nullptr;
        return E_FAIL;
    }
    
    ID3D11Texture2D* pTexture2D = static_cast<ID3D11Texture2D*>(pTextureResource);
    D3D11_TEXTURE2D_DESC texDesc;
    pTexture2D->GetDesc(&texDesc);

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = static_cast<DXGI_FORMAT>(srvFormatDXGI);
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MostDetailedMip = 0;
    srvDesc.Texture2D.MipLevels = (texDesc.MipLevels == 0) ? -1 : texDesc.MipLevels;

    ID3D11ShaderResourceView* pNewSRV = nullptr;
    HRESULT hr = g_D3D11Device->CreateShaderResourceView(pTexture2D, &srvDesc, &pNewSRV);

    if (SUCCEEDED(hr)) 
    {
        if (ppSRV) *ppSRV = pNewSRV;
    } 
    else 
    {
        if (ppSRV) *ppSRV = nullptr;
    }
    return hr;
}

extern "C" __declspec(dllexport) void ReleaseNativeObject(void* pObject)
{
    if (pObject)
    {
        ((IUnknown*)pObject)->Release();
    }
}

// render thread で実行される copy 実体。リング未処理分を 1 つの CL にまとめ、
// src（Unity 所有 RT）は resource state 宣言で Unity に barrier を任せ、
// dst（XR swapchain image）は acquire 中 state = RENDER_TARGET（XR_KHR_D3D12_enable 仕様）を
// COPY_DEST に落として copy 後に戻す。
static void UNITY_INTERFACE_API OnRenderEvent(int eventId)
{
    if (!s_D3D12) return;

    if (eventId == kEventCacheDeviceObjects)
    {
        s_cachedD3D12Device = s_D3D12->GetDevice();
        s_cachedD3D12Queue = s_D3D12->GetCommandQueue();
        return;
    }
    if (eventId != kEventExecutePendingCopies) return;

    LONG from = s_completedTicket;   // callback は render thread 単一実行＝completed の読み書きはここだけ
    LONG to = s_enqueuedTicket;
    if (from >= to) return;
    if (to - from > kCopyRingSize) return; // 防御（理論上 Enqueue 側の満杯ガードで起きない）

    ID3D12Device* dev = s_D3D12->GetDevice();
    ID3D12Fence* frameFence = s_D3D12->GetFrameFence();
    if (!dev || !frameFence) return;

    // 空きスロット（fence 完了済み）を探す。無ければ今回は見送り（チケット据え置き→次 event で再試行）
    CopyCmdSlot* slot = nullptr;
    UINT64 completedFence = frameFence->GetCompletedValue();
    for (int i = 0; i < kCopySlotCount; i++)
        if (s_copySlots[i].fenceValue <= completedFence) { slot = &s_copySlots[i]; break; }
    if (!slot) return;

    if (!slot->alloc && FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot->alloc)))) return;
    if (!slot->cl)
    {
        if (FAILED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot->alloc, nullptr, IID_PPV_ARGS(&slot->cl)))) return;
    }
    else
    {
        if (FAILED(slot->alloc->Reset())) return;
        if (FAILED(slot->cl->Reset(slot->alloc, nullptr))) return;
    }

    UnityGraphicsD3D12ResourceState srcStates[kCopyRingSize];
    int stateCount = 0;
    for (LONG t = from + 1; t <= to; t++)
    {
        PendingCopyD3D12& pc = s_copyRing[(t - 1) % kCopyRingSize];
        // EnqueueCopyD3D12 が null を弾くため src/dst は常に非 null（不変条件）。万一 null でも
        // 「copy 対象なし＝そのチケットは vacuously 完了」なので skip して completed には含めてよい。
        if (!pc.src || !pc.dst) continue;

        D3D12_RESOURCE_BARRIER toCopy = {};
        toCopy.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        toCopy.Transition.pResource = pc.dst;
        toCopy.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        toCopy.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        toCopy.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
        slot->cl->ResourceBarrier(1, &toCopy);

        slot->cl->CopyResource(pc.dst, pc.src);

        D3D12_RESOURCE_BARRIER toRT = toCopy;
        toRT.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        toRT.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        slot->cl->ResourceBarrier(1, &toRT);

        srcStates[stateCount].resource = pc.src;
        srcStates[stateCount].expected = D3D12_RESOURCE_STATE_COPY_SOURCE;
        srcStates[stateCount].current = D3D12_RESOURCE_STATE_COPY_SOURCE;
        stateCount++;
    }

    if (FAILED(slot->cl->Close())) return;
    // ExecuteCommandList の戻り値はエラーコードではなくフレーム fence 値（worker thread submit）。
    // completed を to へ進めるのは Create/Reset/Close の全失敗パス（上の早期 return）を通過した後のみ＝
    // 「submit を発行できたチケットまで」に限定される。Close 等で失敗したフレームは completed 据え置きで次 event 再試行。
    slot->fenceValue = s_D3D12->ExecuteCommandList(slot->cl, stateCount, srcStates);
    InterlockedExchange(&s_completedTicket, to);
}

extern "C" __declspec(dllexport) int IsD3D12Active()
{
    // UnityPluginLoad が device 初期化前に来た場合に備えた lazy 再試行（確定済みなら no-op）。
    // C# の backend 判定は必ず最初にこれを呼ぶため、ここが再試行の単一ポイント。
    TryInitGraphicsBackend();
    return s_D3D12 ? 1 : 0;
}

extern "C" __declspec(dllexport) void* GetD3D12Device()
{
    // cache（render thread 取得）優先。未 cache 時は main thread 直読み fallback（getter のみ・設計 §4）。
    // GetDevice/GetCommandQueue は安定した device/queue ポインタを返す純 getter で、CL 記録（CommandRecordingState）を
    // 伴わない＝thread-agnostic。よって Allow 構成で submission thread 想定でも main thread 直読みで安全。
    if (s_cachedD3D12Device) return s_cachedD3D12Device;
    return s_D3D12 ? s_D3D12->GetDevice() : nullptr;
}

extern "C" __declspec(dllexport) void* GetD3D12CommandQueue()
{
    if (s_cachedD3D12Queue) return s_cachedD3D12Queue;
    return s_D3D12 ? s_D3D12->GetCommandQueue() : nullptr;
}

extern "C" __declspec(dllexport) UnityRenderingEvent GetRenderEventFunc()
{
    return OnRenderEvent;
}

extern "C" __declspec(dllexport) int EnqueueCopyD3D12(void* src, void* dst)
{
    if (!s_D3D12 || !s_ringLockInit || !src || !dst) return 0;
    EnterCriticalSection(&s_ringLock);
    LONG t = s_enqueuedTicket + 1;
    if (t - s_completedTicket > kCopyRingSize) { LeaveCriticalSection(&s_ringLock); return 0; }
    s_copyRing[(t - 1) % kCopyRingSize].src = (ID3D12Resource*)src;
    s_copyRing[(t - 1) % kCopyRingSize].dst = (ID3D12Resource*)dst;
    InterlockedExchange(&s_enqueuedTicket, t);
    LeaveCriticalSection(&s_ringLock);
    return (int)t;
}

extern "C" __declspec(dllexport) int GetCompletedCopyTicket()
{
    return (int)s_completedTicket;
}

// Step 2 診断: GPU 完了まで強制待ち（恒久 fix ではなく leak rate 計測用）。
// 既存 WaitForCopyTicket は ExecuteCommandList の submit 完了までしか待たない。本関数は Unity 共有 frame fence
// の `GetCompletedValue() >= slot->fenceValue` まで待つ＝コピーコマンドの GPU 完了まで。
// ticket 引数は記録目的（個別 slot を引かず、全 slot の最大 fenceValue で待つ＝累積分も巻き込んで完了させる）。
// 戻り値: 1=完了, 0=timeout/error.
extern "C" __declspec(dllexport) int WaitForCopyGpuComplete(int /*ticket*/, int timeoutMs)
{
    if (!s_D3D12) return 1;
    ID3D12Fence* frameFence = s_D3D12->GetFrameFence();
    if (!frameFence) return 1;

    // 全 slot の最大 fenceValue を取得。fenceValue は UINT64 = 8byte aligned write が x64 で atomic ＝
    // race の悪影響は古い値を読む程度（safe-by-construction で実害は「過小評価して早期 return」のみ）。
    UINT64 maxFence = 0;
    for (int i = 0; i < kCopySlotCount; i++)
        if (s_copySlots[i].fenceValue > maxFence) maxFence = s_copySlots[i].fenceValue;
    if (maxFence == 0) return 1;

    UINT64 completed = frameFence->GetCompletedValue();
    if (completed >= maxFence) return 1;

    HANDLE hEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!hEvent) return 0;
    int rc = 0;
    if (SUCCEEDED(frameFence->SetEventOnCompletion(maxFence, hEvent)))
    {
        DWORD dw = WaitForSingleObject(hEvent, timeoutMs > 0 ? (DWORD)timeoutMs : INFINITE);
        rc = (dw == WAIT_OBJECT_0) ? 1 : 0;
    }
    CloseHandle(hEvent);
    return rc;
}

// teardown 専用の安全網: 破棄予定 swapchain image を参照する未処理 copy を「実行せず完了扱い」に倒し、
// render thread が destroyed image へ CopyResource する use-after-free を防ぐ。drain（C# 側の有限待機）が
// timeout した後に呼ぶ。残存 race = render thread が既に該当 event を処理中のケースのみ（この窓は閉じきれない）。
extern "C" __declspec(dllexport) void CancelPendingCopiesD3D12()
{
    if (!s_D3D12 || !s_ringLockInit) return;
    EnterCriticalSection(&s_ringLock);
    InterlockedExchange(&s_completedTicket, s_enqueuedTicket);
    LeaveCriticalSection(&s_ringLock);
}

// 共有 GPU メモリ leak 調査用（診断専用・本番経路には影響なし）。
// ID3D12Device の adapter LUID から IDXGIAdapter3 を解決して cache し、
// DXGI_MEMORY_SEGMENT_GROUP_LOCAL / _NON_LOCAL の CurrentUsage / Budget を返す。
// NON_LOCAL = system memory backed segment = タスクマネージャ「共有 GPU メモリ」に対応するはず。
static IDXGIAdapter3* TryAcquireDxgiAdapter3()
{
    if (s_cachedDxgiAdapter3) return s_cachedDxgiAdapter3;
    if (!s_D3D12) return nullptr;

    // device は render thread cache 優先・main から直読みでも GetDevice() は thread-agnostic（既存方針と同じ）。
    ID3D12Device* dev = s_cachedD3D12Device
        ? reinterpret_cast<ID3D12Device*>(s_cachedD3D12Device)
        : s_D3D12->GetDevice();
    if (!dev) return nullptr;

    LUID luid = dev->GetAdapterLuid();

    IDXGIFactory4* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || !factory) return nullptr;

    IDXGIAdapter1* adapter1 = nullptr;
    HRESULT hr = factory->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter1));
    factory->Release();
    if (FAILED(hr) || !adapter1) return nullptr;

    IDXGIAdapter3* adapter3 = nullptr;
    hr = adapter1->QueryInterface(IID_PPV_ARGS(&adapter3));
    adapter1->Release();
    if (FAILED(hr) || !adapter3) return nullptr;

    s_cachedDxgiAdapter3 = adapter3;
    return s_cachedDxgiAdapter3;
}

extern "C" __declspec(dllexport) int QueryVideoMemoryInfo(
    uint64_t* localCurrent, uint64_t* localBudget,
    uint64_t* nonLocalCurrent, uint64_t* nonLocalBudget)
{
    if (!localCurrent || !localBudget || !nonLocalCurrent || !nonLocalBudget) return 0;
    *localCurrent = 0; *localBudget = 0; *nonLocalCurrent = 0; *nonLocalBudget = 0;

    IDXGIAdapter3* adapter3 = TryAcquireDxgiAdapter3();
    if (!adapter3) return 0;

    DXGI_QUERY_VIDEO_MEMORY_INFO local = {};
    DXGI_QUERY_VIDEO_MEMORY_INFO nonLocal = {};
    if (FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local))) return 0;
    if (FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal))) return 0;

    *localCurrent = local.CurrentUsage;
    *localBudget = local.Budget;
    *nonLocalCurrent = nonLocal.CurrentUsage;
    *nonLocalBudget = nonLocal.Budget;
    return 1;
}