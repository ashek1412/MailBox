using MailBox.ViewModels;
using System.Drawing;
using System.Windows;

namespace MailBox.Views;

public partial class ComposeWindow : Window
{
    private bool _editorReady = false;

    public ComposeWindow(ComposeViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.SendSucceeded += () => Application.Current.Dispatcher.InvokeAsync(Close);
        vm.DraftSaved    += () => Application.Current.Dispatcher.InvokeAsync(Close);
        InitEditorAsync(vm.Body);
    }

    private async void InitEditorAsync(string initialBody)
    {
        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: MailBox.Services.AppPaths.WebView2Dir);
        await BodyEditor.EnsureCoreWebView2Async(env);
        _editorReady = true;
        BodyEditor.DefaultBackgroundColor = Color.White;

        // Load a minimal contenteditable HTML page as the compose body
        var html = $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              * { box-sizing: border-box; margin: 0; padding: 0; }
              body { font-family: 'Segoe UI', sans-serif; font-size: 14px;
                     color: #374151; padding: 16px; min-height: 100vh;
                     outline: none; }
              #editor { outline: none; min-height: calc(100vh - 32px); }
              #editor:empty:before { content: 'Write your message…';
                                     color: #9CA3AF; pointer-events: none; }
              blockquote, [style*="border-left"] { margin: 8px 0; }
            </style>
            </head>
            <body>
              <div id="editor" contenteditable="true">{{initialBody}}</div>
              <script>
                const editor = document.getElementById('editor');
                editor.addEventListener('input', () => {
                    window.chrome.webview.postMessage({ type: 'bodyChanged', html: editor.innerHTML });
                });
                // Place cursor at start
                const range = document.createRange();
                const sel = window.getSelection();
                range.setStart(editor, 0);
                range.collapse(true);
                sel.removeAllRanges();
                sel.addRange(range);
                editor.focus();
              </script>
            </body>
            </html>
            """;

        BodyEditor.NavigateToString(html);

        // Listen for body changes from the editor
        BodyEditor.CoreWebView2.WebMessageReceived += (s, e) =>
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.GetProperty("type").GetString() == "bodyChanged" &&
                    DataContext is ComposeViewModel vm)
                    vm.Body = doc.RootElement.GetProperty("html").GetString() ?? "";
            }
            catch { }
        };
    }

    private async void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (BodyEditor.CoreWebView2 == null) return;
        if (sender is FrameworkElement el && el.Tag is string cmd)
            await BodyEditor.CoreWebView2.ExecuteScriptAsync(
                $"document.execCommand('{cmd}', false, null); document.getElementById('editor').focus();");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (DataContext is ComposeViewModel vm)
            vm.SearchRecipientsCommand.Execute(((System.Windows.Controls.TextBox)sender).Text);
    }
}
