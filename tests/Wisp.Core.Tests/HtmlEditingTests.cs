using MimeKit;
using Wisp.Core.Accounts;
using Wisp.Core.Mail;

namespace Wisp.Core.Tests;

public class HtmlEditingTests
{
    [Fact]
    public void Line_breaks_count_like_in_a_text_box()
    {
        Assert.Equal("Hallo<br>\nWelt", HtmlEditing.SourceToHtml("Hallo\nWelt"));
    }

    [Fact]
    public void No_extra_breaks_around_block_elements()
    {
        var html = HtmlEditing.SourceToHtml("Liste:\n<ul>\n  <li>eins</li>\n  <li>zwei</li>\n</ul>\nEnde");

        Assert.Equal("Liste:\n<ul>\n  <li>eins</li>\n  <li>zwei</li>\n</ul>\nEnde", html);
    }

    [Fact]
    public void Plain_text_is_escaped_when_switching_to_advanced()
    {
        Assert.Equal("a &lt; b &amp; c\nzweite Zeile", HtmlEditing.TextToSource("a < b & c\r\nzweite Zeile"));
    }

    [Fact]
    public void Bold_wraps_the_selection_and_keeps_it_selected()
    {
        var r = HtmlEditing.Bold("Das ist wichtig.", 8, 15);

        Assert.Equal("Das ist <b>wichtig</b>.", r.Text);
        Assert.Equal("wichtig", r.Text[r.SelectionStart..r.SelectionEnd]);
    }

    [Fact]
    public void Bold_without_selection_puts_the_caret_between_the_tags()
    {
        var r = HtmlEditing.Bold("Text ", 5, 5);

        Assert.Equal("Text <b></b>", r.Text);
        Assert.Equal(8, r.SelectionStart);
    }

    [Fact]
    public void List_turns_each_selected_line_into_an_item()
    {
        var r = HtmlEditing.MakeList("Einkauf\nMilch\nBrot", 9, 15, numbered: false);

        Assert.Equal("Einkauf\n<ul>\n  <li>Milch</li>\n  <li>Brot</li>\n</ul>", r.Text);
    }

    [Theory]
    [InlineData("wisp.example", "https://wisp.example")]
    [InlineData("https://wisp.example/a", "https://wisp.example/a")]
    [InlineData("anna@example.org", "mailto:anna@example.org")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///etc/passwd", null)]
    public void Only_web_and_mail_links_are_accepted(string input, string? expected)
    {
        Assert.Equal(expected, HtmlEditing.NormalizeUrl(input));
    }

    [Fact]
    public void Link_attribute_is_escaped()
    {
        var r = HtmlEditing.Link("hier", 0, 4, "https://x.example/?a=1&b=\"2\"");

        Assert.Equal("<a href=\"https://x.example/?a=1&amp;b=&quot;2&quot;\">hier</a>", r.Text);
    }

    [Fact]
    public void Signature_goes_above_the_quote_with_the_standard_separator()
    {
        var body = Composer.WithSignature("\n\nAm Montag schrieb Anna:\n> Hallo", "Viele Grüße\r\nErika\n");

        Assert.Equal("\n\n-- \nViele Grüße\nErika\n\nAm Montag schrieb Anna:\n> Hallo", body);
        Assert.Equal("Text", Composer.WithSignature("Text", "  "));
    }

    [Fact]
    public void Html_mail_has_a_text_alternative_and_no_active_content()
    {
        var account = new AccountConfig
        {
            DisplayName = "Erika", Email = "erika@example.org", UserName = "erika@example.org",
            Imap = new ServerSettings("imap.example.org", 993, Accounts.Security.Tls),
            Smtp = new ServerSettings("smtp.example.org", 465, Accounts.Security.Tls),
        };
        var draft = new Draft
        {
            To = "bob@example.org", Subject = "Hallo",
            HtmlSource = "Hallo <b>Bob</b>\n<script>steal()</script><img src=x onerror=alert(1)>",
        };

        var message = Composer.Build(draft, account);

        Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Contains("<b>Bob</b>", message.HtmlBody);
        Assert.DoesNotContain("script", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hallo Bob", message.TextBody);
    }
}
