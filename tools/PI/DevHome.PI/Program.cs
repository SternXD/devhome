﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DevHome.Common.Extensions;
using DevHome.Common.Helpers;
using DevHome.PI.Models;
using DevHome.Service.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;
using Windows.ApplicationModel.Activation;
using WinRT;

namespace DevHome.PI;

public static class Program
{
    private static App? _app;
    private static bool firstActivation = true;

    [global::System.Runtime.InteropServices.DllImport("Microsoft.ui.xaml.dll")]
    [global::System.Runtime.InteropServices.DefaultDllImportSearchPaths(global::System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    private static extern void XamlCheckProcessRequirements();

    private const string MainInstanceKey = "mainInstance";

    private const string ElevatedInstanceKey = "elevatedInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        Type? t = Type.GetTypeFromCLSID(new Guid("1F98F450-C163-4A99-B257-E1E6CB3E1C57"));
        Guid g = typeof(ITimServer).GUID;

        TimServer server = new TimServer();

        ITimServer? server2 = server as ITimServer;

        if (server2 is not null)
        {
            int num = server2.GetNumber();
        }

        // Set up Logging
        try
        {
            Environment.SetEnvironmentVariable("DEVHOME_LOGS_ROOT", Path.Join(Common.Logging.LogFolderRoot, "DevHomePI"));
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings_pi.json")
                .Build();
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set up logging: {ex.Message}");
        }

        var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, $"DevHomePI-{Environment.ProcessId}");
        ThreadPool.QueueUserWorkItem((o) =>
        {
            var waitResult = stopEvent.WaitOne();

            _app?.UIDispatcher?.TryEnqueue(() =>
            {
                var primaryWindow = Application.Current.GetService<PrimaryWindow>();
                primaryWindow.Close();
            });
        });

        try
        {
            XamlCheckProcessRequirements();

            WinRT.ComWrappersSupport.InitializeComWrappers();

            var isRedirect = DecideRedirection().GetAwaiter().GetResult();

            if (!isRedirect)
            {
                Log.Information("Starting application");
                Application.Start((p) =>
                {
                    var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                    var context = new DispatcherQueueSynchronizationContext(dispatcherQueue);
                    SynchronizationContext.SetSynchronizationContext(context);
                    _app = new App();
                    OnActivated(null, AppInstance.GetCurrent().GetActivatedEventArgs());
                });
            }

            stopEvent.Close();
            stopEvent.Dispose();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application start-up failed");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task<bool> DecideRedirection()
    {
        var activatedEventArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var isElevatedInstancePresent = false;
        var isUnElevatedInstancePresent = false;
        var instanceList = AppInstance.GetInstances();
        foreach (var appInstance in instanceList)
        {
            if (appInstance.Key.Equals(MainInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                isUnElevatedInstancePresent = true;
            }
            else if (appInstance.Key.Equals(ElevatedInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                isElevatedInstancePresent = true;
            }
        }

        AppInstance instance;
        if (isElevatedInstancePresent)
        {
            // Redirect to the elevated instance if present.
            instance = AppInstance.FindOrRegisterForKey(ElevatedInstanceKey);
        }
        else if (RuntimeHelper.IsCurrentProcessRunningAsAdmin())
        {
            // Wait for unelevated instance to exit
            while (isUnElevatedInstancePresent)
            {
                isUnElevatedInstancePresent = false;
                instanceList = AppInstance.GetInstances();
                foreach (var appInstance in instanceList)
                {
                    if (appInstance.Key.Equals(MainInstanceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        isUnElevatedInstancePresent = true;
                        var stopAppInstance = new EventWaitHandle(false, EventResetMode.ManualReset, $"DevHomePI-{appInstance.ProcessId}");
                        stopAppInstance.Set();
                    }
                }
            }

            // Register the elevated instance key
            instance = AppInstance.FindOrRegisterForKey(ElevatedInstanceKey);
        }
        else
        {
            instance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        }

        var isRedirect = false;
        if (instance.IsCurrent)
        {
            instance.Activated += OnActivated;
        }
        else
        {
            // Redirect the activation (and args) to the registered instance, and exit.
            await instance.RedirectActivationToAsync(activatedEventArgs);
            isRedirect = true;
        }

        return isRedirect;
    }

    private static void OnActivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        var wasFirstActivation = firstActivation;
        firstActivation = false;
        var commandLine = string.Empty;
        if (e.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
        {
            commandLine = e.Data.As<ILaunchActivatedEventArgs>().Arguments;
        }
        else if (e.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
        {
            // Start the app in the background to handle the startup task and register the hotkey
            if (wasFirstActivation && !App.IsFeatureEnabled())
            {
                // Exit the process if PI Expermental feature is not enabled and its the first activation in the process
                Log.Information("Experimental feature is not enabled. Exiting the process.");
                Process.GetCurrentProcess().Kill(false);
            }

            // Don't show the bar window for startup task activations.
            return;
        }

        // Convert commandLine into a string array. We just can't split based just on spaces, in case there are spaces inclosed in quotes
        // i.e. --application "My App"
        var commandLineArgs = Regex.Matches(commandLine, @"[\""].+?[\""]|[^ ]+").Select(m => m.Value).ToArray();

        // TODO: This should be replaced with system.commandline Microsoft.Extensions.Configuration
        //  is not intended to be a general purpose commandline parser, but rather only supports /key=value or /key value pairs
        var builder = new ConfigurationBuilder();
        builder.AddCommandLine(commandLineArgs);
        var config = builder.Build();

        Process? targetProcess = null;
        var targetApp = config["application"];
        var targetPid = config["pid"];
        var pageToExpand = config["expandWindow"];

        try
        {
            if (targetApp != null)
            {
                Debug.Assert(targetApp != string.Empty, "Why is appname empty?");

                Process[] processes = Process.GetProcessesByName(targetApp);
                if (processes.Length > 0)
                {
                    targetProcess = processes[0];
                }
            }
            else if (targetPid != null)
            {
                var pid = int.Parse(targetPid, CultureInfo.CurrentCulture);
                targetProcess = Process.GetProcessById(pid);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to find target process {TargetApp} {TargetPid}", targetApp, targetPid);
        }

        Debug.Assert(_app != null, "Why is _app null on a redirection?");

        // Be sure to set the target app on the UI thread
        _app?.UIDispatcher?.TryEnqueue(() =>
        {
            if (targetProcess != null)
            {
                TargetAppData.Instance.SetNewAppData(targetProcess, Windows.Win32.Foundation.HWND.Null);
            }

            // Show the bar window
            var primaryWindow = Application.Current.GetService<PrimaryWindow>();
            primaryWindow.ShowBarWindow();

            if (pageToExpand != null)
            {
                var barWindow = primaryWindow.DBarWindow;
                Debug.Assert(barWindow is not null, "We show the bar window, so it cannot be null here");

                var pageType = Type.GetType($"DevHome.PI.ViewModels.{pageToExpand}");
                if (pageType is not null)
                {
                    barWindow.NavigateTo(pageType);
                }
            }
        });
    }

    private sealed class Ole32
    {
        // https://docs.microsoft.com/windows/win32/api/wtypesbase/ne-wtypesbase-clsctx
        public const int _CLSCTX_LOCAL_SERVER = 0x4;

        // https://docs.microsoft.com/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstance
        [DllImport(nameof(Ole32))]
        public static extern int CoCreateInstance(
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}

/*

[ComImport]
[Guid("D1FF65D2-7CDA-489E-9AE0-701855C4F6A1")]
public interface ITimServer
{
    int GetJoke(out int prefix);
}
*/
[ComImport]
[Guid("1F98F450-C163-4A99-B257-E1E6CB3E1C57")]
public class TimServer;
