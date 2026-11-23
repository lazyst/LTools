using System.Security.Principal;

namespace LTools.Core;

/// <summary>
/// 进程是否以管理员提权。两处用法都受它影响：UIPI 会拦住低完整性的拖放（见
/// <see cref="Views.FileDropBridge"/>），UIA 对提权窗口才读得到控件树。
/// </summary>
internal static class Elevation
{
    public static bool IsElevated { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
