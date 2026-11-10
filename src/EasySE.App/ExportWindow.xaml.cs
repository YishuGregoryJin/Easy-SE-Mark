using System.ComponentModel;
using System.IO;
using System.Windows;
using EasySE.Core;
using EasySE.Media;
using Microsoft.Win32;

namespace EasySE.App;

public partial class ExportWindow : Window
{
    private readonly Project project;
    private readonly string? source;
    private CancellationTokenSource? cancellation;
    private bool running, closeRequested;
    public ExportResult? Result { get; private set; }
    public ExportWindow(Project project, string? source)
    {
        InitializeComponent(); this.project = project; this.source = source;
        ResolutionText.Text = $"{project.Video.Width} × {project.Video.Height}";
        FileName.Text = ExportPolicy.SafeName(project.Title, ".mp4");
        DirectoryPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
    private void DirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择导出目录", Multiselect = false };
        if (Directory.Exists(DirectoryPath.Text.Trim())) dialog.InitialDirectory = DirectoryPath.Text.Trim();
        if (dialog.ShowDialog(this) == true) DirectoryPath.Text = dialog.FolderName;
    }
    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (running) return;
        var directory = DirectoryPath.Text.Trim();
        if (!Directory.Exists(directory)) { Stage.Text = "目标目录不存在"; return; }
        running = true; closeRequested = false; cancellation = new();
        FileName.IsEnabled = DirectoryPath.IsEnabled = DirectoryButton.IsEnabled = StartButton.IsEnabled = false;
        Stage.Text = "准备"; Progress.Value = 0; Progress.Visibility = Visibility.Visible; CancelButton.IsEnabled = true;
        var progress = new Progress<ExportProgress>(p => { Stage.Text = p.Stage; Progress.Value = p.Percent; Percent.Text = $"{p.Percent:0}%"; });
        try
        {
            using var renderer = new ExportRenderer(project);
            Result = await VideoExporter.RunAsync(new(project, source, directory, FileName.Text.Trim(), renderer.Compose)
                { NeedsComposition = renderer.NeedsComposition }, progress, cancellation.Token);
            running = false; DialogResult = true;
        }
        catch (OperationCanceledException) { Stage.Text = "已取消"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Stage.Text = ex.Message + (ex.Data["EasySE.ExportLog"] is string log ? "\n日志：" + log : "");
            Stage.TextWrapping = TextWrapping.Wrap; Stage.ToolTip = Stage.Text;
        }
        finally
        {
            running = false; cancellation.Dispose(); cancellation = null;
            FileName.IsEnabled = DirectoryPath.IsEnabled = DirectoryButton.IsEnabled = StartButton.IsEnabled = CancelButton.IsEnabled = true;
            if (closeRequested && IsVisible) Close();
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (running) { cancellation?.Cancel(); CancelButton.IsEnabled = false; Stage.Text = "取消中"; }
        else Close();
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!running) return;
        e.Cancel = true; closeRequested = true; cancellation?.Cancel(); CancelButton.IsEnabled = false; Stage.Text = "取消中";
    }
}
