using System.Text;
using Wisp.Core.Rendering;
using Wisp.Core.Security;

namespace Wisp.Core.Tests;

public class RenderingTests
{
    [Fact]
    public void Sanitizer_removes_scripts_handlers_and_dangerous_links()
    {
        var result = HtmlSanitizer.Sanitize("""
            <html><body>
              <script>alert(1)</script>
              <p onclick="steal()">Hallo</p>
              <a href="javascript:evil()">x</a>
              <iframe src="https://evil.example"></iframe>
              <form action="https://evil.example"><input name="pw"></form>
            </body></html>
            """);

        Assert.DoesNotContain("script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hallo", result.Html);
    }

    [Fact]
    public void Sanitizer_detects_tracking_pixels_and_css_images()
    {
        Assert.True(HtmlSanitizer.Sanitize("<img src=\"https://track.example/p.gif\" width=1 height=1>").HasRemoteImages);
        Assert.True(HtmlSanitizer.Sanitize("<div style=\"background:url('https://x.example/a.png')\">x</div>").HasRemoteImages);
        Assert.False(HtmlSanitizer.Sanitize("<img src=\"cid:logo\">").HasRemoteImages);
    }

    [Fact]
    public void Sanitizer_strips_css_imports()
    {
        var result = HtmlSanitizer.Sanitize("<style>@import url(https://x.example/a.css); p{color:red}</style><p>a</p>");

        Assert.DoesNotContain("@import", result.Html);
        Assert.Contains("color:red", result.Html);
    }

    [Theory]
    [InlineData("www.meine-bank.de", "https://meine-bank.de.evil.example/login", true)]
    [InlineData("https://www.paypal.com", "https://paypal.com.secure-login.example", true)]
    [InlineData("paypal.com", "https://www.paypal.com/signin", false)]
    [InlineData("Jetzt ansehen", "https://shop.example/angebot", false)]
    [InlineData("bbc.co.uk", "https://www.bbc.co.uk/news", false)]
    public void Phishing_links_are_flagged(string text, string url, bool suspicious)
    {
        Assert.Equal(suspicious, LinkCollector.IsSuspicious(text, new Uri(url)));
    }

    [Fact]
    public void Phishing_link_in_html_mail_is_flagged()
    {
        var raw = Encoding.UTF8.GetBytes(
            "From: a@example.org\r\nSubject: Test\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" +
            "<p>Details in Ihrem <a href='https://kundenkonto.evil.example/login'>www.stadtwerke.example</a>.</p>");

        var mail = MailRenderer.Render(raw, new RenderOptions(false, false));

        Assert.True(Assert.Single(mail.Links).Suspicious);
    }

    [Fact]
    public void Html_and_images_are_off_unless_enabled()
    {
        var raw = Encoding.UTF8.GetBytes(
            "From: a@example.org\r\nTo: b@example.org\r\nSubject: Test\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n\r\n<p>Hallo <b>Welt</b><img src=\"https://t.example/x.gif\"></p>");

        var plain = MailRenderer.Render(raw, new RenderOptions(ShowHtml: false, LoadRemoteImages: false));
        Assert.Null(plain.Html);
        Assert.True(plain.HasHtml);
        Assert.True(plain.HasRemoteImages);
        Assert.Contains(plain.Blocks.SelectMany(b => b.Spans), s => s.Text.Contains("Welt") && s.Bold);

        var html = MailRenderer.Render(raw, new RenderOptions(ShowHtml: true, LoadRemoteImages: false));
        Assert.NotNull(html.Html);
        Assert.False(html.RemoteImagesAllowed);
    }

    [Fact]
    public void Attachment_names_cannot_escape_the_target_folder()
    {
        Assert.Equal("passwd", MailRenderer.SafeFileName("../../etc/passwd"));
        Assert.Equal("evil.exe", MailRenderer.SafeFileName("C:\\Windows\\evil.exe"));
        Assert.Equal("Anhang", MailRenderer.SafeFileName(".."));
    }

    [Fact]
    public void Vault_with_master_password_rejects_wrong_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wisp-vault-{Guid.NewGuid():N}.json");
        try
        {
            var vault = new Vault(path);
            var key = vault.Create(VaultProtection.MasterPassword, "richtig horse battery");

            Assert.Equal(key, vault.UnlockWithPassword("richtig horse battery"));
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => vault.UnlockWithPassword("falsch"));
            Assert.DoesNotContain(Convert.ToBase64String(key), File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
