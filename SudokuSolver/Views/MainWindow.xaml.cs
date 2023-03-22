﻿using SudokuSolver.Utilities;
using SudokuSolver.ViewModels;

namespace SudokuSolver.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
internal sealed partial class MainWindow : SubClassWindow
{
    private enum Error { Success, Failure }
    private enum Status { Cancelled, Continue }
    public WindowViewModel ViewModel { get; private set; }

    private readonly PrintHelper printHelper;

    private StorageFile? sourceFile;
    private bool processingClose = false;
    private AboutBox? aboutBox = null;
    private ErrorDialog? errorDialog = null;

    public MainWindow(StorageFile? storagefile, MainWindow? creator)
    {
        InitializeComponent();

        // each window needs a local copy of the common view settings
        Settings.PerViewSettings viewSettings = Settings.Data.ViewSettings.Clone();

        // acrylic also works, but isn't recommended according to the UI guidelines
        if (!TrySetMicaBackdrop(viewSettings.Theme))
        {
            layoutRoot.Loaded += (s, e) =>
            {
                // the visual states won't exist until after OnApplyTemplate() has completed
                bool stateFound = VisualStateManager.GoToState(layoutRoot, "BackdropNotSupported", false);
                Debug.Assert(stateFound);
            };
        }

        ViewModel = new WindowViewModel(viewSettings);
        Puzzle.ViewModel = new PuzzleViewModel(viewSettings);
        Puzzle.ViewModel.PropertyChanged += ViewModel_PropertyChanged;  // used to update the window title

        appWindow.Closing += async (s, args) =>
        {
            // the await call creates a continuation routine, so must cancel the close here
            // and handle the actual closing explicitly when the continuation routine runs...
            args.Cancel = true;
            await HandleWindowClosing();
        };

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            CustomTitleBar.ParentAppWindow = appWindow;
            CustomTitleBar.UpdateThemeAndTransparency(viewSettings.Theme);
            Activated += CustomTitleBar.ParentWindow_Activated;
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;

            // the last event received will have the correct dimensions
            layoutRoot.SizeChanged += (s, a) => SetWindowDragRegions();
            Menu.SizeChanged += (s, a) => SetWindowDragRegions();
            Puzzle.SizeChanged += (s, a) => SetWindowDragRegions();

            Menu.Loaded += (s, a) => SetWindowDragRegions();
            Puzzle.Loaded += (s, a) => SetWindowDragRegions();

            // the drag regions need to be adjusted for menu fly outs
            FileMenuItem.Loaded += (s, a) => ClearWindowDragRegions();
            ViewMenuItem.Loaded += (s, a) => ClearWindowDragRegions();
            FileMenuItem.Unloaded += (s, a) => SetWindowDragRegions();
            ViewMenuItem.Unloaded += (s, a) => SetWindowDragRegions();
        }
        else
        {
            CustomTitleBar.Visibility = Visibility.Collapsed;
        }

        // always set the window icon, it's used in the task switcher
        SetWindowIconFromAppIcon();
        UpdateWindowTitle();

        printHelper = new PrintHelper(this);

        if (creator is not null)
            appWindow.MoveAndResize(App.Instance.GetNewWindowPosition(creator.RestoreBounds));
        else
            appWindow.MoveAndResize(App.Instance.GetNewWindowPosition(this));

        if (Settings.Data.WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        else
            WindowState = Settings.Data.WindowState;

       
        layoutRoot.Loaded += async (s, e) =>
        {
            layoutRoot.RequestedTheme = viewSettings.Theme;
            
            // set the duration for the next theme transition
            Puzzle.BackgroundBrushTransition.Duration = new TimeSpan(0, 0, 0, 0, 250);

            if (storagefile is not null)
            {
                Error error = await OpenFile(storagefile);

                if (error == Error.Success)
                    SourceFile = storagefile;
            }
        };
    }

    private async Task HandleWindowClosing()
    {
        // This is called from the File menu's close click handler and
        // also the AppWindow.Closing event handler. 

        Status status = Status.Continue;

        if (!processingClose)  // the first user attempt to close, a second will always succeed
        {
            processingClose = true;

            // cannot have more than one content dialog open at the same time
            aboutBox?.Hide();
            errorDialog?.Hide();

            status = await SaveExistingFirst();
        }

        if (status == Status.Cancelled)  // the save existing prompt was cancelled
        {
            processingClose = false;
        }
        else
        {
            bool lastWindow = App.Instance.UnRegisterWindow(this);

            if (lastWindow)
            {
                Settings.Data.RestoreBounds = RestoreBounds;
                Settings.Data.WindowState = WindowState;
                await Settings.Data.Save();
            }

            // calling Close() doesn't raise an AppWindow.Closing event
            Close();
        }
    }

    private async void NewClickHandler(object sender, RoutedEventArgs e)
    {
        Status status = await SaveExistingFirst();

        if (status != Status.Cancelled)
        {
            Puzzle.ViewModel!.New();
            SourceFile = null;
        }
    }

    private async void OpenClickHandler(object sender, RoutedEventArgs e)
    {
        Status status = await SaveExistingFirst();

        if (status != Status.Cancelled)
        {
            FileOpenPicker openPicker = new FileOpenPicker();
            InitializeWithWindow.Initialize(openPicker, WindowPtr);
            openPicker.FileTypeFilter.Add(App.cFileExt);

            StorageFile file = await openPicker.PickSingleFileAsync();

            if (file is not null)
            {
                Error error = await OpenFile(file);

                if (error == Error.Success)
                    SourceFile = file;
            }
        }
    }

    private void NewWindowClickHandler(object sender, RoutedEventArgs e)
    {
        App.Instance.CreateNewWindow(storageFile: null, creator: this);
    }

    private async void SaveClickHandler(object sender, RoutedEventArgs e)
    {
        await Save();
    }

    private async void SaveAsClickHandler(object sender, RoutedEventArgs e)
    {
        await SaveAs();
    }

    private async void PrintClickHandler(object sender, RoutedEventArgs e)
    {
        try
        {
            PuzzleView printView = new PuzzleView
            {
                IsPrintView = true,
                ViewModel = Puzzle.ViewModel,
            };

            await printHelper.PrintViewAsync(PrintCanvas, printView, SourceFile, Settings.Data.PrintSettings.Clone());
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("A printing error occurred.", ex.Message);
        }
    }

    private async void CloseClickHandler(object sender, RoutedEventArgs e)
    {
        await HandleWindowClosing();
    }

    public static bool IsPrintingAvailable => PrintManager.IsSupported();

    private async Task<Error> OpenFile(StorageFile file)
    {
        Error error = Error.Failure;

        try
        {
            using (Stream stream = await file.OpenStreamForReadAsync())
            {
                Puzzle.ViewModel!.Open(stream);
                error = Error.Success;
            }
        }
        catch (Exception ex)
        {
            string heading = $"An error occurred when opening {file.DisplayName}.";
            await ShowErrorDialog(heading, ex.Message);
        }

        return error;
    }

    private async Task<Status> SaveExistingFirst()
    {
        Status status = Status.Continue;

        if (Puzzle.ViewModel!.Modified)
        {
            string path;

            if (SourceFile is null)
                path = App.cNewPuzzleName;
            else
                path = SourceFile.Path;

            ContentDialogResult result = await new ConfirmSaveDialog(path, Content.XamlRoot, layoutRoot.ActualTheme).ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                status = await Save();  // if it's a new file, the Save As picker could be cancelled
            }
            else if (result == ContentDialogResult.None)
            {
                status = Status.Cancelled;
            }
        }

        return status;
    }

    private async Task SaveFile(StorageFile file)
    {
        using (StorageStreamTransaction transaction = await file.OpenTransactedWriteAsync())
        {
            using (Stream stream = transaction.Stream.AsStreamForWrite())
            {
                Puzzle.ViewModel?.Save(stream);

                // delete any existing file data beyond the end of the stream
                transaction.Stream.Size = transaction.Stream.Position;

                await transaction.CommitAsync();
            }
        }
    }

    private async Task<Status> Save()
    {
        Status status = Status.Continue;

        if (SourceFile is not null)
        {
            try
            {
                await SaveFile(SourceFile);
            }
            catch (Exception ex)
            {
                string heading = $"An error occurred when saving {SourceFile.DisplayName}.";
                await ShowErrorDialog(heading, ex.Message);
            }
        }
        else
            status = await SaveAs();

        return status;
    }

    private async Task<Status> SaveAs()
    {
        Status status = Status.Cancelled;
        FileSavePicker savePicker = new FileSavePicker();
        InitializeWithWindow.Initialize(savePicker, WindowPtr);

        savePicker.FileTypeChoices.Add("Sudoku files", new List<string>() { App.cFileExt });

        if (SourceFile is null)
            savePicker.SuggestedFileName = App.cNewPuzzleName;
        else
            savePicker.SuggestedFileName = SourceFile.DisplayName;

        StorageFile file = await savePicker.PickSaveFileAsync();

        if (file is not null)
        {
            try
            {
                await SaveFile(file);
                SourceFile = file;
                status = Status.Continue;
            }
            catch (Exception ex)
            {
                string heading = $"An error occurred when saving {file.DisplayName}.";
                await ShowErrorDialog(heading, ex.Message);
            }
        }

        return status; 
    }

    private async void AboutClickHandler(object sender, RoutedEventArgs e)
    {
        aboutBox ??= new AboutBox(Content.XamlRoot);
        aboutBox.RequestedTheme = layoutRoot.ActualTheme;
        await aboutBox.ShowAsync();
    }

    private async Task ShowErrorDialog(string message, string details)
    {
        errorDialog ??= new ErrorDialog(Content.XamlRoot);
        errorDialog.RequestedTheme = layoutRoot.ActualTheme;
        errorDialog.Message = message;
        errorDialog.Details = details;
        await errorDialog.ShowAsync();
    }

    private StorageFile? SourceFile
    {
        get => sourceFile;
        set
        {
            if (sourceFile != value)
            {
                sourceFile = value;
                UpdateWindowTitle();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Puzzle.ViewModel.Modified))
            UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        string filePart = SourceFile is null ? App.cNewPuzzleName : SourceFile.DisplayName;
        string modified = Puzzle.ViewModel!.Modified ? "*" : string.Empty;
        string title;

        if (layoutRoot.FlowDirection == FlowDirection.LeftToRight)
            title = $"{App.cDisplayName} - {filePart}{modified}";
        else
            title = $"{modified}{filePart} - {App.cDisplayName}";

        if (AppWindowTitleBar.IsCustomizationSupported())
            CustomTitleBar.Title = title;
        else
            Title = title;

        // the app window's title is used in the task switcher
        appWindow.Title = title;
    }

    private void ClearWindowDragRegions()
    {
        Debug.Assert(AppWindowTitleBar.IsCustomizationSupported());
        Debug.Assert(appWindow.TitleBar.ExtendsContentIntoTitleBar);

        // clear all drag rectangles to allow mouse interaction with menu fly outs,  
        // including clicks anywhere in the window used to dismiss the menu
        appWindow.TitleBar.SetDragRectangles(new[] { new RectInt32(0, 0, 0, 0) });
    }

    private void SetWindowDragRegions()
    {
        if (Menu.IsLoaded && Puzzle.IsLoaded)
        {
            Debug.Assert(AppWindowTitleBar.IsCustomizationSupported());
            Debug.Assert(appWindow.TitleBar.ExtendsContentIntoTitleBar);

            double scale = Puzzle.XamlRoot.RasterizationScale;

            RectInt32 windowRect = new RectInt32(0, 0, appWindow.ClientSize.Width, appWindow.ClientSize.Height);
            RectInt32 menuRect = ScaledRect(Menu.ActualOffset, Menu.ActualSize, scale);
            RectInt32 puzzleRect = ScaledRect(Puzzle.ActualOffset, Puzzle.ActualSize, scale);

            SimpleRegion region = new SimpleRegion(windowRect);
            region.Subtract(menuRect);
            region.Subtract(puzzleRect);

            appWindow.TitleBar.SetDragRectangles(region.ToArray());
        }
    }

    private static RectInt32 ScaledRect(Vector3 location, Vector2 size, double scale)
    {
        return ScaledRect(location.X, location.Y, size.X, size.Y, scale);
    }

    private static RectInt32 ScaledRect(double x, double y, double width, double height, double scale)
    {
        return new RectInt32(Convert.ToInt32(x * scale), 
                                Convert.ToInt32(y * scale), 
                                Convert.ToInt32(width * scale), 
                                Convert.ToInt32(height * scale));
    }
}
