using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MaaYys.App.Views;

/// <summary>
/// 日志截图卡片的大图预览（P5.5）：只读展示 + 另存为 PNG。
/// 缩略图在内存里是原始像素，存盘时才编码（与导出走同一条路）。
/// </summary>
public partial class LogImageView : Window
{
    private readonly BitmapSource _bitmap;

    public LogImageView(BitmapSource bitmap, string caption)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        InitializeComponent();
        _bitmap = bitmap;
        Shot.Source = bitmap;
        Caption.Text = $"{caption}  ·  {bitmap.PixelWidth}×{bitmap.PixelHeight}（点击「另存为」保存原图尺寸）";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"maayys-shot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using var stream = new FileStream(dialog.FileName, FileMode.Create);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "日志截图", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
