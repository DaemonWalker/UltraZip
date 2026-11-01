using System.IO;
using System.Windows;

namespace UltraZip.Views;

public partial class ArchiveCommentDialog : Window
{
    public ArchiveCommentDialog(string archivePath, string comment)
    {
        InitializeComponent();
        ArchivePathText.Text = Path.GetFileName(archivePath);
        ArchivePathText.ToolTip = archivePath;
        CommentText.Text = comment;
        EmptyText.Text = comment.Length == 0 ? "此压缩包没有注释。" : "只读注释，可选中文字复制。";
    }
}
