using System.Security.Principal;

namespace LTools.Core;

/// <summary>
/// 进程是否以管理员提权。剩下的用法是「这件事只有提权才做得成吗」这一类判断；
/// UIA 对提权窗口才读得到控件树是其中一条（拖放已经不再是理由——窗口都住在普通权限宿主里）。
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
