using System.Diagnostics;
using System.IO;

namespace XhMonitor.Desktop.Services;

public sealed class InstallerLauncher : IInstallerLauncher
{
    public bool TryLaunch(string installerPath, out string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            });

            message = "已弹出安装程序。若取消安装，可再次点击“安装”。";
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "已取消安装，可再次点击“安装”。";
            return false;
        }
        catch (Exception ex)
        {
            message = $"启动安装程序失败：{ex.Message}";
            return false;
        }
    }
}
