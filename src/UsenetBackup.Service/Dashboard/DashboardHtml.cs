using System.Reflection;
using System.Text;

namespace UsenetBackup.Service;

/// <summary>
/// Serves the dashboard single-page app. The HTML/CSS/JS lives in
/// Web/index.html as an embedded resource so it gets proper editor
/// support; the {{CSRF_TOKEN}} placeholder is replaced with the
/// per-startup token before serving.
/// </summary>
public static class DashboardHtml
{
    private static readonly string RawPage = LoadPage();

    public static string Page(string csrfToken) =>
        RawPage.Replace("{{CSRF_TOKEN}}", csrfToken, StringComparison.Ordinal);

    private static string LoadPage()
    {
        var asm = typeof(DashboardHtml).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "UsenetBackup.Service.Dashboard.Web.index.html")
            ?? throw new InvalidOperationException(
                "Embedded dashboard page is missing: Dashboard/Web/index.html.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
