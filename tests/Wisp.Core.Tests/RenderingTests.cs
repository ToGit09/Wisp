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
    public void Sanitizer_detects_remote_and_css_images()
    {
        Assert.True(HtmlSanitizer.Sanitize("<img src=\"https://x.example/photo.jpg\" width=300>").HasRemoteImages);
        Assert.True(HtmlSanitizer.Sanitize("<div style=\"background:url('https://x.example/a.png')\">x</div>").HasRemoteImages);
        Assert.False(HtmlSanitizer.Sanitize("<img src=\"cid:logo\">").HasRemoteImages);
    }


    [Fact]
    public void Tracking_pixels_are_removed_and_never_count_as_images_to_load()
    {
        var result = HtmlSanitizer.Sanitize("<p>Hallo</p><img src=\"https://track.example/p.gif\" width=\"1\" height=\"1\"><img src=\"https://t.example/q\" style=\"width:0;height:0\">");

        Assert.DoesNotContain("<img", result.Html);
        Assert.False(result.HasRemoteImages);
        // A spacer (1 px wide, but 20 px high) is layout and stays.
        Assert.Contains("<img", HtmlSanitizer.Sanitize("<img src=\"cid:s\" width=\"1\" height=\"20\">").Html);
    }

    [Theory]
    [InlineData("display:none")]
    [InlineData("display: none !important; font-size: 1px")]
    [InlineData("max-height:0;overflow:hidden")]
    [InlineData("opacity:0")]
    [InlineData("mso-hide:all")]
    public void Hidden_preview_text_is_removed(string style)
    {
        var raw = $"<html><body><div style=\"{style}\">Vorschau</div><p>Sichtbar</p></body></html>";

        var html = HtmlSanitizer.Sanitize(raw).Html;
        Assert.DoesNotContain("Vorschau", html);
        Assert.Contains("Sichtbar", html);

        // The text view (used when HTML display is off) leaves it out as well.
        var text = MailRenderer.ToPlainText(MailRenderer.FromHtml(raw, new LinkCollector()));
        Assert.Equal("Sichtbar", text);
    }

    [Fact]
    public void Font_size_zero_hides_only_text_that_inherits_it()
    {
        var html = HtmlSanitizer.Sanitize("<div style=\"font-size:0\">weg<span style=\"font-size:14px\">bleibt</span></div><p style=\"opacity:0.5\">halb</p>").Html;

        Assert.DoesNotContain("weg", html);
        Assert.Contains("bleibt", html);
        Assert.Contains("halb", html);
    }

    [Theory]
    [InlineData("background:#1a73e8", "background-color: #1a73e8")]
    [InlineData("background: url(https://x.example/a.png) no-repeat #f0f0f0", "background-color: #f0f0f0")]
    [InlineData("background: linear-gradient(#e66465, #9198e5)", "background-color: #e66465")]
    [InlineData("background: white !important", "background-color: white !important")]
    [InlineData("background: url(data:image/png;base64,AAAA) red", "background-color: red")]
    [InlineData("background-color: #fff", "background-color: #fff")]
    [InlineData("background: url(a.png) no-repeat", "")]
    [InlineData("border-radius: 6px", "border-radius: 6px; corner-radius: 6px")]
    [InlineData("border-radius: 4px 4px 0 0", "border-radius: 4px 4px 0 0; corner-radius: 4px")]
    [InlineData("border-radius: 50%", "border-radius: 50%")]
    [InlineData(".background:hover { color: red }", ".background:hover { color: red }")]
    public void Css_the_renderer_does_not_know_is_translated(string css, string expected)
    {
        Assert.Equal(expected, CssCompat.Rewrite(css));
    }

    [Fact]
    public void Body_styles_move_to_a_wrapper_and_its_background_to_the_sheet()
    {
        var result = HtmlSanitizer.Sanitize("<html><body bgcolor=\"f4f4f7\" style=\"margin:0;height:100%;font-family:Arial;color:#333\"><p>x</p></body></html>");

        Assert.Equal("#f4f4f7", result.Background);
        Assert.StartsWith("<div style=\"font-family:Arial;color:#333\"><p>x</p></div>", result.Html);
        Assert.Null(HtmlSanitizer.Sanitize("<p>x</p>").Background);
        Assert.Equal("#101010", HtmlSanitizer.Sanitize("<body style=\"background:#101010\">x</body>").Background);
    }

    [Fact]
    public void Fixed_width_block_with_auto_margins_is_centred()
    {
        var html = HtmlSanitizer.Sanitize("<div style=\"width:300px;margin:0 auto\">x</div>").Html;

        Assert.Matches("<td></td><td width=\"300\"><div style=\"width:300px;margin:0 auto\">x</div></td><td></td>", html);
    }
    [Fact]
    public void Sanitizer_strips_css_imports()
    {
        var result = HtmlSanitizer.Sanitize("<style>@import url(https://x.example/a.css); p{color:red}</style><p>a</p>");

        Assert.DoesNotContain("@import", result.Html);
        Assert.Contains("color:red", result.Html);
    }

    [Theory]
    [InlineData("color: rgba(0,0,0,0.87)", "color: rgba(0, 0, 0, 222)")]
    [InlineData("color:rgba(0, 0, 0, 0.54);", "color:rgba(0, 0, 0, 138);")]
    [InlineData("color: rgba(255,0,0,1)", "color: #ff0000")]
    [InlineData("color: rgb(10 20 30 / 50%)", "color: rgba(10, 20, 30, 128)")]
    [InlineData("color: rgb(100%, 0%, 0%)", "color: #ff0000")]
    [InlineData("color: hsl(120, 100%, 25%)", "color: #008000")]
    [InlineData("color: #00000080", "color: rgba(0, 0, 0, 128)")]
    [InlineData("color: #f00f", "color: #ff0000")]
    [InlineData("color: #1a73e8", "color: #1a73e8")]
    [InlineData("#abcd { color: red }", "#abcd { color: red }")]
    [InlineData("color: rgba(var(--x), 0.5)", "color: rgba(var(--x), 0.5)")]
    public void Css_colors_are_rewritten_for_the_renderer(string css, string expected)
    {
        Assert.Equal(expected, CssColors.Normalize(css));
    }

    [Theory]
    [InlineData("(max-width: 499px)", false)]
    [InlineData("only screen and (max-width: 500px)", false)]
    [InlineData("(min-width: 501px)", true)]
    [InlineData("screen", true)]
    [InlineData("print", false)]
    [InlineData("yahoo", false)]
    [InlineData("screen and (-webkit-min-device-pixel-ratio: 0) and (max-width: 499px)", false)]
    [InlineData("(prefers-color-scheme: dark)", false)]
    [InlineData("print, (min-width: 600px)", true)]
    [InlineData("not print", true)]
    public void Media_queries_are_evaluated_for_a_desktop_reader(string query, bool matches)
    {
        Assert.Equal(matches, CssMedia.Matches(query, CssMedia.DesktopWidth));
    }

    [Fact]
    public void Phone_rules_are_dropped_and_desktop_rules_kept()
    {
        var css = CssMedia.Resolve("""
            p { color: red }
            @media (max-width: 499px) { .content { width: 100% !important } }
            @media (min-width: 501px) { .button a { padding: 13px 40px } }
            @media screen { @font-face { font-family: X; src: url(x.woff) } }
            """);

        Assert.DoesNotContain("100%", css);
        Assert.Contains(".button a { padding: 13px 40px }", css);
        Assert.Contains("p { color: red }", css);
        Assert.DoesNotContain("@", css);
    }

    [Fact]
    public void Attributes_without_value_get_an_empty_one()
    {
        // The renderer reads <img alt width="128"> as alt="width" and the image loses its size.
        var html = HtmlSanitizer.Sanitize("<img src=\"cid:a\" alt width=\"128\">").Html;

        Assert.Contains("alt=\"\" width=\"128\"", html);
    }

    [Fact]
    public void Centred_fixed_width_table_is_wrapped_in_spacer_columns()
    {
        var html = HtmlSanitizer.Sanitize("""
            <table width="100%"><tr><td align="center"><table width="500" class="content"><tr><td>x</td></tr></table></td></tr></table>
            """).Html;

        Assert.Matches("<td></td><td width=\"500\"><table width=\"500\" class=\"content\">.*</table></td><td></td>", html);
    }

    [Fact]
    public void Percentage_heights_are_dropped_and_bottom_alignment_moves_to_the_outer_cell()
    {
        var html = HtmlSanitizer.Sanitize("""
            <table height="240" style="height: 240px"><tr><td class="bg">
              <table height="100%"><tr><td height="100%" style="vertical-align: bottom" valign="bottom">x</td></tr></table>
            </td></tr></table>
            """).Html;

        Assert.DoesNotContain("100%", html);
        var cell = System.Text.RegularExpressions.Regex.Match(html, "<td class=\"bg\"[^>]*>").Value;
        Assert.Contains("height=\"240\"", cell);
        Assert.Contains("valign=\"bottom\"", cell);
    }

    [Fact]
    public void Sanitizer_keeps_semi_transparent_text_visible()
    {
        var result = HtmlSanitizer.Sanitize("<div style=\"color: rgba(0,0,0,0.87)\">Neue Anmeldung</div>");

        Assert.Contains("rgba(0, 0, 0, 222)", result.Html);
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
