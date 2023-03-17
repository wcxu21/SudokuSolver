﻿using SudokuSolver.Utilities;
using SudokuSolver.Views;

using System.Text;

// not to be confused with Windows.System.DispatcherQueue
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace SudokuSolver;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public const string cFileExt = ".sdku";
    public const string cDisplayName = "Sudoku Solver";
    public const string cNewPuzzleName = "Untitled";

    public static bool IsPackaged { get; } = GetIsPackaged();

    private readonly DispatcherQueue uiThreadDispatcher;
    private readonly AppInstance appInstance;
    private readonly List<MainWindow> windowList = new List<MainWindow>();

    private bool appClosing = false;

    /// <summary>
    /// Initializes the singleton application object. This will be the single current
    /// instance, attempts to open more apps will already have been redirected.
    /// </summary>
    public App(AppInstance instance)
    {
        Debug.Assert(instance.IsCurrent);

        uiThreadDispatcher = DispatcherQueue.GetForCurrentThread();

        appInstance = instance;
        appInstance.Activated += MainInstance_Activated;

        InitializeComponent();
    }

    // Invoked on the ui thread when the application is launched normally
    protected async override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs _)
    {
        AppActivationArguments args = appInstance.GetActivatedEventArgs();

        if (args.Kind == ExtendedActivationKind.File)
        {
            ProcessFileActivation(args);
        }
        else if (args.Kind == ExtendedActivationKind.Launch)
        {
            if (IsPackaged)
                CreateNewWindow(storageFile: null);
            else
            {
                IList<string> commandLine = Environment.GetCommandLineArgs();

                bool windowCreated = await ProcessCommandLine(commandLine);

                if (!windowCreated)
                    CreateNewWindow(storageFile: null);
            }
        }
    }

    // Invoked when a redirection request is received.
    // Unlike OnLaunched(), this isn't called on the ui thread.
    private void MainInstance_Activated(object? sender, AppActivationArguments e)
    {
        bool success = uiThreadDispatcher.TryEnqueue(async () =>
        {
            if (!appClosing)
            {
                if (e.Kind == ExtendedActivationKind.File)
                    ProcessFileActivation(e);
                else if (e.Kind == ExtendedActivationKind.Launch)
                    await ProcessRedirectedLaunchActivation(e);
            }
        });

        Debug.Assert(success);
    }

    private void ProcessFileActivation(AppActivationArguments args)
    {
        if ((args.Data is IFileActivatedEventArgs fileData) && (fileData.Files.Count > 0))
        {
            foreach (IStorageItem storageItem in fileData.Files)
            {
                if (storageItem is StorageFile storageFile)
                    CreateNewWindow(storageFile);
            }
        }
    }

    private async Task ProcessRedirectedLaunchActivation(AppActivationArguments args)
    {
        if (args.Data is ILaunchActivatedEventArgs launchData)
        {
            IList<string> commandLine = SplitLaunchActivationCommandLine(launchData.Arguments);

            bool windowCreated = await ProcessCommandLine(commandLine);
                
            if (!windowCreated)
                TrySwitchToMainWindow();
        }
    }

    private async Task<bool> ProcessCommandLine(IList<string> args)
    {
        bool windowCreated = false;

        // args[0] is typically the path to the executing assembly
        for (int index = 1; index < args.Count; index++)
        {
            string arg = args[index];

            if (!string.IsNullOrEmpty(arg) && Path.GetExtension(arg).ToLower() == App.cFileExt && File.Exists(arg))
            {
                StorageFile storgeFile = await StorageFile.GetFileFromPathAsync(arg);
                CreateNewWindow(storgeFile);
                windowCreated = true;
            }
        }

        return windowCreated;
    }

    internal void CreateNewWindow(StorageFile? storageFile, MainWindow? creator = null)
    {
        if (!appClosing)
        {
            MainWindow window = new MainWindow(storageFile, creator);
            windowList.Add(window);
            TryBumpWindowToFront(window);
        }
    }

    private static bool TryBumpWindowToFront(Window window)
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        HWND target = (HWND)WindowNative.GetWindowHandle(window);

        if (target != foreground)
        {
            if (PInvoke.SetForegroundWindow(target))
                return true;

            Debug.WriteLine("SetForegroundWindow() was refused");
            return false;
        }

        return true;
    }

    internal bool UnRegisterWindow(MainWindow window)
    {
        appClosing = windowList.Count == 1;

        bool found = windowList.Remove(window);
        Debug.Assert(found);

        return appClosing;
    }

    private bool TrySwitchToMainWindow()
    {
        IntPtr targetWindow = Process.GetCurrentProcess().MainWindowHandle;
        Debug.Assert(targetWindow != IntPtr.Zero);

        foreach (MainWindow window in windowList)
        {
            if (targetWindow == WindowNative.GetWindowHandle(window))
            {
                if (window.WindowState == WindowState.Minimized) 
                    window.WindowState = WindowState.Normal;

                return TryBumpWindowToFront(window);
            }
        }

        return false;
    }

    internal RectInt32 AdjustNewWindowPosition(RectInt32 bounds)
    {
        static bool TitleBarOverlaps(PointInt32 a, PointInt32 b, int titleBarHeight)
        {
            RectInt32 aRect = new RectInt32(a.X, a.Y, titleBarHeight, titleBarHeight);
            RectInt32 bRect = new RectInt32(b.X, b.Y, titleBarHeight, titleBarHeight);
            return aRect.Intersects(bRect); 
        }

        const int cTitleBarHeight = 32;
        int index = 0;
        int resetCount = 0;
        PointInt32 newPos = bounds.TopLeft();
        
        while ((index < windowList.Count) && (resetCount < windowList.Count))
        {
            MainWindow existingWindow = windowList[index++];
            PointInt32 existingPos = existingWindow.RestoreBounds.TopLeft();
            double scaleFactor = existingWindow.GetScaleFactor();
            int clientTitleBarHeight = MainWindow.ConvertToDeviceSize(cTitleBarHeight, scaleFactor);

            newPos = AdjustWindowBoundsForDisplay(new RectInt32(newPos.X, newPos.Y, bounds.Width, bounds.Height)).TopLeft();

            if (TitleBarOverlaps(existingPos, newPos, clientTitleBarHeight))
            {
                newPos = existingPos.Offset(clientTitleBarHeight + 1);
                index = 0;
                ++resetCount;  // avoid an infinate loop if the position cannot be adjusted due to display limits
            }
        }

        return AdjustWindowBoundsForDisplay(new RectInt32(newPos.X, newPos.Y, bounds.Width, bounds.Height));
    }

    private static RectInt32 AdjustWindowBoundsForDisplay(RectInt32 bounds)
    {
        Debug.Assert(!bounds.IsEmpty());

        RectInt32 workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        PointInt32 position = bounds.TopLeft();

        if ((position.Y + bounds.Height) > workArea.Bottom())
            position.Y = workArea.Bottom() - bounds.Height;

        if (position.Y < workArea.Y)
            position.Y = workArea.Y;

        if ((position.X + bounds.Width) > workArea.Right())
            position.X = workArea.Right() - bounds.Width;

        if (position.X < workArea.X)
            position.X = workArea.X;

        int width = Math.Min(bounds.Width, workArea.Width);
        int height = Math.Min(bounds.Height, workArea.Height);

        return new RectInt32(position.X, position.Y, width, height);
    }

    private static bool GetIsPackaged()
    {
        uint length = 0;
        WIN32_ERROR error = PInvoke.GetCurrentPackageFullName(ref length, null);
        return error == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER;
    }

    // The command line is constructed by the os when a file is dragged 
    // and dropped onto the exe (or it's shortcut), so really should be well formed.
    private static IList<string> SplitLaunchActivationCommandLine(string commandLine)
    {
        List<string> arguments = new List<string>();
        StringBuilder sb = new StringBuilder();
        bool insideQuotes = false;

        foreach (char letter in commandLine)
        {
            if (letter == '"')
                insideQuotes = !insideQuotes;

            else if (insideQuotes || (letter != ' '))
                sb.Append(letter);

            else if (sb.Length > 0)
            {
                arguments.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            arguments.Add(sb.ToString());

        return arguments;
    }
}