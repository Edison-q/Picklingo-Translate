using Picklingo.Interop;

namespace Picklingo.Services;

/// <summary>
/// 全局热键服务：在指定窗口句柄上注册一个系统级热键，触发时抛出 <see cref="Pressed"/> 事件。
/// 注册失败（例如被其他软件占用）由调用方决定如何提示。
/// </summary>
internal sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 1;

    private readonly IntPtr _hwnd;
    private bool _registered;

    /// <summary>热键被按下时触发（在 UI 线程的消息循环上）。</summary>
    public event Action? Pressed;

    public HotKeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>注册热键；返回是否成功。modifiers 用 MOD_* 常量组合，vk 为虚拟键码。</summary>
    public bool Register(uint modifiers, uint vk)
    {
        _registered = NativeMethods.RegisterHotKey(_hwnd, HotKeyId, modifiers | NativeMethods.MOD_NOREPEAT, vk);
        return _registered;
    }

    /// <summary>由窗口消息处理函数调用：命中本热键则触发事件。</summary>
    public void HandleWindowMessage(int msg, IntPtr wParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            Pressed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotKeyId);
            _registered = false;
        }
    }
}
