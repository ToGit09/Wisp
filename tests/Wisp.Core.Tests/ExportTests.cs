using System.Text;
using MimeKit;
using Wisp.Core.Mail;

namespace Wisp.Core.Tests;

public class ExportTests
{
    static byte[] Sample()
    {
        var builder = new BodyBuilder
        {
            TextBody = "Hallo Tobias,\r\n\r\nschöne Grüße aus Köln.",
            HtmlBody = """
                <html><body bgcolor="#fafafa"><script>alert(1)</script><p>Hallo <b>Tobias</b>,</p>
                <img src="cid:logo"><img src="https://track.example/photo.jpg" width="300">
                <div style="background-image:url('https://track.example/bg.png')">schöne Grüße aus Köln.</div></body></html>
                """,
        };
        var logo = builder.LinkedResources.Add("logo.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
        logo.ContentId = "logo";
        builder.Attachments.Add("Rechnung.pdf", [1, 2, 3]);
        var message = new MimeMessage { Subject = "Grüße: <Köln>", Body = builder.ToMessageBody(), Date = new DateTimeOffset(2026, 11, 9, 10, 12, 0, TimeSpan.Zero) };
        message.From.Add(new MailboxAddress("Anna Schmidt", "anna@example.com"));
        message.To.Add(new MailboxAddress("Tobias", "tobias@example.com"));
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }

    static async Task<string> ExportAsync(ExportFormat format, bool keepRemoteImages = false)
    {
        using var target = new MemoryStream();
        await MailExporter.WriteAsync(Sample(), format, target, keepRemoteImages, CancellationToken.None);
        return Encoding.UTF8.GetString(target.ToArray());
    }

    [Fact]
    public async Task Eml_is_the_original_byte_for_byte()
    {
        var raw = Sample();
        using var target = new MemoryStream();
        await MailExporter.WriteAsync(raw, ExportFormat.Eml, target, keepRemoteImages: false, CancellationToken.None);

        Assert.Equal(raw, target.ToArray());
    }

    [Fact]
    public async Task Text_has_header_lines_and_the_readable_text()
    {
        var text = await ExportAsync(ExportFormat.Text);

        Assert.Contains("Von: \"Anna Schmidt\" <anna@example.com>", text);
        Assert.Contains("Betreff: Grüße: <Köln>", text);
        Assert.Contains("Anhänge: Rechnung.pdf", text);
        Assert.Contains("schöne Grüße aus Köln.", text);
    }

    [Fact]
    public async Task Html_is_safe_and_self_contained()
    {
        var html = await ExportAsync(ExportFormat.Html);

        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>Gr&#252;&#223;e: &lt;K&#246;ln&gt;</title>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("src=\"data:image/png;base64,", html); // the image inside the mail is embedded
        Assert.DoesNotContain("track.example", html); // nothing is fetched when the file is opened
        Assert.Contains("background-color:#fafafa", html);
    }

    [Fact]
    public async Task Html_keeps_remote_images_only_if_the_user_loads_them_anyway()
    {
        Assert.Contains("https://track.example/photo.jpg", await ExportAsync(ExportFormat.Html, keepRemoteImages: true));
    }

    [Theory]
    [InlineData("a.eml", ExportFormat.Eml)]
    [InlineData("a.HTML", ExportFormat.Html)]
    [InlineData("a.htm", ExportFormat.Html)]
    [InlineData("a.txt", ExportFormat.Text)]
    [InlineData("a", ExportFormat.Eml)]
    public void Format_follows_the_file_name(string name, ExportFormat expected)
    {
        Assert.Equal(expected, MailExporter.FormatOf(name));
    }

    [Fact]
    public void File_names_are_valid_sortable_and_never_collide()
    {
        var date = new DateTimeOffset(2026, 11, 9, 10, 12, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 9, 10, 12, 0)));
        var name = MailExporter.FileName(date, "AW: Rechnung 10/2026 <wichtig>?", ExportFormat.Eml);

        Assert.Equal("2026-11-09 1012 AW Rechnung 10-2026 wichtig.eml", name);
        Assert.Equal("2026-11-09 1012 Ohne Betreff.txt", MailExporter.FileName(date, " ", ExportFormat.Text));
        Assert.True(MailExporter.FileName(date, new string('x', 300), ExportFormat.Eml).Length < 110);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.eml", MailExporter.Unique("a.eml", taken));
        Assert.Equal("A (2).eml", MailExporter.Unique("A.eml", taken));
        Assert.Equal("a (3).eml", MailExporter.Unique("a.eml", taken));
    }
}
