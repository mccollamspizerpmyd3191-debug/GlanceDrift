#include <windows.h>
#include <d3d11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <cstring>
#include <memory>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

// 只在两倍模式接收指定窗口的合成画面，回读的范围限于鼠标附近。
struct Capture {
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    IDirect3DDevice runtimeDevice{nullptr};
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    Direct3D11CaptureFrame latest{nullptr};
    com_ptr<ID3D11Texture2D> staging;
    winrt::Windows::Graphics::SizeInt32 poolSize{};
    RECT previous{};
    int width=0, height=0;

    explicit Capture(HWND source) {
        auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        check_hresult(interop->CreateForWindow(source, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            device.put(), nullptr, context.put()));
        auto dxgi = device.as<IDXGIDevice>();
        com_ptr<::IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        runtimeDevice = inspectable.as<IDirect3DDevice>();
        poolSize = item.Size();
        pool = Direct3D11CaptureFramePool::CreateFreeThreaded(runtimeDevice,
            DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
        session = pool.CreateCaptureSession(item);
        if (auto cursor = session.try_as<IGraphicsCaptureSession2>()) cursor.IsCursorCaptureEnabled(false);
        session.StartCapture();
    }
    ~Capture() {
        try { if(session) session.Close(); if(latest) latest.Close(); if(pool) pool.Close(); } catch(...) {}
    }
    int read(RECT outer, RECT visible, RECT crop, unsigned char* pixels, int stride) {
        auto frame = pool.TryGetNextFrame();
        if (frame) {
            auto size = frame.ContentSize();
            if (size.Width <= 0 || size.Height <= 0) return 0;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height) {
                if (latest) { latest.Close(); latest = nullptr; }
                frame.Close();
                poolSize = size;
                pool.Recreate(runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
                return 0;
            }
            if(latest) latest.Close();
            latest = frame;
        } else if (!latest) return 0;
        else if (EqualRect(&crop, &previous)) return 2;

        auto access = latest.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),texture.put_void()));
        // 窗口捕获可能从可见边框或外框起算，以当前帧的尺寸识别。
        auto error = [&](RECT r) { return abs(r.right-r.left-poolSize.Width)+abs(r.bottom-r.top-poolSize.Height); };
        RECT origin = error(visible) < error(outer) ? visible : outer;
        int x=crop.left-origin.left, y=crop.top-origin.top, w=crop.right-crop.left, h=crop.bottom-crop.top;
        if(x<0 || y<0 || x+w>poolSize.Width || y+h>poolSize.Height) return 0;
        if(w!=width || h!=height) {
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width=w; desc.Height=h; desc.MipLevels=1; desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count=1;
            desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            staging=nullptr;
            check_hresult(device->CreateTexture2D(&desc,nullptr,staging.put()));
            width=w; height=h;
        }
        D3D11_BOX box{UINT(x),UINT(y),0,UINT(x+w),UINT(y+h),1};
        context->CopySubresourceRegion(staging.get(),0,0,0,0,texture.get(),0,&box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check_hresult(context->Map(staging.get(),0,D3D11_MAP_READ,0,&mapped));
        for(int row=0;row<h;row++)
            memcpy(pixels+row*stride,static_cast<unsigned char*>(mapped.pData)+row*mapped.RowPitch,w*4);
        context->Unmap(staging.get(),0);
        previous=crop;
        return 1;
    }
};

extern "C" __declspec(dllexport) void* OpenCapture(HWND window) noexcept {
    try { return new Capture(window); } catch(...) { return nullptr; }
}
extern "C" __declspec(dllexport) int ReadCrop(Capture* c, RECT outer, RECT visible, RECT crop,
    unsigned char* pixels, int stride) noexcept {
    try { return c->read(outer,visible,crop,pixels,stride); } catch(...) { return -1; }
}
extern "C" __declspec(dllexport) void CloseCapture(Capture* c) noexcept { delete c; }
