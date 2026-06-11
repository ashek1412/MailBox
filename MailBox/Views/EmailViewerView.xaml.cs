using MailBox.ViewModels;
using Microsoft.Web.WebView2.Core;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;

namespace MailBox.Views;

public partial class EmailViewerView : UserControl
{
    public EmailViewerView()
    {
        InitializeComponent();
        InitWebViewAsync();
        DataContextChanged += OnDataContextChanged;
    }

    private async void InitWebViewAsync()
    {
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: MailBox.Services.AppPaths.WebView2Dir);
            await BodyWebView.EnsureCoreWebView2Async(env);

            // Force light mode — overrides Windows dark-mode system setting
            BodyWebView.DefaultBackgroundColor = Color.White;
            BodyWebView.CoreWebView2.Profile.PreferredColorScheme =
                CoreWebView2PreferredColorScheme.Light;

            // Block scripts and XHR/Fetch from email HTML (prevents JS execution + tracking beacons)
            // Images, CSS, and fonts are intentionally allowed so email content renders correctly.
            BodyWebView.CoreWebView2.WebResourceRequested += (s, e) =>
            {
                e.Response = BodyWebView.CoreWebView2.Environment
                    .CreateWebResourceResponse(null, 204, "No Content", "");
            };
            BodyWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
            BodyWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
            BodyWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
        }
        catch (Exception ex)
        {
            // WebView2 init failed (runtime missing, or user-data folder locked by
            // another MailBox instance) — tell the user instead of showing a blank body.
            BodyWebView.Visibility = Visibility.Collapsed;
            WebViewErrorText.Text =
                "Email body cannot be displayed — the embedded browser (WebView2) failed to start.\n\n" +
                "If another copy of MailBox is running, close it and reopen this one. " +
                "Otherwise install the Microsoft WebView2 Runtime.\n\nDetails: " + ex.Message;
            WebViewErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EmailViewerViewModel vm)
            LoadHtml(vm.SanitizedHtml);
    }

    private void LoadHtml(string html)
    {
        var content = string.IsNullOrEmpty(html)
            ? "<html><body style='font-family:Segoe UI,sans-serif;font-size:14px;color:#374151;padding:20px;background:#fff'>No content</body></html>"
            : InjectLightMode(html);

        if (BodyWebView.CoreWebView2 != null)
            BodyWebView.NavigateToString(content);
        else
            BodyWebView.CoreWebView2InitializationCompleted += (s, ev) =>
            {
                if (ev.IsSuccess) LoadHtml(html);
            };
    }

    /// <summary>
    /// Injects a light-mode CSS reset into the HTML so it always renders on a white background
    /// regardless of Windows dark mode or any dark styling in the email itself.
    /// </summary>
    private static string InjectLightMode(string html)
    {
        const string css = """
            <meta name="color-scheme" content="light only">
            <style>
              :root { color-scheme: light !important; }
              html, body {
                background-color: #ffffff !important;
                color: #1f2937;
              }
            </style>
            """;

        // Inject into existing <head> … </head>
        var headOpen = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headOpen >= 0)
            return html.Insert(headOpen + 6, css);

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose >= 0)
            return html.Insert(headClose, css);

        // Full document but no <head> — inject after <html ...>
        var htmlTag = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlTag >= 0)
        {
            var tagEnd = html.IndexOf('>', htmlTag);
            if (tagEnd >= 0)
                return html.Insert(tagEnd + 1, $"<head>{css}</head>");
        }

        // Plain fragment — wrap it
        return $"""
            <html><head>{css}</head>
            <body style="font-family:Segoe UI,sans-serif;font-size:14px;padding:20px">
            {html}
            </body></html>
            """;
    }

    // Block navigation away from the email (e.g., clicking links opens system browser)
    private void WebView_NavigationStarting(object sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("about:") && !e.Uri.StartsWith("data:"))
        {
            e.Cancel = true;
            // Open external links in the default browser
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
    }
}
