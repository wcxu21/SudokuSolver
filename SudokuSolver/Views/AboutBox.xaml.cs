﻿using Sudoku.Utils;

namespace Sudoku.Views;

public sealed partial class AboutBox : ContentDialog
{
    public AboutBox()
    {
        this.InitializeComponent();

        VersionTextBlock.Text = string.Format(VersionTextBlock.Text, typeof(App).Assembly.GetName().Version);
        RequestedTheme = ThemeHelper.Instance.CurrentTheme;
        LoadWindowIconImage(RequestedTheme);
    }

    private async void LoadWindowIconImage(ElementTheme theme)
    {
        string path = theme == ElementTheme.Light ? "Sudoku.Resources.about_light.png" : "Sudoku.Resources.about_dark.png";
        AboutImage.Source = await MainWindow.LoadEmbeddedImageResource(path);
    }
}
