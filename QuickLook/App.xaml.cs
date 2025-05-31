// Copyright © 2017-2025 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using Microsoft.Win32;
using QuickLook.Common.Helpers;
using QuickLook.Helpers;
using QuickLook.NativeMethods;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Violeta.Appearance;

namespace QuickLook;

public partial class App : Application
{
    public static readonly string LocalDataPath = SettingHelper.LocalDataPath;
    public static readonly string UserPluginPath = Path.Combine(SettingHelper.LocalDataPath, "QuickLook.Plugin\\");
    public static readonly string AppFullPath = Assembly.GetExecutingAssembly().Location;
    public static readonly string AppPath = Path.GetDirectoryName(AppFullPath);
    public static readonly bool Is64Bit = Environment.Is64BitProcess;
    public static readonly bool IsUWP = ProcessHelper.IsRunningAsUWP();
    public static readonly bool IsWin11 = Environment.OSVersion.Version >= new Version(10, 0, 21996);
    public static readonly bool IsWin10 = !IsWin11 && Environment.OSVersion.Version >= new Version(10, 0);
    public static readonly bool IsGPUInBlacklist = SystemHelper.IsGPUInBlacklist();
    public static readonly bool IsPortable = SettingHelper.IsPortableVersion();

    /// <summary>
    /// When set, QuickLook will close once the preview window is closed. This is
    /// useful for file type associations where QuickLook should behave like a
    /// normal viewer.
    /// </summary>
    public static bool ExitAfterPreview { get; private set; }

    private bool _cleanExit = true;
    private Mutex _isRunning;

    static App()
    {
        // Explicitly set to PerMonitor to avoid being overridden by the system
        if (SHCore.SetProcessDpiAwareness(SHCore.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE) is uint result)
        {
            Debug.WriteLine(
                result == 0 ?
                "DPI Awareness applied successfully" :
                $"DPI Awareness manual setup failed. Error Code: {result}"
            );
        }

        // Occurs when the resolution of an assembly fails
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            // Ignore the resource fails
            // e.g. "QuickLook.resources, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"
            if (e.Name.Contains(".resources,"))
            {
                return null;
            }

            try
            {
                // Manually resolve the assembly fails
                // https://github.com/QL-Win/QuickLook/issues/1618
                // e.g. "System.Memory, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"
                if (e.Name.Split(',').FirstOrDefault() is string assemblyName)
                {
                    foreach (var libPath in FetchFiles(AppDomain.CurrentDomain.BaseDirectory, assemblyName + ".dll"))
                    {
                        return Assembly.LoadFrom(libPath);
                    }
                }
            }
            catch
            {
                // There is no way to resolve it
            }

            return null;

            static IEnumerable<string> FetchFiles(string rootPath, string targetFileName)
            {
                foreach (var file in Directory.GetFiles(rootPath, "*" + Path.GetExtension(targetFileName), SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileName(file), targetFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Exception handling events which are not caught in the Task thread
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                ProcessHelper.WriteLog(e.Exception.ToString());
                Current?.Dispatcher?.BeginInvoke(() =>
                {
                    Wpf.Ui.Violeta.Controls.ExceptionReport.Show(e.Exception);
                });
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
            }
            finally
            {
                e.SetObserved();
            }
        };

        // Exception handling events which are not caught in UI thread
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                ProcessHelper.WriteLog(e.Exception.ToString());
                Current?.Dispatcher?.BeginInvoke(() =>
                {
                    Wpf.Ui.Violeta.Controls.ExceptionReport.Show(e.Exception);
                });
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
            }
            finally
            {
                e.Handled = true;
            }
        };

        // Exception handling events which are not caught in Non-UI thread
        // Such as a child thread created by ourself
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    ProcessHelper.WriteLog(ex.ToString());
                    Current?.Dispatcher?.BeginInvoke(() =>
                    {
                        Wpf.Ui.Violeta.Controls.ExceptionReport.Show(ex);
                    });
                }
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
            }
            finally
            {
                // Ignore
            }
        };

        // Initialize MessageBox patching
        bool modernMessageBox = SettingHelper.Get("ModernMessageBox", true, "QuickLook");
        if (modernMessageBox) MessageBoxPatcher.Initialize();
        // Initialize TrayIcon patching
        bool modernTrayIcon = SettingHelper.Get("ModernTrayIcon", true, "QuickLook");
        if (modernTrayIcon) TrayIconPatcher.Initialize();

        // Set initial theme based on system settings
        ThemeManager.Apply(OSThemeHelper.AppsUseDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light);
        SystemEvents.UserPreferenceChanged += (_, _) =>
            ThemeManager.Apply(OSThemeHelper.AppsUseDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light);
        UxTheme.ApplyPreferredAppMode();

        base.OnStartup(e);
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        string pathArg = null;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("/viewer", StringComparison.OrdinalIgnoreCase))
            {
                ExitAfterPreview = true;
            }
            else if (pathArg == null && !arg.StartsWith("/"))
            {
                pathArg = arg;
            }
        }

        if (!EnsureOSVersion()
            || !EnsureFirstInstance(e.Args)
            || !EnsureFolderWritable(SettingHelper.LocalDataPath))
        {
            _cleanExit = false;
            Shutdown();
            return;
        }

        CheckUpdate();
        RunListener(e);

        // first instance: run and preview this file
        if (!string.IsNullOrEmpty(pathArg))
            PipeServerManager.SendMessage(PipeMessages.Toggle, pathArg);
    }

    private bool EnsureOSVersion()
    {
        if (!ProcessHelper.IsOnWindows10S())
            return true;

        MessageBox.Show("This application does not run on Windows 10 S.");

        return false;
    }

    private bool EnsureFolderWritable(string folder)
    {
        try
        {
            var path = FileHelper.CreateTempFile(folder);
            File.Delete(path);
        }
        catch
        {
            MessageBox.Show(string.Format(TranslationHelper.Get("APP_PATH_NOT_WRITABLE"), folder), "QuickLook",
                MessageBoxButton.OK, MessageBoxImage.Error);

            return false;
        }

        return true;
    }

    private void CheckUpdate()
    {
        if (DateTime.Now.Ticks - SettingHelper.Get<long>("LastUpdateTicks") < TimeSpan.FromDays(30).Ticks)
            return;

        Task.Delay(120 * 1000).ContinueWith(_ => Updater.CheckForUpdates(true));
        SettingHelper.Set("LastUpdateTicks", DateTime.Now.Ticks);
    }

    private void RunListener(StartupEventArgs e)
    {
        TrayIconManager.GetInstance();
        if (!e.Args.Contains("/autorun") && !IsUWP)
            TrayIconManager.ShowNotification(string.Empty, TranslationHelper.Get("APP_START"));
        if (e.Args.Contains("/first"))
            AutoStartupHelper.CreateAutorunShortcut();

        NativeMethods.QuickLook.Init();

        PluginManager.GetInstance();
        ViewWindowManager.GetInstance();
        KeystrokeDispatcher.GetInstance();
        PipeServerManager.GetInstance();
    }

    private void App_OnExit(object sender, ExitEventArgs e)
    {
        if (!_cleanExit)
            return;

        _isRunning.ReleaseMutex();

        PipeServerManager.GetInstance().Dispose();
        TrayIconManager.GetInstance().Dispose();
        KeystrokeDispatcher.GetInstance().Dispose();
        ViewWindowManager.GetInstance().Dispose();
    }

    private bool EnsureFirstInstance(string[] args)
    {
        _isRunning = new Mutex(true, "QuickLook.App.Mutex", out bool isFirst);

        if (isFirst)
            return true;

        string pathArg = args.FirstOrDefault(a => !a.StartsWith("/"));
        string[] options = args.Where(a => a != pathArg).ToArray();

        // second instance: preview this file
        if (!string.IsNullOrEmpty(pathArg) && (Directory.Exists(pathArg) || File.Exists(pathArg)))
        {
            PipeServerManager.SendMessage(PipeMessages.Toggle, pathArg, options);
        }
        // second instance: duplicate
        else
            MessageBox.Show(TranslationHelper.Get("APP_SECOND_TEXT"), TranslationHelper.Get("APP_SECOND"),
                MessageBoxButton.OK, MessageBoxImage.Information);

        return false;
    }
}
