using System.Reflection;
using System.Text;

namespace FileKeep.Service;

/// <summary>
/// Serves the dashboard single-page app. The HTML/CSS/JS lives in
/// Web/index.html as an embedded resource so it gets proper editor
/// support; the {{CSRF_TOKEN}} placeholder is replaced with the
/// per-startup token before serving, and {{APP_VERSION}} with this
/// build's token, so the served page knows which build it belongs to
/// (the sidebar stale-page check compares it against the service's).
/// </summary>
public static class DashboardHtml
{
    private static readonly string RawPage = LoadPage();
    private static readonly string BuildToken = DashboardApi.GetBuildToken();

    public static string Page(string csrfToken) =>
        RawPage
            .Replace("{{CSRF_TOKEN}}", csrfToken, StringComparison.Ordinal)
            .Replace("{{APP_VERSION}}", BuildToken, StringComparison.Ordinal);

    private static string LoadPage()
    {
        var asm = typeof(DashboardHtml).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "FileKeep.Service.Dashboard.Web.index.html")
            ?? throw new InvalidOperationException(
                "Embedded dashboard page is missing: Dashboard/Web/index.html.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
